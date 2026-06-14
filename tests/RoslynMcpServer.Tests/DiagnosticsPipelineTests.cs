using System.Collections.Generic;

using Microsoft.CodeAnalysis;

using RoslynMcpServer.Core.Analysis;
using RoslynMcpServer.Core.Models;
using RoslynMcpServer.Core.Workspace;

using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Verifies the AdhocWorkspaceHost + DiagnosticAnalyzer pipeline without going
/// through MCP. These are the skeleton stage's smoke tests: a known-broken .cs
/// file must surface diagnostics with the right Id and location.
/// </summary>
public class DiagnosticsPipelineTests
{
    [Fact]
    public async Task File_With_Syntax_Error_Produces_Diagnostic()
    {
        var host = new AdhocWorkspaceHost();
        var broken = Path.Combine(Path.GetTempPath(), $"roslyn_mcp_test_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(broken, """
            class Broken {
                void M() {
                    int x =   // missing initializer — syntax error
                }
            }
            """);

        try
        {
            var compilation = await host.GetCompilationAsync(broken);
            Assert.NotNull(compilation);
            var diagnostics = DiagnosticAnalyzer.Extract(compilation, DiagnosticSeverity.Error);

            Assert.NotEmpty(diagnostics);
            Assert.Contains(diagnostics, d => d.Severity == nameof(DiagnosticSeverity.Error));
        }
        finally
        {
            File.Delete(broken);
        }
    }

    [Fact]
    public async Task Valid_File_Produces_No_Errors()
    {
        var host = new AdhocWorkspaceHost();
        var valid = Path.Combine(Path.GetTempPath(), $"roslyn_mcp_test_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(valid, """
            using System;

            class Hello {
                static void Main() {
                    Console.WriteLine("hello");
                }
            }
            """);

        try
        {
            var compilation = await host.GetCompilationAsync(valid);
            Assert.NotNull(compilation);
            var diagnostics = DiagnosticAnalyzer.Extract(compilation, DiagnosticSeverity.Error);

            // Should have zero errors (warnings OK from the adhoc compilation setup).
            var errors = diagnostics.Where(d => d.Severity == nameof(DiagnosticSeverity.Error)).ToList();
            Assert.Empty(errors);
        }
        finally
        {
            File.Delete(valid);
        }
    }

    [Fact]
    public async Task Missing_Type_Reference_Surfaces_As_Error()
    {
        var host = new AdhocWorkspaceHost();
        var file = Path.Combine(Path.GetTempPath(), $"roslyn_mcp_test_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, """
            class C {
                void M() {
                    var x = NonExistentNamespace.NonExistentType.Create();
                }
            }
            """);

        try
        {
            var compilation = await host.GetCompilationAsync(file);
            Assert.NotNull(compilation);
            var diagnostics = DiagnosticAnalyzer.Extract(compilation, DiagnosticSeverity.Error);

            // Should surface CS0103 "name does not exist" or CS0246 "type not found"
            // for the missing reference. Roslyn may choose either depending on context.
            Assert.Contains(diagnostics, d => d.Id == "CS0103" || d.Id == "CS0246");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FormatAsText_Shows_Counts_And_Locations()
    {
        var diags = new List<DiagnosticResult>
        {
            new()
            {
                Id = "CS1002",
                Severity = "Error",
                Message = "; expected",
                File = "/test/Program.cs",
                Line = 12,
                Column = 9,
                EndLine = 12,
                EndColumn = 10,
            },
            new()
            {
                Id = "CS0219",
                Severity = "Warning",
                Message = "Variable 'x' is assigned but never used",
                File = "/test/Program.cs",
                Line = 8,
                Column = 17,
                EndLine = 8,
                EndColumn = 18,
            },
        };

        var text = DiagnosticAnalyzer.FormatAsText(diags);

        Assert.Contains("1 error", text);
        Assert.Contains("1 warning", text);
        Assert.Contains("CS1002", text);
        Assert.Contains("Program.cs(12,9)", text);
    }
}
