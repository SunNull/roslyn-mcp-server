using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Core.Workspace;

/// <summary>
/// Abstracts how a Roslyn <see cref="Workspace"/> and its compilations are
/// obtained. The skeleton stage used <see cref="AdhocWorkspaceHost"/> (manual
/// references); production swaps in <see cref="MSBuildWorkspaceHost"/> which
/// loads .sln/.csproj and resolves all NuGet/project references via MSBuild.
/// </summary>
public interface IWorkspaceHost
{
    /// <summary>
    /// The underlying Roslyn workspace, or null when the host is file-only
    /// (AdhocWorkspaceHost). Tools that need Solution/Document-level APIs
    /// (find_references, code_fixes) use this; simpler tools use
    /// <see cref="GetCompilationAsync"/>.
    /// </summary>
    Microsoft.CodeAnalysis.Workspace? Workspace { get; }

    /// <summary>
    /// The currently loaded solution, or null if only a single project/file
    /// is loaded. null for AdhocWorkspaceHost.
    /// </summary>
    Solution? CurrentSolution { get; }

    /// <summary>
    /// True when the host has finished its initial project load. False during
    /// the async MSBuild restore/load. Callers should check this and surface
    /// a "still loading" message if false.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Returns a Roslyn compilation for the given path. The path may be a .cs
    /// file, a .csproj, a .sln, or a directory. For a .cs file inside a loaded
    /// project, returns that project's compilation. For a standalone file,
    /// returns an ad-hoc compilation.
    /// </summary>
    Task<Compilation?> GetCompilationAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Resolves a .cs file path to its containing Roslyn <see cref="Document"/>,
    /// or null if the file is not part of a loaded project. Tools like hover,
    /// goto_definition, and find_references need a Document to get a
    /// SemanticModel.
    /// </summary>
    Task<Document?> GetDocumentAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Returns all projects in the loaded solution, or a single-project list.
    /// Empty when the host is file-only.
    /// </summary>
    IReadOnlyList<Project> GetProjects();

    /// <summary>
    /// Reloads the workspace from disk. Called by the watcher when files change
    /// or by an explicit "refresh" tool call.
    /// </summary>
    Task ReloadAsync(CancellationToken ct = default);
}
