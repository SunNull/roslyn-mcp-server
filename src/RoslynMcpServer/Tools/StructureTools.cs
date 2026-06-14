using System.ComponentModel;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Analysis;
using RoslynMcpServer.Core.Workspace;

namespace RoslynMcpServer.Tools;

/// <summary>
/// Structure and refactoring tools — for understanding and navigating code
/// structure rather than individual symbols. These power "list all classes in
/// this file", "who implements IService", and "show me what would change if I
/// renamed X" queries.
/// </summary>
[McpServerToolType]
public static class StructureTools
{
    // ── roslyn_find_symbols ──────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_find_symbols")]
    [Description(
        "Search for symbols (classes, methods, properties, fields, events) by name " +
        "across the entire solution. Returns file:line locations. Use for " +
        "'where is MyClass defined' queries.")]
    public static async Task<string> FindSymbols(
        IWorkspaceHost host,
        [Description("Symbol name or partial name to search for")] string pattern,
        [Description("Optional: filter by symbol kind (class, method, property, etc.)")] string? kind_filter = null,
        CancellationToken ct = default)
    {
        var solution = host.CurrentSolution;
        if (solution == null)
            return "Error: no solution loaded. Call roslyn_workspace_info or load a .sln/.csproj first.";

        // Use the built-in symbol finder which searches all projects.
        var results = (await SymbolFinder.FindSourceDeclarationsAsync(solution, pattern, false, cancellationToken: ct)).ToList();

        // Apply optional kind filter.
        if (!string.IsNullOrEmpty(kind_filter))
        {
            var kf = kind_filter.ToLowerInvariant();
            results = results.Where(r => r.Kind.ToString().ToLowerInvariant().Contains(kf)).ToList();
        }

        if (results.Count == 0)
            return $"No symbols matching '{pattern}' found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} symbol{(results.Count == 1 ? "" : "s")} matching '{pattern}':");

        foreach (var sym in results.Take(100))
        {
            var loc = sym.Locations.FirstOrDefault();
            if (loc == null || !loc.IsInSource)
                continue;

            var lineSpan = loc.GetLineSpan();
            var f = loc.SourceTree?.FilePath ?? "?";
            sb.AppendLine($"  {Path.GetFileName(f)}:{lineSpan.StartLinePosition.Line + 1}  [{sym.Kind}] {sym.Name}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_get_file_members ──────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_get_file_members")]
    [Description(
        "List all top-level declarations (classes, interfaces, structs, enums) and " +
        "their members in a .cs file. Returns a hierarchical tree. Use for " +
        "'show me everything in this file' queries.")]
    public static async Task<string> GetFileMembers(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        CancellationToken ct = default)
    {
        var doc = await host.GetDocumentAsync(file, ct);
        if (doc == null)
            return $"Error: '{file}' is not part of a loaded project.";

        var root = await doc.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        if (root == null)
            return "Error: could not parse file.";

        var sb = new StringBuilder();
        sb.AppendLine(Path.GetFileName(file) + ":");

        foreach (var member in root.Members)
        {
            FormatMember(sb, member, 1);
        }

        return sb.ToString().TrimEnd();
    }

    private static void FormatMember(StringBuilder sb, MemberDeclarationSyntax member, int indent)
    {
        var prefix = new string(' ', indent * 2);
        var keyword = member.Kind().ToString();
        var name = GetMemberName(member);

        if (!string.IsNullOrEmpty(name))
            sb.AppendLine($"{prefix}{keyword} {name}");
        else
            sb.AppendLine($"{prefix}{keyword}");

        // Recurse into nested members (class body, etc.)
        var children = member.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m.Parent == member);

