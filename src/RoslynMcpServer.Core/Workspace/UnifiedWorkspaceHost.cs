using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Core.Workspace;

/// <summary>
/// The single host that handles everything. It transparently merges two
/// strategies:
///
/// 1. <b>Project mode</b> — when a .sln/.slnx/.csproj is loaded (manually via
///    <see cref="LoadProjectAsync"/> or auto-discovered), all queries get the
///    full semantic model with NuGet references resolved.
///
/// 2. <b>Adhoc mode</b> — standalone .cs files get an adhoc compilation with
///    base BCL references. Good enough for syntax errors and basic type checks.
///
/// The AI doesn't need to know which mode is active — it just passes a file
/// path and the host does the right thing. If the file is inside a loaded
/// project, it gets project-grade analysis. If not, it gets adhoc analysis.
/// The AI can call <c>roslyn_csharp_load_project</c> to upgrade to project mode at
/// any time.
/// </summary>
public sealed class UnifiedWorkspaceHost : IWorkspaceHost, IDisposable
{
    private MSBuildWorkspace? _msbuildWorkspace;
    private Solution? _solution;
    private string? _loadedProjectPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private static bool _msbuildRegistered;

    // Loading state: when a load is in progress, query methods wait for it
    // instead of returning "no solution loaded" — so the AI doesn't have to
    // guess when Roslyn has finished parsing.
    private volatile bool _isLoading;
    private TaskCompletionSource<bool>? _loadTcs;

    // Adhoc fallback for standalone files.
    private readonly ConcurrentDictionary<string, (DateTime mtime, SyntaxTree tree)> _treeCache = new();
    private static readonly Lazy<MetadataReference[]> BaseReferences = new(LoadBaseReferences);

    public Microsoft.CodeAnalysis.Workspace? Workspace => _msbuildWorkspace;
    public Solution? CurrentSolution => _solution;
    public bool IsProjectLoaded => _solution != null;
    public bool IsLoading => _isLoading;
    public string? LoadedProjectPath => _loadedProjectPath;

    public UnifiedWorkspaceHost()
    {
        EnsureMSBuildRegistered();
    }

    // ── IWorkspaceHost ─────────────────────────────────────────────────────

    public async Task<Compilation?> GetCompilationAsync(string path, CancellationToken ct = default)
    {
        // If a project load is in progress, wait for it so we see the loaded state.
        await WaitForLoadAsync(ct);

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required. Provide a .cs file or a directory.");

        // Normalize path separators (forward slashes work on all platforms via
        // Path.GetFullPath, but we normalize so error messages are clean).
        var abs = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
        var ext = Path.GetExtension(abs);

        // Early existence check — fail fast with a clear message instead of
        // letting downstream code throw confusing "part of the path" errors.
        if (!File.Exists(abs) && !Directory.Exists(abs))
        {
            // Non-C# extension check first (for clear "C# only" message).
            if (IsNonCSharpSource(ext))
                throw new NotSupportedException(
                    $"This server only analyzes C# (.cs) files. '{ext}' is not supported. " +
                    "Use the appropriate language tooling for this file.");

            throw new FileNotFoundException(
                $"Path not found: {abs}. " +
                "Provide a .cs file path, a .sln/.slnx/.csproj, or a directory. " +
                "If the file exists, check that the path is correct and absolute.", abs);
        }

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

        // .csx file (C# script): use the scripting parser, not the regular C# parser.
        // Script syntax differs from regular C# (top-level statements, #r, #load).
        if (ext.Equals(".csx", StringComparison.OrdinalIgnoreCase))
        {
            return AdhocScriptCompilation(abs);
        }

        // .csproj/.sln/.slnx: load and return first project's compilation.
        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
           IsSolutionExt(ext))
        {
            await LoadProjectAsync(abs, ct);
            var project = _solution?.Projects.FirstOrDefault();
            return project != null ? await project.GetCompilationAsync(ct) : null;
        }

        // Directory: adhoc-compile all .cs files in it.
        if (Directory.Exists(abs))
            return AdhocCompilation(abs);

        // Non-C# source files: reject explicitly so the AI gets a clear message
        // instead of a confusing "file not found". Roslyn MCP only analyzes C#.
        if (IsNonCSharpSource(ext))
            throw new NotSupportedException(
                $"This server only analyzes C# (.cs) files. '{ext}' is not supported. " +
                "Use the appropriate language tooling for this file.");

