using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Core.Analysis;

/// <summary>
/// Helpers for mapping between the 1-based line/column the LLM provides (from
/// read_file output) and the 0-based Roslyn <see cref="LinePosition"/> it
/// consumes internally. Centralised here so every tool converts the same way.
/// </summary>
public static class PositionMapper
{
    /// <summary>
    /// Converts 1-based line + column + symbol text into a Roslyn
    /// <see cref="LinePosition"/>. The symbol text is used to locate the exact
    /// column when the caller gives a column of 0 (unknown).
    /// </summary>
    public static LinePosition ToLinePosition(
        SourceText sourceText,
        int line1Based,
        int column1Based,
        string? symbolText)
    {
        var line0 = Math.Max(0, line1Based - 1);
        var line = sourceText.Lines[line0];

        // If the caller gave a real column, use it (1-based → 0-based).
        if (column1Based > 0)
            return new LinePosition(line0, Math.Max(0, column1Based - 1));

        // Otherwise search for the symbol text on that line.
        if (!string.IsNullOrEmpty(symbolText))
        {
            var lineText = sourceText.ToString(new TextSpan(line.Start, line.Span.Length));
            var idx = lineText.IndexOf(symbolText, StringComparison.Ordinal);
            if (idx >= 0)
                return new LinePosition(line0, idx);
        }

        return new LinePosition(line0, 0);
    }

    /// <summary>
    /// Returns the text of one line (1-based) from a source file, or "" if out
    /// of range. Used for display in results.
    /// </summary>
    public static string ReadLine(string filePath, int line1Based)
    {
        if (!File.Exists(filePath))
            return "";

        var lines = File.ReadAllLines(filePath);
        var idx = line1Based - 1;
        return idx >= 0 && idx < lines.Length ? lines[idx].Trim() : "";
    }
}
