using Microsoft.CodeAnalysis;

using RoslynMcpServer.Core.Models;

namespace RoslynMcpServer.Core.Analysis;

/// <summary>
/// Extracts Roslyn diagnostics from a compilation and maps them to the
/// agent-friendly <see cref="DiagnosticResult"/> shape. Filters to only what the
/// LLM needs: errors and warnings, with 1-based line numbers, file names, and the
/// stable diagnostic Id (CS0123, IDE00xx, etc.) so the model can look up the
/// rule or correlate fixes.
/// </summary>
public static class DiagnosticAnalyzer
{
    /// <summary>
    /// Returns diagnostics at or above the given severity. Defaults to warnings
    /// and errors, since info/suggestions are noise for an LLM iterating on code.
    /// </summary>
    public static IReadOnlyList<DiagnosticResult> Extract(
        Compilation? compilation,
        DiagnosticSeverity minimum = DiagnosticSeverity.Warning)
    {
        if (compilation == null)
            return Array.Empty<DiagnosticResult>();

        var diags = compilation.GetDiagnostics();
        var outList = new List<DiagnosticResult>(diags.Length);

        foreach (var d in diags)
        {
            if (d.Severity < minimum)
                continue;

            // Skip "hidden" diagnostics — they are internal suppressions, not errors.
            if (d.Severity == DiagnosticSeverity.Hidden)
                continue;

            var result = new DiagnosticResult
            {
                Id = d.Id,
                Severity = d.Severity.ToString(),
                Message = d.GetMessage(),
            };

            var loc = d.Location;
            if (loc.IsInSource && loc.SourceTree is { } tree)
            {
                var lineSpan = loc.GetLineSpan();
                var start = lineSpan.StartLinePosition;
                var end = lineSpan.EndLinePosition;

                result = result with
                {
                    File = tree.FilePath,
                    Line = start.Line + 1,       // Roslyn is 0-based; LLMs want 1-based
                    Column = start.Character + 1,
                    EndLine = end.Line + 1,
                    EndColumn = end.Character + 1,
                };
            }

            outList.Add(result);
        }

        return outList;
    }

    /// <summary>
    /// Formats diagnostics as plain text for the MCP tool response — one per line,
    /// grouped by file. The most compact useful shape for an LLM to consume.
    /// </summary>
    public static string FormatAsText(IReadOnlyList<DiagnosticResult> diagnostics)
    {
        if (diagnostics.Count == 0)
            return "✓ No diagnostics — file compiles cleanly.";

        var errors = diagnostics.Count(d => d.Severity == nameof(DiagnosticSeverity.Error));
        var warnings = diagnostics.Count(d => d.Severity == nameof(DiagnosticSeverity.Warning));
        var summary = $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}";

        var lines = new List<string>(diagnostics.Count + 2);
        lines.Add($"Found {summary}:");

        // Group by file, then sort by line within each file.
        foreach (var group in diagnostics.GroupBy(d => d.File).OrderBy(g => g.Key))
        {
            if (!string.IsNullOrEmpty(group.Key))
                lines.Add($"");
            foreach (var d in group.OrderBy(d => d.Line))
                lines.Add($"  {d}");
        }

        return string.Join('\n', lines);
    }
}
