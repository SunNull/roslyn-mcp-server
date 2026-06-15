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
    /// or directory — the primary "is this code compilable?" check. AI agents
    /// call this after editing a .cs file to get real-time feedback (syntax
    /// errors, missing usings, type mismatches) without a full dotnet build.
    /// </summary>
    [McpServerTool(Name = "roslyn_csharp_diagnostics")]
    [Description(
        "C# only. Get Roslyn compilation diagnostics for a C# file or directory. " +
        "Returns errors and warnings with exact file:line:column locations and " +
        "diagnostic IDs (CS0123, etc.). Use after editing a .cs or .csx file to check if " +
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
            var result = DiagnosticAnalyzer.FormatAsText(diagnostics);

            // If no project is loaded, the file was analyzed in standalone (adhoc)
            // mode — NuGet types won't resolve. Warn the AI so it knows.
            if (!host.IsProjectLoaded && diagnostics.Count > 0)
            {
                result = "[Standalone mode] NuGet types and cross-project references won't resolve. " +
                         "Call roslyn_csharp_load_project with a .csproj for full analysis.\n\n" + result;
            }

            return result;
        }
        catch (FileNotFoundException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (NotSupportedException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            return $"Error: timed out while analyzing '{path}'. " +
                   "The project may be very large. Try again or use a smaller scope (a single .cs file).";
        }
        catch (Exception ex)
        {
            return $"Error analyzing '{path}': {ex.Message}. " +
                   "If this is a .cs file, ensure a project is loaded (roslyn_csharp_load_project).";
        }
    }
}
