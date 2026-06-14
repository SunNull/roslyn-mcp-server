using System.ComponentModel;

using Microsoft.CodeAnalysis;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Analysis;
using RoslynMcpServer.Core.Workspace;

namespace RoslynMcpServer.Tools;

/// <summary>
/// MCP tools exposing Roslyn compilation diagnostics. Marked with
/// <see cref="McpServerToolTypeAttribute"/> so the SDK's
/// <c>WithToolsFromAssembly()</c> auto-discovers and registers every method
/// tagged <see cref="McpServerToolAttribute"/>.
/// </summary>
[McpServerToolType]
public static class DiagnosticsTools
{
    /// <summary>
    /// Returns Roslyn compilation diagnostics (errors + warnings) for a C# file
    /// or directory. This is the skeleton stage's single tool — it validates the
    /// end-to-end MCP pipeline before we add hover/references/code-fixes.
    /// </summary>
    [McpServerTool(Name = "roslyn_diagnostics")]
    [Description(
        "Get Roslyn compilation diagnostics for a C# file or directory. " +
        "Returns errors and warnings with exact file:line:column locations and " +
        "diagnostic IDs (CS0123, etc.). Use after editing a .cs file to check if " +
        "it compiles — catches syntax errors, missing usings, type mismatches, " +
        "and unresolved symbols in real time without a full dotnet build.")]
    public static async Task<string> GetDiagnostics(
        IWorkspaceHost host,
        [Description("Path to a .cs file or a directory containing .cs files. Relative paths resolve against the server's working directory.")]
        string path,
        [Description("Minimum severity to report: \"error\", \"warning\", or \"info\". Default: \"warning\" (errors + warnings).")]
        string minimum_severity = "warning",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Error: 'path' is required. Provide a .cs file or directory.";

        var minSeverity = minimum_severity.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "info" => DiagnosticSeverity.Info,
            _ => DiagnosticSeverity.Warning,
        };

        try
        {
            var compilation = await host.GetCompilationAsync(path, ct);
            var diagnostics = DiagnosticAnalyzer.Extract(compilation, minSeverity);
            return DiagnosticAnalyzer.FormatAsText(diagnostics);
        }
        catch (FileNotFoundException ex)
        {
            return $"Error: file or directory not found: {ex.FileName ?? path}";
        }
        catch (Exception ex)
        {
            return $"Error analyzing '{path}': {ex.Message}";
        }
    }
}