        foreach (var child in children)
        {
            FormatMember(sb, child, indent + 1);
        }
    }

    private static string GetMemberName(MemberDeclarationSyntax member) => member switch
    {
        ClassDeclarationSyntax c => c.Identifier.Text,
        InterfaceDeclarationSyntax i => i.Identifier.Text,
        StructDeclarationSyntax s => s.Identifier.Text,
        EnumDeclarationSyntax e => e.Identifier.Text,
        MethodDeclarationSyntax m => $"{m.ReturnType} {m.Identifier.Text}({string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type))})",
        PropertyDeclarationSyntax p => $"{p.Type} {p.Identifier.Text}",
        FieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text)),
        EventDeclarationSyntax ev => ev.Identifier.Text,
        NamespaceDeclarationSyntax ns => ns.Name.ToString(),
        _ => ""
    };

    // ── roslyn_find_implementations ──────────────────────────────────────────
    [McpServerTool(Name = "roslyn_find_implementations")]
    [Description(
        "Find all implementations of an interface or abstract method/class. Give " +
        "the file, 1-based line, and symbol text. Returns each implementation.")]
    public static async Task<string> FindImplementations(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number")] int line,
        [Description("The interface or abstract member name")] string symbol,
        CancellationToken ct = default)
    {
        var solution = host.CurrentSolution;
        var doc = await host.GetDocumentAsync(file, ct);
        if (doc == null || solution == null)
            return $"Error: could not resolve '{file}' or no solution loaded.";

        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model == null || root == null)
            return "Error: semantic model unavailable.";

        // Find the symbol at the position.
        var sourceText = await doc.GetTextAsync(ct);
        var lineText = sourceText.Lines[line - 1];
        var lineStr = sourceText.ToString(new TextSpan(lineText.Start, lineText.Span.Length));
        var col = lineStr.IndexOf(symbol, StringComparison.Ordinal);
        if (col < 0)
            return $"Symbol '{symbol}' not found on line {line}.";

        var absPos = lineText.Start + col;
        var node = root.FindToken(absPos).Parent;
        if (node == null) return $"No syntax node found at '{file}:{line}'.";
        var sym = model.GetSymbolInfo(node).Symbol;
        if (sym == null)
            return $"No symbol found for '{symbol}'.";

        var implementations = (await SymbolFinder.FindImplementationsAsync(sym, solution, cancellationToken: ct)).ToList();

        if (implementations.Count == 0)
            return $"No implementations found for '{sym.Name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {implementations.Count} implementation{(implementations.Count == 1 ? "" : "s")} of '{sym.Name}':");

        foreach (var impl in implementations)
        {
            var loc = impl.Locations.FirstOrDefault();
            if (loc == null || !loc.IsInSource)
                continue;

            var lineSpan = loc.GetLineSpan();
            var f = loc.SourceTree?.FilePath ?? "?";
            var snippet = PositionMapper.ReadLine(f, lineSpan.StartLinePosition.Line + 1);
            sb.AppendLine($"  {Path.GetFileName(f)}:{lineSpan.StartLinePosition.Line + 1}  {snippet}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_preview_rename ────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_preview_rename")]
    [Description(
        "Preview all locations that would change if a symbol were renamed. Give the " +
        "file, 1-based line, current symbol name, and proposed new name. Returns the " +
        "list of affected locations WITHOUT applying any changes.")]
    public static async Task<string> PreviewRename(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number")] int line,
        [Description("Current symbol name")] string symbol,
        [Description("Proposed new name")] string new_name,
        CancellationToken ct = default)
    {
        var solution = host.CurrentSolution;
        var doc = await host.GetDocumentAsync(file, ct);
        if (doc == null || solution == null)
            return $"Error: could not resolve '{file}' or no solution loaded.";

        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model == null || root == null)
            return "Error: semantic model unavailable.";

        var sourceText = await doc.GetTextAsync(ct);
        var lineText = sourceText.Lines[line - 1];
        var lineStr = sourceText.ToString(new TextSpan(lineText.Start, lineText.Span.Length));
        var col = lineStr.IndexOf(symbol, StringComparison.Ordinal);
        if (col < 0)
            return $"Symbol '{symbol}' not found on line {line}.";

        var absPos = lineText.Start + col;
        var node = root.FindToken(absPos).Parent;
        if (node == null) return $"No syntax node found at '{file}:{line}'.";
        var sym = model.GetSymbolInfo(node).Symbol;
        if (sym == null)
            return $"No symbol found for '{symbol}'.";

        // Compute the rename without applying it.
#pragma warning disable CS0618
        var newSolution = await Renamer.RenameSymbolAsync(solution, sym, new_name, (Microsoft.CodeAnalysis.Options.OptionSet?)null, ct);
#pragma warning restore CS0618
        // Compare old vs new solution to count affected documents.
        var changedDocIds = new List<DocumentId>();
        foreach (var projectChange in newSolution.GetChanges(solution).GetProjectChanges())
        {
            changedDocIds.AddRange(projectChange.GetChangedDocuments());
        }

        var sb = new StringBuilder();
        var totalChanges = changedDocIds.Count;

        sb.AppendLine($"Renaming '{sym.Name}' → '{new_name}' would affect {totalChanges} location{(totalChanges == 1 ? "" : "s")}:");

        foreach (var docId in changedDocIds)
        {
            var changedDoc = newSolution.GetDocument(docId);
            if (changedDoc?.FilePath != null)
                sb.AppendLine($"  {Path.GetFileName(changedDoc.FilePath)}");
        }

        return sb.ToString().TrimEnd();
    }
}
