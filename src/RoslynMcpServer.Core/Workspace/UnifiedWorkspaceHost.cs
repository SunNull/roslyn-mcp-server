using System.Collections.Concurrent;
using System.Text;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Core.Workspace;

/// <summary>
/// The single host that handles everything. It transparently merges two
/// strategies:
///
/// 1. <b>Project mode</b> — when a .sln/.csproj is loaded (manually via
///    <see cref="LoadProjectAsync"/> or auto-discovered), all queries get the
///    full semantic model with NuGet references resolved.
///
/// 2. <b>Adhoc mode</b> — standalone .cs files get an adhoc compilation with
///    base BCL references. Good enough for syntax errors and basic type checks.
///
/// The AI doesn't need to know which mode is active — it just passes a file
/// path and the host does the right thing. If the file is inside a loaded
/// project, it gets project-grade analysis. If not, it gets adhoc analysis.
/// The AI can call <c>roslyn_load_project</c> to upgrade to project mode at
/// any time.
/// </summary>
public sealed class UnifiedWorkspaceHost : IWorkspaceHost, IDisposable
{
    private MSBuildWorkspace? _msbuildWorkspace;
    private Solution? _solution;
    private string? _loadedProjectPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private static bool _msbuildRegistered;

    // Adhoc fallback for standalone files.
    private readonly ConcurrentDictionary<string, (DateTime mtime, SyntaxTree tree)> _treeCache = new();
    private static readonly Lazy<MetadataReference[]> BaseReferences = new(LoadBaseReferences);

    public Microsoft.CodeAnalysis.Workspace? Workspace => _msbuildWorkspace;
    public Solution? CurrentSolution => _solution;
    public bool IsProjectLoaded => _solution != null;
    public string? LoadedProjectPath => _loadedProjectPath;

    public UnifiedWorkspaceHost()
    {
        EnsureMSBuildRegistered();
    }

    // ── IWorkspaceHost ─────────────────────────────────────────────────────

    public async Task<Compilation?> GetCompilationAsync(string path, CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(path);
        var ext = Path.GetExtension(abs);

        // .cs file: try to find it in a loaded project first.
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var doc = await GetDocumentAsync(abs, ct);
            if (doc != null)
                return await doc.Project.GetCompilationAsync(ct);

            // Not in a loaded project — try auto-discovery, then adhoc.
            await TryAutoLoadForFileAsync(abs, ct);
            doc = await GetDocumentAsync(abs, ct);
            if (doc != null)
                return await doc.Project.GetCompilationAsync(ct);

            return AdhocCompilation(abs);
        }

        // .csproj/.sln: load and return first project's compilation.
        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
           ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            await LoadProjectAsync(abs, ct);
            var project = _solution?.Projects.FirstOrDefault();
            return project != null ? await project.GetCompilationAsync(ct) : null;
        }

        // Directory: adhoc-compile all .cs files in it.
        if (Directory.Exists(abs))
            return AdhocCompilation(abs);

        throw new FileNotFoundException($"Path not found: {abs}", abs);
    }

    public Task<Document?> GetDocumentAsync(string filePath, CancellationToken ct = default)
    {
        if (_solution == null)
            return Task.FromResult<Document?>(null);

        var abs = Path.GetFullPath(filePath);
        var docId = _solution.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
        if (docId == null)
        {
            // Case-insensitive fallback (Windows).
            docId = _solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, abs, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        return Task.FromResult(docId != null ? _solution.GetDocument(docId) : null);
    }

    public IReadOnlyList<Project> GetProjects()
    {
        if (_solution != null)
            return _solution.Projects.ToList();

        return new List<Project>();
    }

    public async Task LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        await _loadLock.WaitAsync(ct);
        try
        {
            EnsureMSBuildRegistered();
            var abs = Path.GetFullPath(projectPath);
            var ext = Path.GetExtension(abs);

            _msbuildWorkspace?.Dispose();
            _msbuildWorkspace = CreateWorkspace();

            if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                _solution = await _msbuildWorkspace.OpenSolutionAsync(abs, cancellationToken: ct);
            }
            else if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                await _msbuildWorkspace.OpenProjectAsync(abs, cancellationToken: ct);
                _solution = _msbuildWorkspace.CurrentSolution;
            }
            else
            {
                throw new ArgumentException($"Expected .sln or .csproj, got: {abs}");
            }

            _loadedProjectPath = abs;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<bool> TryAutoLoadForFileAsync(string csFilePath, CancellationToken ct = default)
    {
        // Already loaded? Check if this file is in the current solution.
        if (_solution != null)
        {
            var abs = Path.GetFullPath(csFilePath);
            if (_solution.GetDocumentIdsWithFilePath(abs).Any())
                return true; // Already covered by the loaded project.
        }

        // Search upward for .sln or .csproj (like git finding .git).
        var dir = Path.GetDirectoryName(Path.GetFullPath(csFilePath));
        while (dir != null)
        {
            // Prefer .sln (covers multi-project solutions).
            var slnFiles = Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                try
                {
                    await LoadProjectAsync(slnFiles[0], ct);
                    return true;
                }
                catch { /* fall through to .csproj search */ }
            }

            // Fall back to .csproj in this directory.
            var csprojFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                try
                {
                    await LoadProjectAsync(csprojFiles[0], ct);
                    return true;
                }
                catch { /* fall through */ }
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }

        return false;
    }

    // ── Adhoc fallback ──────────────────────────────────────────────────────

    private Compilation? AdhocCompilation(string path)
    {
        var files = new List<string>();

        if (File.Exists(path))
            files.Add(path);
        else if (Directory.Exists(path))
            files.AddRange(Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories));
        else
            return null;

        var trees = files.ConvertAll(GetOrParseTree);
        return CSharpCompilation.Create(
            assemblyName: "adhoc",
            syntaxTrees: trees,
            references: BaseReferences.Value,
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithNullableContextOptions(NullableContextOptions.Enable));
    }

    private SyntaxTree GetOrParseTree(string file)
    {
        var mtime = File.GetLastWriteTimeUtc(file);
        var key = Path.GetFullPath(file);

        if (_treeCache.TryGetValue(key, out var entry) && entry.mtime == mtime)
            return entry.tree;

        var source = File.ReadAllText(file, Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(source, path: key);
        _treeCache[key] = (mtime, tree);
        return tree;
    }

    // ── MSBuild infrastructure ──────────────────────────────────────────────

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
            var instance = instances.OrderByDescending(i => i.Version).FirstOrDefault();
            if (instance != null)
                MSBuildLocator.RegisterInstance(instance);
            else
                MSBuildLocator.RegisterDefaults();

            _msbuildRegistered = true;
        }
    }

    private static MSBuildWorkspace CreateWorkspace()
    {
        return MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["AlwaysCompileContentFiles"] = "true",
        });
    }

    private static MetadataReference[] LoadBaseReferences()
    {
        var paths = new HashSet<string>();
        void Add(Type t)
        {
            var loc = t.Assembly.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                paths.Add(loc);
        }

        Add(typeof(object));
        Add(typeof(Console));
        Add(typeof(Enumerable));
        Add(typeof(List<>));
        Add(typeof(Dictionary<,>));
        Add(typeof(Task));
        Add(typeof(Uri));

        foreach (var name in new[] { "System.Runtime", "netstandard" })
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (asm?.Location is { Length: > 0 } p && File.Exists(p))
                paths.Add(p);
        }

        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    }

    public void Dispose()
    {
        _msbuildWorkspace?.Dispose();
        _loadLock.Dispose();
    }
}
