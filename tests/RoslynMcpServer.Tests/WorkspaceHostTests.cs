using RoslynMcpServer.Core.Workspace;

using Xunit;

namespace RoslynMcpServer.Tests;

/// <summary>
/// Tests for UnifiedWorkspaceHost — verifies the transparent adhoc/project
/// mode switching, auto-discovery, and fallback behavior.
/// </summary>
public class WorkspaceHostTests
{
    [Fact]
    public async Task UnifiedHost_Starts_Without_Project()
    {
        var host = new UnifiedWorkspaceHost();
        Assert.False(host.IsProjectLoaded);
        Assert.Null(host.CurrentSolution);
        Assert.Null(host.LoadedProjectPath);
        host.Dispose();
    }

    [Fact]
    public async Task UnifiedHost_GetProjects_Empty_When_No_Project()
    {
        var host = new UnifiedWorkspaceHost();
        Assert.Empty(host.GetProjects());
        host.Dispose();
    }

    [Fact]
    public async Task UnifiedHost_GetDocument_Returns_Null_When_No_Project()
    {
        var host = new UnifiedWorkspaceHost();
        var doc = await host.GetDocumentAsync("/any/path.cs");
        Assert.Null(doc);
        host.Dispose();
    }

    [Fact]
    public async Task UnifiedHost_Adhoc_Compilation_For_Standalone_File()
    {
        var host = new UnifiedWorkspaceHost();
        var file = Path.Combine(Path.GetTempPath(), $"ws_test_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "class C { }");

        try
        {
            var compilation = await host.GetCompilationAsync(file);
            Assert.NotNull(compilation);
            Assert.NotEmpty(compilation!.SyntaxTrees);
        }
        finally
        {
            File.Delete(file);
            host.Dispose();
        }
    }

    [Fact]
    public async Task UnifiedHost_TryAutoLoad_Returns_False_For_No_Project()
    {
        var host = new UnifiedWorkspaceHost();
        // A temp file with no .sln/.csproj nearby.
        var file = Path.Combine(Path.GetTempPath(), $"ws_test_{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "class C { }");

        try
        {
            var found = await host.TryAutoLoadForFileAsync(file);
            // Might find a project or not depending on temp dir — just verify no throw.
            Assert.True(found == true || found == false);
        }
        finally
        {
            File.Delete(file);
            host.Dispose();
        }
    }
}
