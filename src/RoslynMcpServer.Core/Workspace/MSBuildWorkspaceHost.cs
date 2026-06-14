using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Core.Workspace;

/// <summary>
/// Production-grade host: loads .sln/.csproj via <see cref="MSBuildWorkspace"/>,
/// resolving all PackageReference, ProjectReference, and TargetFramework
/// metadata through MSBuild. This gives every tool access to the real semantic
/// model — external NuGet symbols resolve correctly, cross-project references
/// work, and find_references searches the entire solution.
///
/// For a single .cs file not part of any project, it falls back to an ad-hoc
/// compilation (like <see cref="AdhocWorkspaceHost"/>).
/// </summary>
public sealed class MSBuildWorkspaceHost : IWorkspaceHost, IDisposable
{
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _loadedPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // Single-file ad-hoc fallback (for .cs files outside any loaded project).
    private readonly AdhocWorkspaceHost _adhoc = new();
    private static bool _msbuildRegistered;

    public MSBuildWorkspaceHost()
    {
        EnsureMSBuildRegistered(); // register once at construction, before any LoadAsync
    }

    public Microsoft.CodeAnalysis.Workspace? Workspace => _workspace;
    public Solution? CurrentSolution => _solution;
    public bool IsReady => Volatile.Read(ref _msbuildRegistered) && _solution != null;

    /// <summary>
    /// Initialises MSBuild Locator (once per process) and loads the solution or
    /// project at <paramref name="path"/>. Safe to call multiple times —
    /// reloads pick up file changes.
    /// </summary>
    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            EnsureMSBuildRegistered();

            var abs = Path.GetFullPath(path);
            var ext = Path.GetExtension(abs);

            // Dispose previous workspace if reloading.
            _workspace?.Dispose();
            _workspace = CreateWorkspace();

            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                _solution = await _workspace.OpenSolutionAsync(abs, cancellationToken: ct);
            }
            else if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await _workspace.OpenProjectAsync(abs, cancellationToken: ct);
                _solution = _workspace.CurrentSolution;
            }
            else
            {
                throw new ArgumentException(
                    $"Expected .sln or .csproj, got: {abs}");
            }

            _loadedPath = abs;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<Compilation?> GetCompilationAsync(string path, CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(path);
        var ext = Path.GetExtension(abs);

        // Single .cs file: try to find it in a loaded project; fall back to adhoc.
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var doc = await GetDocumentAsync(abs, ct);
            if (doc != null)
            {
                return await doc.Project.GetCompilationAsync(ct);
            }

            // Not in a loaded project — use adhoc compilation.
            return await _adhoc.GetCompilationAsync(abs, ct);
        }

        // .csproj/.sln: reload if needed and return the compilation.
        if (_solution == null || _loadedPath == null)
        {
            await LoadAsync(abs, ct);
        }

        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = FindProject(abs);
            return project != null ? await project.GetCompilationAsync(ct) : null;
        }

        // .sln: return the first project's compilation (diagnostics tool usually
        // wants per-project; this is a fallback).
        var firstProject = _solution?.Projects.FirstOrDefault();
        return firstProject != null ? await firstProject.GetCompilationAsync(ct) : null;
    }

    public Task<Document?> GetDocumentAsync(string filePath, CancellationToken ct = default)
    {
        if (_solution == null)
            return Task.FromResult<Document?>(null);

        var abs = Path.GetFullPath(filePath);
        var docId = _solution.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
        if (docId == null)
        {
            // Try case-insensitive match (Windows file system).
            docId = _solution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        }

        return Task.FromResult(docId != null ? _solution.GetDocument(docId) : null);
    }

    public IReadOnlyList<Project> GetProjects()
    {
        if (_solution != null)
            return _solution.Projects.ToList();

        return Array.Empty<Project>();
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (_loadedPath != null)
            await LoadAsync(_loadedPath, ct);
    }

    private Project? FindProject(string projectPath)
    {
        if (_solution == null)
            return null;

        var abs = Path.GetFullPath(projectPath);
        return _solution.Projects.FirstOrDefault(p =>
            string.Equals(p.FilePath, abs, StringComparison.OrdinalIgnoreCase));
    }

    private static MSBuildWorkspace CreateWorkspace()
    {
        var props = new Dictionary<string, string>
        {
            ["AlwaysCompileContentFiles"] = "true",
            ["CheckForOverflowUnderflow"] = "false",
        };

        return MSBuildWorkspace.Create(props);
    }

    /// <summary>
    /// Registers MSBuild Locator exactly once. Uses a lock-based guard so
    /// concurrent first-load calls can't double-register (which throws
    /// InvalidOperationException). Called once at construction so the locator
    /// is registered before any LoadAsync contends.
    /// </summary>
    private static readonly object RegisterLock = new();
    private static void EnsureMSBuildRegistered()
    {
        if (_msbuildRegistered)
            return;

        lock (RegisterLock)
        {
            if (_msbuildRegistered)
                return;

            var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
            var instance = instances
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (instance != null)
                MSBuildLocator.RegisterInstance(instance);
            else
                MSBuildLocator.RegisterDefaults();

            _msbuildRegistered = true;
        }
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _loadLock.Dispose();
    }
}
