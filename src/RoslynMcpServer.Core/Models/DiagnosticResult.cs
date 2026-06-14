using System.Text.Json.Serialization;

namespace RoslynMcpServer.Core.Models;

/// <summary>
/// One Roslyn diagnostic, formatted for JSON return to the LLM agent.
/// Mirrors the fields an LLM needs to understand and fix a problem: where it is,
/// how bad it is, a stable identifier for the rule, and the human-readable message.
/// </summary>
public sealed record DiagnosticResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("column")]
    public int Column { get; init; }

    [JsonPropertyName("end_line")]
    public int EndLine { get; init; }

    [JsonPropertyName("end_column")]
    public int EndColumn { get; init; }

    /// <summary>
    /// Human-readable single-line form for text MCP responses:
    /// "Foo.cs(12,9): error CS0103: The name 'x' does not exist"
    /// </summary>
    public override string ToString()
    {
        var loc = string.IsNullOrEmpty(File) ? "?" : $"{Path.GetFileName(File)}({Line},{Column})";
        return $"{loc}: {Severity.ToLowerInvariant()} {Id}: {Message}";
    }
}
