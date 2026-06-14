using System.ComponentModel;
using System.Reflection;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Workspace;

namespace RoslynMcpServer.Tools;

/// <summary>
/// Code fix and formatting tools — for suggesting and applying Roslyn-powered
/// fixes to code problems. These wrap Roslyn's CodeFixProvider infrastructure
/// (the same engine that powers Visual Studio's "light bulb" suggestions).
/// </summary>
[McpServerToolType]
public static class CodeFixTools
{
    // ── roslyn_get_code_fixes ────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_get_code_fixes")]
    [Description(
        "Get Roslyn code fix suggestions for a specific diagnostic in a .cs file. " +
        "Give the file path, 1-based line, and diagnostic ID (e.g. CS0219, IDE0060). " +
        "Returns available fixes with descriptions but does NOT apply them.")]
    public static async Task<string> GetCodeFixes(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number of the diagnostic")] int line,
        [Description("The diagnostic ID (e.g. CS0219, IDE0060)")] string diagnostic_id,
        CancellationToken ct = default)
    {
        var doc = await host.GetDocumentAsync(file, ct);
        if (doc == null)
            return $"Error: '{file}' is not part of a loaded project.";

        // Get the span for the line (expand to the whole line so code fix
        // providers can see the context).
        var sourceText = await doc.GetTextAsync(ct);
        if (line < 1 || line > sourceText.Lines.Count)
            return $"Line {line} is out of range.";

        var textLine = sourceText.Lines[line - 1];
        var span = new TextSpan(textLine.Start, textLine.Span.Length);

        // Find all available code fixes for this span.
        var codeFixes = await FindCodeFixesAsync(doc, span, diagnostic_id, ct);

        if (codeFixes.Count == 0)
            return $"No code fixes available for '{diagnostic_id}' at {Path.GetFileName(file)}:{line}.";

        var sb = new StringBuilder();
        sb.AppendLine($"Available fixes for {diagnostic_id}:");

        for (var i = 0; i < codeFixes.Count; i++)
        {
            sb.AppendLine($"  {i + 1}. {codeFixes[i].Title}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_apply_code_fix ────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_apply_code_fix")]
    [Description(
        "Apply a Roslyn code fix to a .cs file and return the changed text. Give " +
        "the file path, 1-based line, diagnostic ID, and the fix number from " +
        "roslyn_get_code_fixes. Does NOT write to disk — returns the new text for " +
        "the model to review and write via write_file.")]
    public static async Task<string> ApplyCodeFix(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        [Description("1-based line number of the diagnostic")] int line,
        [Description("The diagnostic ID")] string diagnostic_id,
        [Description("Fix number from roslyn_get_code_fixes (1-based)")] int fix_number,
        CancellationToken ct = default)
    {
        var doc = await host.GetDocumentAsync(file, ct);
        if (doc == null)
            return $"Error: '{file}' is not part of a loaded project.";

        var sourceText = await doc.GetTextAsync(ct);
        if (line < 1 || line > sourceText.Lines.Count)
            return $"Line {line} is out of range.";

        var textLine = sourceText.Lines[line - 1];
        var span = new TextSpan(textLine.Start, textLine.Span.Length);

        var codeFixes = await FindCodeFixesAsync(doc, span, diagnostic_id, ct);

        if (codeFixes.Count == 0)
            return $"No code fixes available for '{diagnostic_id}'.";

        if (fix_number < 1 || fix_number > codeFixes.Count)
            return $"Fix number {fix_number} is out of range (1-{codeFixes.Count}).";

        var action = codeFixes[fix_number - 1];

        // Apply the fix.
        var operations = await action.GetOperationsAsync(ct);
        var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();

        if (applyOp == null)
            return "Code fix did not produce any changes.";

        var changedSolution = applyOp.ChangedSolution;
        var changedDoc = changedSolution.GetDocument(doc.Id);

        if (changedDoc == null)
            return "Could not retrieve the changed document.";

        var changedText = await changedDoc.GetTextAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Applied fix: {action.Title}");
        sb.AppendLine();
        sb.AppendLine("Changed file:");
        sb.AppendLine("```csharp");
        sb.AppendLine(changedText.ToString());
        sb.AppendLine("```");

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_format_document ───────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_format_document")]
    [Description(
        "Format a .cs file using Roslyn's formatter (same engine as Visual Studio's " +
        "Format Document). Returns the formatted text without writing to disk.")]
    public static async Task<string> FormatDocument(
        IWorkspaceHost host,
        [Description("Path to the .cs file")] string file,
        CancellationToken ct = default)
    {
        var doc = await host.GetDocumentAsync(file, ct);
        if (doc == null)
            return $"Error: '{file}' is not part of a loaded project.";

        // Apply Roslyn's default formatting rules.
        var root = await doc.GetSyntaxRootAsync(ct);
        if (root == null)
            return "Error: could not parse file.";

        var formatted = Formatter.Format(root, doc.Project.Solution.Workspace);

        var sb = new StringBuilder();
        sb.AppendLine("Formatted file:");
        sb.AppendLine("```csharp");
        sb.AppendLine(formatted.ToFullString());
        sb.AppendLine("```");

        return sb.ToString().TrimEnd();
    }

    // ── helper: discover code fixes via Roslyn's CodeFix provider infrastructure ─

    /// <summary>
    /// Finds code fixes by reflecting over loaded assemblies for exported
    /// CodeFixProviders (MEF's ExportCodeFixProviderAttribute). This is a
    /// simplified version of what Visual Studio's light-bulb does — a full
    /// implementation would compose the entire analyzer/fixer MEF graph.
    /// Returns empty until a provider assembly is loaded into the process.
    /// </summary>
    private static async Task<List<CodeAction>> FindCodeFixesAsync(
        Document document,
        TextSpan span,
        string diagnosticId,
        CancellationToken ct)
    {
        var fixes = new List<CodeAction>();

        // Get diagnostics that match the requested ID.
        var root = await document.GetSyntaxRootAsync(ct);
        var allDiags = new List<Diagnostic>();

        if (root != null)
        {
            allDiags.AddRange(root.GetDiagnostics()
                .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase)));
        }

        var compilation = await document.Project.GetCompilationAsync(ct);
        if (compilation != null)
        {
            allDiags.AddRange(compilation.GetDiagnostics()
                .Where(d => d.Location.IsInSource &&
                            string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase) &&
                            d.Location.SourceTree?.FilePath == document.FilePath));
        }

        if (allDiags.Count == 0)
            return fixes;

        // Discover CodeFixProviders via reflection from all loaded assemblies.
        var providerTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => SafeGetTypes(a))
            .Where(t => typeof(CodeFixProvider).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttribute<ExportCodeFixProviderAttribute>() != null)
            .ToList();

        foreach (var providerType in providerTypes)
        {
            CodeFixProvider? provider;
            try
            {
                provider = (CodeFixProvider?)Activator.CreateInstance(providerType);
            }
            catch
            {
                continue;
            }

            if (provider == null)
                continue;

            foreach (var diag in allDiags)
            {
                var context = new CodeFixContext(document, diag, (action, _) => fixes.Add(action), ct);
                try
                {
                    await provider.RegisterCodeFixesAsync(context);
                }
                catch
                {
                    // Provider threw — skip it, continue with others.
                }
            }
        }

        return fixes;
    }

    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch
        {
            return Type.EmptyTypes;
        }
    }
}
