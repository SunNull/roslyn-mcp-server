using System.ComponentModel;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Recommendations;
using Microsoft.CodeAnalysis.Text;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Analysis;
using RoslynMcpServer.Core.Workspace;

namespace RoslynMcpServer.Tools;

/// <summary>
/// Semantic query tools — the 5 core "ask the compiler about a symbol" tools.
/// Each takes a file path + 1-based line + symbol text (matching how the LLM
/// sees them in read_file output) and returns structured compiler knowledge.
/// </summary>
[McpServerToolType]
public static class SemanticTools
{
    // ── roslyn_hover ─────────────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_hover")]
    [Description(
        "Get the type signature, documentation, and member info for a symbol at a " +
        "specific location. Give the file path, 1-based line number, and the symbol " +
        "text. Returns the fully-qualified type name, method signature, or type info.")]
    public static async Task<string> Hover(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number where the symbol appears")] int line,
        [Description("The symbol text on that line (used to locate the column)")] string symbol,
        CancellationToken ct = default)
    {
        var (doc, pos) = await ResolvePositionAsync(host, file, line, symbol, ct);
        if (doc == null)
            return $"Error: could not resolve '{file}' in the loaded workspace. Load the .csproj first.";

        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model == null || root == null)
            return "Error: semantic model unavailable.";

        var node = root.FindToken(pos).Parent;
        if (node == null) return "No syntax node found at that position.";
        var symbolInfo = model.GetSymbolInfo(node);
        var sym = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        if (sym == null)
            return $"No symbol information found at {Path.GetFileName(file)}:{line}.";

        var sb = new StringBuilder();
        sb.AppendLine($"Symbol: {sym.Name}");
        sb.AppendLine($"Kind: {sym.Kind}");
        sb.AppendLine($"Full name: {sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");

        if (sym is ITypeSymbol typeSym)
        {
            sb.AppendLine($"Type: {typeSym.TypeKind}");
            if (typeSym.BaseType != null)
                sb.AppendLine($"Base: {typeSym.BaseType}");
            var members = typeSym.GetMembers();
            sb.AppendLine($"Members: {members.Length}");
        }

        if (!string.IsNullOrEmpty(sym.GetDocumentationCommentXml()))
            sb.AppendLine($"Docs: {sym.GetDocumentationCommentXml()}");

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_goto_definition ───────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_goto_definition")]
    [Description(
        "Jump to where a symbol is defined. Returns the file path, line number, " +
        "and a snippet of the definition. Give the file, 1-based line, and symbol.")]
    public static async Task<string> GotoDefinition(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number")] int line,
        [Description("The symbol text to find the definition of")] string symbol,
        CancellationToken ct = default)
    {
        var (doc, pos) = await ResolvePositionAsync(host, file, line, symbol, ct);
        if (doc == null)
            return $"Error: could not resolve '{file}'.";

        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model == null || root == null)
            return "Error: semantic model unavailable.";

        var node = root.FindToken(pos).Parent;
        if (node == null) return "No syntax node found at that position.";
        var sym = model.GetSymbolInfo(node).Symbol;
        if (sym == null)
            return $"No definition found for '{symbol}'.";

        var loc = sym.Locations.FirstOrDefault();
        if (loc == null || !loc.IsInSource)
            return $"Symbol '{sym.Name}' is defined in metadata (external assembly), not in source.";

        var lineSpan = loc.GetLineSpan();
        var defFile = loc.SourceTree?.FilePath ?? "?";
        var defLine = lineSpan.StartLinePosition.Line + 1;
        var snippet = PositionMapper.ReadLine(defFile, defLine);

