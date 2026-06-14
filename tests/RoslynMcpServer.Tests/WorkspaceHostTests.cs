using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynMcpServer.Core.Analysis;
using RoslynMcpServer.Core.Workspace;

using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Tests for the AdhocWorkspaceHost's enhanced IWorkspaceHost interface —
/// verifies null-safety, Document resolution, project listing, and reload
/// semantics. These exercise the Core layer without MCP.
/// </summary>
public class WorkspaceHostTests
{
    [Fact]
    public async Task AdhocHost_Workspace_And_Solution_Are_Null()
    {
        var host = new AdhocWorkspaceHost();
        Assert.Null(host.Workspace);
        Assert.Null(host.CurrentSolution);
    }

    [Fact]
    public async Task AdhocHost_IsReady_Is_True()
    {
        var host = new AdhocWorkspaceHost();
        Assert.True(host.IsReady);
    }

    [Fact]
    public async Task AdhocHost_GetDocument_Returns_Null()
    {
        var host = new AdhocWorkspaceHost();
        var doc = await host.GetDocumentAsync("/any/path.cs");
        Assert.Null(doc);
    }

    [Fact]
    public async Task AdhocHost_GetProjects_Returns_Empty()
    {
        var host = new AdhocWorkspaceHost();
        Assert.Empty(host.GetProjects());
    }

    [Fact]
    public async Task AdhocHost_Reload_Clears_Cache()
    {
        var host = new AdhocWorkspaceHost();
        var file = Path.Combine(Path.GetTempPath(), $"ws_test_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "class C { }");

        try
        {
            _ = await host.GetCompilationAsync(file);
            await host.ReloadAsync(); // should not throw
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AdhocHost_GetCompilation_Nullable_Returns_NonNull()
    {
        var host = new AdhocWorkspaceHost();
        var file = Path.Combine(Path.GetTempPath(), $"ws_test_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "class C { }");

        try
        {
            var compilation = await host.GetCompilationAsync(file);
            Assert.NotNull(compilation);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
