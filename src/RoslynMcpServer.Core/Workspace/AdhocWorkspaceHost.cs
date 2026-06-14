using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Core.Workspace;

/// <summary>
/// Minimal fallback host: builds a <see cref="CSharpCompilation"/> from one or
/// more .cs files with a hard-coded set of base assembly references. Does NOT
/// resolve NuGet packages or project references — used when no .sln/.csproj is
/// loaded, or for .cs files outside any project. The production host is
/// <see cref="MSBuildWorkspaceHost"/>.
/// </summary>
public sealed class AdhocWorkspaceHost : IWorkspaceHost
{
    // Cached metadata references — loaded once, reused across all calls.
    private static readonly Lazy<MetadataReference[]> BaseReferences = new(LoadBaseReferences);

    // Per-file syntax-tree cache keyed by path + last-write-time, so repeated
    // diagnostics calls on an unchanged file don't re-parse.
    private readonly ConcurrentDictionary<string, (DateTime mtime, SyntaxTree tree)> _treeCache = new();

    public Microsoft.CodeAnalysis.Workspace? Workspace => null;
    public Solution? CurrentSolution => null;
    public bool IsReady => true;

    public Task<Compilation?> GetCompilationAsync(string path, CancellationToken ct = default)
    {
        var trees = ResolveTrees(path);
        var compilation = CSharpCompilation.Create(
            assemblyName: "adhoc",
            syntaxTrees: trees,
            references: BaseReferences.Value,
            options: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithOverflowChecks(true)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        return Task.FromResult<Compilation?>(compilation);
    }

    public Task<Document?> GetDocumentAsync(string filePath, CancellationToken ct = default)
    {
        // AdhocWorkspaceHost has no project/workspace concept — no Document.
        return Task.FromResult<Document?>(null);
    }

    public IReadOnlyList<Project> GetProjects() => Array.Empty<Project>();

    public Task ReloadAsync(CancellationToken ct = default)
    {
        _treeCache.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads a file's source text (used by ad-hoc diagnostics and by the file
    /// loader when no project is available).
    /// </summary>
    public async Task<(SourceText Text, string FilePath)?> LoadFileAsync(string path, CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(path);
        if (!File.Exists(abs))
            return null;

        await using var stream = File.OpenRead(abs);
        var sourceText = SourceText.From(stream, Encoding.UTF8);
        return (sourceText, abs);
    }

    private IList<SyntaxTree> ResolveTrees(string path)
    {
        var abs = Path.GetFullPath(path);
        var files = new List<string>();

        if (File.Exists(abs))
        {
            files.Add(abs);
        }
        else if (Directory.Exists(abs))
        {
            files.AddRange(Directory.EnumerateFiles(abs, "*.cs", SearchOption.AllDirectories));
        }
        else
        {
            throw new FileNotFoundException($"Path not found: {abs}", abs);
        }

        return files.ConvertAll(GetOrParseTree);
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

    private static MetadataReference[] LoadBaseReferences()
    {
        var paths = new HashSet<string>();

        void Add(Type type)
        {
            var loc = type.Assembly.Location;
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

        var runtimeAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "System.Runtime", StringComparison.OrdinalIgnoreCase));
        if (runtimeAsm?.Location is { Length: > 0 } runtimePath && File.Exists(runtimePath))
            paths.Add(runtimePath);

        var netstandard = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "netstandard", StringComparison.OrdinalIgnoreCase));
        if (netstandard?.Location is { Length: > 0 } nsPath && File.Exists(nsPath))
            paths.Add(nsPath);

        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    }
}
