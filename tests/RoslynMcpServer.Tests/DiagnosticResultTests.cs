using RoslynMcpServer.Core.Models;

using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Tests for the DiagnosticResult DTO and its ToString formatting — the
/// shape the LLM actually sees. Verifies that the output format is parseable
/// and that severity/ordering info is correct.
/// </summary>
public class DiagnosticResultTests
{
    [Fact]
    public void ToString_Produces_FileLine_Error_Format()
    {
        var d = new DiagnosticResult
        {
            Id = "CS0103",
            Severity = "Error",
            Message = "name not found",
            File = "/test/Program.cs",
            Line = 12,
            Column = 9,
            EndLine = 12,
            EndColumn = 20,
        };

        var s = d.ToString();
        Assert.Contains("Program.cs(12,9)", s);
        Assert.Contains("error", s.ToLowerInvariant());
        Assert.Contains("CS0103", s);
        Assert.Contains("name not found", s);
    }

    [Fact]
    public void ToString_Handles_Empty_File()
    {
        var d = new DiagnosticResult
        {
            Id = "CS5001",
            Severity = "Error",
            Message = "no entry point",
        };

        var s = d.ToString();
        Assert.Contains("?", s); // empty file shows "?"
        Assert.Contains("CS5001", s);
    }

    [Fact]
    public void ToString_Handles_Warning_Severity()
    {
        var d = new DiagnosticResult
        {
            Id = "CS0219",
            Severity = "Warning",
            Message = "unused variable",
            File = "Foo.cs",
            Line = 5,
            Column = 1,
            EndLine = 5,
            EndColumn = 2,
        };

        var s = d.ToString();
        Assert.Contains("warning", s.ToLowerInvariant());
    }
}
