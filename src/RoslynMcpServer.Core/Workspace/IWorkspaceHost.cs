using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Core.Workspace;

/// <summary>
/// Abstracts how a Roslyn <see cref="Workspace"/> and its compilations are
/// obtained. The unified host (<see cref="UnifiedWorkspaceHost"/>) handles
/// both project-level (MSBuildWorkspace) and file-level (Adhoc) analysis
/// automatically — callers just pass a path and the host figures out whether
/// it's part of a loaded project or a standalone file.
/// </summary>
public interface IWorkspaceHost
{
    /// <summary>The underlying Roslyn workspace (null in pure adhoc mode).</summary>
    Microsoft.CodeAnalysis.Workspace? Workspace { get; }

    /// <summary>The currently loaded solution (null until a project is loaded).</summary>
    Solution? CurrentSolution { get; }

    /// <summary>True when a project/solution is loaded and ready for queries.</summary>
    bool IsProjectLoaded { get; }

    /// <summary>True while a project load is in progress (Roslyn parsing).</summary>
    bool IsLoading { get; }

    /// <summary>
    /// If a load is in progress, waits for it to complete. Query methods call
    /// this before checking the solution so they see the loaded state.
    /// </summary>
    Task WaitForLoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a compilation for the given path. Auto-resolves: if the path is
    /// a .cs file inside a loaded project, returns that project's compilation
    /// (with all NuGet references resolved). If it's a standalone .cs file,
    /// returns an adhoc compilation with base references. If it's a .csproj or
    /// .sln, loads it first.
    /// </summary>
    Task<Compilation?> GetCompilationAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Resolves a .cs file to its Roslyn Document in a loaded project, or null
    /// if the file is standalone. Semantic tools (hover, goto_def, references)
    /// need a Document for the real SemanticModel.
    /// </summary>
    Task<Document?> GetDocumentAsync(string filePath, CancellationToken ct = default);

    /// <summary>All projects in the loaded solution (empty if only adhoc).</summary>
    IReadOnlyList<Project> GetProjects();

    /// <summary>
    /// Returns the path of the currently loaded .sln/.slnx/.csproj, or null if none.
    /// Used by the watcher to know what to reload.
    /// </summary>
    string? LoadedProjectPath { get; }

    /// <summary>
    /// automatically on first project query, or explicitly by the AI via the
    /// roslyn_csharp_load_project tool. Subsequent calls reload.
    /// </summary>
    Task LoadProjectAsync(string projectPath, CancellationToken ct = default);

    /// <summary>
    /// Auto-discovers and loads a .sln/.slnx/.csproj by searching upward from a .cs
    /// file's directory (like git finding .git). Returns true if a project was
    /// found and loaded.
    /// </summary>
    Task<bool> TryAutoLoadForFileAsync(string csFilePath, CancellationToken ct = default);
}