        // File exists but extension is unknown (not .cs/.csproj/.sln/.slnx/directory).
        throw new NotSupportedException(
            $"'{ext}' files are not analyzed by this server. Only .cs files are supported.");
    }

    public async Task<Document?> GetDocumentAsync(string filePath, CancellationToken ct = default)
    {
        // Wait for any in-progress load so we don't miss a freshly-loaded project.
        await WaitForLoadAsync(ct);

        if (_solution == null)
            return null;

        // Normalize separators: Roslyn stores paths with the OS separator, but
        // MCP clients may send forward slashes (D:/path/file.cs). Without this,
        // the path comparison fails even on a correctly loaded document.
        var abs = Path.GetFullPath(filePath.Replace('/', Path.DirectorySeparatorChar));
        var docId = _solution.GetDocumentIdsWithFilePath(abs).FirstOrDefault();
        if (docId == null)
        {
            // Case-insensitive + separator-insensitive fallback.
            var normalizedAbs = abs.Replace('\\', '/');
            docId = _solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d =>
                {
                    if (d.FilePath == null) return false;
                    var normalizedDoc = d.FilePath.Replace('\\', '/');
                    return string.Equals(normalizedDoc, normalizedAbs, StringComparison.OrdinalIgnoreCase);
                })?.Id;
        }

        return docId != null ? _solution.GetDocument(docId) : null;
    }

    public IReadOnlyList<Project> GetProjects()
    {
        if (_solution != null)
            return _solution.Projects.ToList();

        return new List<Project>();
    }

    public async Task LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        // Set loading flag BEFORE acquiring the lock so concurrent queries
        // (from parallel MCP requests) see _isLoading=true and wait.
        _isLoading = true;
        _loadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _loadLock.WaitAsync(ct);
        try
        {
            EnsureMSBuildRegistered();
            var abs = Path.GetFullPath(projectPath);
            var ext = Path.GetExtension(abs);

            if (!File.Exists(abs))
                throw new FileNotFoundException(
                    $"Project file not found: {abs}. Provide a valid .sln, .slnx or .csproj path.", abs);

            _msbuildWorkspace?.Dispose();
            _msbuildWorkspace = CreateWorkspace();

            // Timeout protection: large solutions can take a long time to load,
            // but we don't want the AI to hang forever. 60s is generous.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            if (IsSolutionExt(ext))
            {
                _solution = await _msbuildWorkspace.OpenSolutionAsync(abs, cancellationToken: timeoutCts.Token);
            }
            else if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                await _msbuildWorkspace.OpenProjectAsync(abs, cancellationToken: timeoutCts.Token);
                _solution = _msbuildWorkspace.CurrentSolution;
            }
            else
            {
                throw new ArgumentException($"Expected .sln, .slnx or .csproj, got: {abs}");
            }

            // Strip unresolved analyzer references — they cause SymbolFinder to throw
            // on large solutions that reference analyzer NuGet packages.
            try
            {
                var currentSolution = _solution!;
                var changed = false;
                foreach (var project in currentSolution.Projects.ToList())
                {
                    var badRefs = project.AnalyzerReferences
                        .Where(a => string.IsNullOrEmpty(a.FullPath) || !File.Exists(a.FullPath))
                        .ToList();
                    if (badRefs.Count > 0)
                    {
                        currentSolution = currentSolution.WithProjectAnalyzerReferences(
                            project.Id,
                            project.AnalyzerReferences.Except(badRefs).ToList());
                        changed = true;
                    }
                }
                if (changed)
                    _solution = currentSolution;
            }
            catch { /* best-effort cleanup */ }

            _loadedProjectPath = abs;
        }
        finally
        {
            _isLoading = false;
            _loadTcs?.TrySetResult(true);
            _loadLock.Release();
        }
    }

    /// <summary>
    /// If a project load is in progress (or about to start), wait for it to
    /// complete. Query methods call this before checking _solution so they see
    /// the loaded state instead of a transient "no solution loaded" error.
    /// Uses a poll loop because MCP requests can arrive concurrently — a query
    /// might start before the load's _isLoading flag is set.
    /// </summary>
    public async Task WaitForLoadAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < 600; i++)  // up to ~60 seconds
        {
            // Done waiting if: not loading, lock is free, AND either solution
            // is loaded or we've given it enough time (standalone mode).
            if (!_isLoading && _loadLock.CurrentCount > 0)
            {
                // If solution loaded, we're done. If not, the load already
                // finished without finding a project — also done (standalone).
                if (_solution != null || i > 5)  // at least 0.6s grace period
                    break;
            }
            await Task.Delay(100, ct);
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

        // Search upward for .sln/.slnx or .csproj (like git finding .git).
        var dir = Path.GetDirectoryName(Path.GetFullPath(csFilePath));
        while (dir != null)
        {
            // Prefer a solution file (.sln or .slnx — covers multi-project solutions).
            var slnFiles = Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly))
                .ToArray();
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

        // No .sln/.slnx/.csproj found upward. If there's a Directory.Build.props
        // or global.json, this file is likely part of a larger project structure
        // whose .csproj is elsewhere — hint so the AI knows to look.
        var fileDir = Path.GetDirectoryName(Path.GetFullPath(csFilePath));
        if (fileDir != null)
        {
            var projectRoot = FindUpwardMarker(fileDir, "Directory.Build.props")
                ?? FindUpwardMarker(fileDir, "global.json");
            if (projectRoot != null)
            {
                Console.Error.WriteLine(
                    $"[roslyn-mcp] Found {Path.GetFileName(projectRoot)} at {projectRoot}. " +
                    "This file may belong to a project whose .csproj is elsewhere. " +
                    "Call roslyn_csharp_load_project to enable full analysis.");
            }
        }

        return false;
    }

    /// <summary>Searches upward from <paramref name="startDir"/> for a marker file.</summary>
    private static string? FindUpwardMarker(string startDir, string markerFileName)
    {
        var dir = startDir;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, markerFileName);
            if (File.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }
        return null;
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

    /// <summary>
    /// Adhoc compilation for C# script files (.csx). Uses SourceCodeKind.Script
    /// which tells the parser to accept script-specific syntax (top-level
    /// statements, #r, #load) instead of flagging them as errors.
    /// </summary>
    private Compilation? AdhocScriptCompilation(string path)
    {
        if (!File.Exists(path))
            return null;

        var key = Path.GetFullPath(path);
        var source = File.ReadAllText(key, Encoding.UTF8);

        // Script parse mode: accepts #r/#load directives and top-level code.
        var scriptOptions = new CSharpParseOptions(
            languageVersion: LanguageVersion.Latest,
            kind: SourceCodeKind.Script);
        var tree = CSharpSyntaxTree.ParseText(source, scriptOptions, path: key);

        return CSharpCompilation.Create(
            assemblyName: "script-adhoc",
            syntaxTrees: [tree],
            references: BaseReferences.Value,
            // Script files have implicit global usings.
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithUsings(ImmutableArray.Create(
                    "System", "System.IO", "System.Collections.Generic",
                    "System.Console", "System.Linq", "System.Text",
                    "System.Threading.Tasks")));
    }

    // ── MSBuild infrastructure ──────────────────────────────────────────────

    private static bool IsSolutionExt(string ext) =>
        ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detects source files from other languages so we can reject them with a
    /// clear "C# only" message instead of a confusing file-not-found error.
    /// </summary>
    private static bool IsNonCSharpSource(string ext) =>
        ext.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".fsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".vb", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".go", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".rs", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".java", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".kt", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".swift", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".scss", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".lua", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".rb", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".php", StringComparison.OrdinalIgnoreCase) ||
        // Non-source files that are clearly not C# — reject explicitly
        ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".toml", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".sql", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".dockerfile", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".proto", StringComparison.OrdinalIgnoreCase);

    private static readonly object RegisterLock = new();
    private static void EnsureMSBuildRegistered()
    {
        if (_msbuildRegistered)
            return;

        lock (RegisterLock)
        {
            if (_msbuildRegistered)
                return;

            try
            {
                var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
                var instance = instances.OrderByDescending(i => i.Version).FirstOrDefault();
                if (instance != null)
                    MSBuildLocator.RegisterInstance(instance);
                else
                    MSBuildLocator.RegisterDefaults();

                _msbuildRegistered = true;
            }
            catch (InvalidOperationException)
            {
                // Already registered by another component — that's fine.
                _msbuildRegistered = true;
            }
        }
    }

    private static MSBuildWorkspace CreateWorkspace()
    {
        return MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["AlwaysCompileContentFiles"] = "true",
            // Skip loading analyzer assemblies — they can cause UnresolvedAnalyzerReference
            // errors during SymbolFinder operations on large solutions. Analyzers are not
            // needed for code intelligence (diagnostics/hover/references).
            ["RunAnalyzers"] = "false",
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