        return $"{Path.GetFileName(defFile)}:{defLine}  {snippet}\n  → {sym.ToDisplayString()}";
    }

    // ── roslyn_find_references ───────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_find_references")]
    [Description(
        "Find all references to a symbol across the entire solution. Give the " +
        "file, 1-based line, and symbol text. Returns each reference as file:line.")]
    public static async Task<string> FindReferences(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number")] int line,
        [Description("The symbol text to find references for")] string symbol,
        CancellationToken ct = default)
    {
        var (doc, pos) = await ResolvePositionAsync(host, file, line, symbol, ct);
        if (doc == null)
            return $"Error: could not resolve '{file}'.";

        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model == null || root == null)
            return "Error: semantic model unavailable.";

        var node = root.FindToken(pos).Parent;
        if (node == null) return "No syntax node found at that position.";
        var sym = model.GetSymbolInfo(node).Symbol;
        if (sym == null)
            return $"No symbol found at '{file}:{line}'.";

        var references = await SymbolFinder.FindReferencesAsync(sym, doc.Project.Solution, ct);

        var sb = new StringBuilder();
        var total = 0;
        foreach (var refLoc in references.SelectMany(r => r.Locations))
        {
            var loc = refLoc.Location;
            if (!loc.IsInSource)
                continue;

            var lineSpan = loc.GetLineSpan();
            var refFile = loc.SourceTree?.FilePath ?? "?";
            var refLine = lineSpan.StartLinePosition.Line + 1;
            var snippet = PositionMapper.ReadLine(refFile, refLine);

            sb.AppendLine($"  {Path.GetFileName(refFile)}:{refLine}  {snippet}");
            total++;
        }

        sb.Insert(0, $"Found {total} reference{(total == 1 ? "" : "s")} to '{sym.Name}':\n");
        return sb.ToString().TrimEnd();
    }

    // ── roslyn_completion ────────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_completion")]
    [Description(
        "Get intelligent code completion suggestions at a position in a .cs file. " +
        "Give the file path and 1-based line/column position. Returns method, " +
        "property, and type names available at that location.")]
    public static async Task<string> Completion(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number")] int line,
        [Description("1-based column position")] int column,
        [Description("Optional: filter to symbols containing this text")] string filter = "",
        CancellationToken ct = default)
    {
        var (doc, pos) = await ResolvePositionAsync(host, file, line, column, ct);
        if (doc == null)
            return $"Error: could not resolve '{file}'.";

        var model = await doc.GetSemanticModelAsync(ct);
        if (model == null)
            return "Error: semantic model unavailable.";

        var sourceText = await doc.GetTextAsync(ct);
        var absPos = sourceText.Lines[line - 1].Start + Math.Max(0, column - 1);

        // Use LookupSymbols — a stable Roslyn API that returns all accessible
        // symbols in scope at a position. Less smart than RecommenderService
        // (no filter-as-you-type) but works reliably across versions.
        var token = (await doc.GetSyntaxRootAsync(ct))?.FindToken(absPos);
        SyntaxNode? container = token?.Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        container ??= token?.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var scopeSymbol = container != null ? model.GetDeclaredSymbol(container) : null;
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        var items = model.LookupSymbols(absPos, container: scopeSymbol as INamespaceOrTypeSymbol, name: filter ?? "");
        var symbols = items
            .Where(s => string.IsNullOrEmpty(filter) ||
                        s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(s => seenSymbols.Add(s))
            .Take(50);

        var sb = new StringBuilder();
        var groups = symbols.GroupBy(s => s.Kind).OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            sb.AppendLine($"  {group.Key}:");
            foreach (var s in group.Take(20))
            {
                sb.AppendLine($"    {s.Name} — {s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
            }
        }

        return sb.Length == 0
            ? "No completion items available at that position."
            : sb.ToString().TrimEnd();
    }

    // ── roslyn_signature_help ────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_signature_help")]
    [Description(
        "Get method signature help (parameter info) for the method call at a " +
        "position. Give the file, 1-based line, and the method name.")]
    public static async Task<string> SignatureHelp(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number where the method call is")] int line,
        [Description("The method name text")] string symbol,
        CancellationToken ct = default)
    {
        var (doc, pos) = await ResolvePositionAsync(host, file, line, symbol, ct);
        if (doc == null)
            return $"Error: could not resolve '{file}'.";

        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model == null || root == null)
            return "Error: semantic model unavailable.";

        // Walk up to find an InvocationExpression containing this position.
        var node = root.FindToken(pos).Parent;
        if (node == null) return "No syntax node found at that position.";
        var invocation = node?.FirstAncestorOrSelf<InvocationExpressionSyntax>();

        if (invocation == null)
            return "No method invocation found at that position.";

        var symbolInfo = model.GetSymbolInfo(invocation);
        var methodSym = symbolInfo.Symbol as IMethodSymbol;

        if (methodSym == null && symbolInfo.CandidateSymbols.Length > 0)
            methodSym = symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (methodSym == null)
            return "Could not resolve the method being called.";

        var sb = new StringBuilder();
        sb.AppendLine($"Method: {methodSym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        sb.AppendLine($"Parameters: {methodSym.Parameters.Length}");
        foreach (var p in methodSym.Parameters)
        {
            sb.AppendLine($"  {p.Name}: {p.Type} {(p.HasExplicitDefaultValue ? $"= {p.ExplicitDefaultValue}" : "")}");
        }
        sb.AppendLine($"Returns: {methodSym.ReturnType}");

        if (symbolInfo.CandidateSymbols.Length > 1)
        {
            sb.AppendLine();
            sb.AppendLine($"Other overloads ({symbolInfo.CandidateSymbols.Length}):");
            foreach (var cand in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().Take(10))
                sb.AppendLine($"  {cand.ToDisplayString()}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    private static async Task<(Document? Doc, int Position)> ResolvePositionAsync(
        IWorkspaceHost host,
        string filePath,
        int line1,
        string symbolOrColumn,
        CancellationToken ct)
    {
        var doc = await host.GetDocumentAsync(filePath, ct);
        if (doc == null)
            return (null, 0);

        var sourceText = await doc.GetTextAsync(ct);
        if (line1 < 1 || line1 > sourceText.Lines.Count)
            return (null, 0);

        var line0 = line1 - 1;
        var textLine = sourceText.Lines[line0];

        // Try to find the symbol text on the line; fall back to column 0.
        var lineStr = sourceText.ToString(new TextSpan(textLine.Start, textLine.Span.Length));
        var col = lineStr.IndexOf(symbolOrColumn, StringComparison.Ordinal);
        var absPos = col >= 0 ? textLine.Start + col : textLine.Start;

        return (doc, absPos);
    }

    private static async Task<(Document? Doc, int Position)> ResolvePositionAsync(
        IWorkspaceHost host,
        string filePath,
        int line1,
        int column1,
        CancellationToken ct)
    {
        var doc = await host.GetDocumentAsync(filePath, ct);
        if (doc == null)
            return (null, 0);

        var sourceText = await doc.GetTextAsync(ct);
        if (line1 < 1 || line1 > sourceText.Lines.Count)
            return (null, 0);

        var line0 = line1 - 1;
        var textLine = sourceText.Lines[line0];
        var absPos = textLine.Start + Math.Max(0, column1 - 1);

        return (doc, absPos);
    }
}
