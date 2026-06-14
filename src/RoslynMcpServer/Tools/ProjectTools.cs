using System.ComponentModel;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Workspace;

namespace RoslynMcpServer.Tools;

/// <summary>
/// Project engineering tools — for understanding solution/project structure,
/// references, packages, and overall compilation health. These power "what
/// projects are in this solution", "what packages does X depend on", and
/// "is the project compiling right now" queries.
/// </summary>
[McpServerToolType]
public static class ProjectTools
{
    // ── roslyn_workspace_info ────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_workspace_info")]
    [Description(
        "Get an overview of the loaded workspace: solution/projects, their target " +
        "frameworks, output types, and document counts. Run this first when you " +
        "need to understand the project structure.")]
    public static async Task<string> WorkspaceInfo(
        IWorkspaceHost host,
        CancellationToken ct = default)
    {
        var solution = host.CurrentSolution;
        if (solution == null)
            return "No solution/project loaded. Use the --workspace startup option to load a .sln or .csproj.";

        var sb = new StringBuilder();
        sb.AppendLine($"Solution: {Path.GetFileName(solution.FilePath ?? "(unsaved)")}");
        sb.AppendLine($"Projects: {solution.Projects.Count()}");
        sb.AppendLine();

        foreach (var project in solution.Projects)
        {
            sb.AppendLine($"  [{project.Name}]");
            sb.AppendLine($"    File: {project.FilePath ?? "(none)"}");
            sb.AppendLine($"    Language: {project.Language}");
            sb.AppendLine($"    OutputType: {project.CompilationOptions?.OutputKind}");
            sb.AppendLine($"    AssemblyName: {project.AssemblyName}");
            sb.AppendLine($"    Documents: {project.DocumentIds.Count}");
            sb.AppendLine($"    References: {project.MetadataReferences.Count}");
            sb.AppendLine($"    ProjectRefs: {project.ProjectReferences.Count()}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_project_references ────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_project_references")]
    [Description(
        "List project-to-project references (which other projects this one depends " +
        "on) for a given project. Give the project name or .csproj path.")]
    public static async Task<string> ProjectReferences(
        IWorkspaceHost host,
        [Description("Project name or .csproj file path")] string project,
        CancellationToken ct = default)
    {
        var proj = FindProject(host, project);
        if (proj == null)
            return $"Project '{project}' not found in the loaded solution.";

        var sb = new StringBuilder();
        sb.AppendLine($"Project references for [{proj.Name}]:");

        foreach (var pref in proj.ProjectReferences)
        {
            var refProj = proj.Solution.GetProject(pref.ProjectId);
            sb.AppendLine($"  → {refProj?.Name ?? "(unknown)"} ({refProj?.FilePath ?? "?"})");
        }

        // Also list assembly (metadata) references — the DLLs and NuGet packages.
        sb.AppendLine();
        sb.AppendLine($"Assembly references ({proj.MetadataReferences.Count}):");
        foreach (var mr in proj.MetadataReferences.Take(50))
        {
            var display = mr switch
            {
                PortableExecutableReference pe => Path.GetFileName(pe.FilePath ?? "?"),
                _ => mr.GetType().Name
            };
            sb.AppendLine($"  • {display}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_nuget_packages ────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_nuget_packages")]
    [Description(
        "List NuGet packages referenced by a project, derived from its metadata " +
        "references. Give the project name or .csproj path.")]
    public static async Task<string> NuGetPackages(
        IWorkspaceHost host,
        [Description("Project name or .csproj file path")] string project,
        CancellationToken ct = default)
    {
        var proj = FindProject(host, project);
        if (proj == null)
            return $"Project '{project}' not found in the loaded solution.";

        // NuGet packages show up as metadata references whose paths contain
        // the NuGet package cache directory structure.
        var packages = new List<(string Name, string Path)>();

        foreach (var mr in proj.MetadataReferences)
        {
            if (mr is not PortableExecutableReference pe || string.IsNullOrEmpty(pe.FilePath))
                continue;

            // NuGet packages live under ~/.nuget/packages/<name>/<version>/...
            var idx = pe.FilePath.IndexOf(".nuget", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                idx = pe.FilePath.IndexOf("packages", StringComparison.OrdinalIgnoreCase);

            if (idx >= 0)
            {
                var name = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(pe.FilePath))) ?? "?";
                packages.Add((name, pe.FilePath));
            }
        }

        if (packages.Count == 0)
            return $"No NuGet packages found for [{proj.Name}].";

        var sb = new StringBuilder();
        sb.AppendLine($"NuGet packages for [{proj.Name}] ({packages.Count}):");

        foreach (var pkg in packages.DistinctBy(p => p.Name).OrderBy(p => p.Name))
        {
            // Try to extract version from the path.
            var version = "?";
            var parts = pkg.Path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                    parts[i].Equals(".nuget", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 2 < parts.Length)
                        version = parts[i + 2];
                    break;
                }
            }

            sb.AppendLine($"  • {pkg.Name} (v{version})");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_project_status ────────────────────────────────────────────────
    [McpServerTool(Name = "roslyn_project_status")]
    [Description(
        "Get the overall compilation status of a project: error count, warning " +
        "count, and the first few errors. Use for 'is the project healthy' checks.")]
    public static async Task<string> ProjectStatus(
        IWorkspaceHost host,
        [Description("Project name or .csproj file path")] string project,
        CancellationToken ct = default)
    {
        var proj = FindProject(host, project);
        if (proj == null)
            return $"Project '{project}' not found in the loaded solution.";

        var compilation = await proj.GetCompilationAsync(ct);
        if (compilation == null)
            return $"Could not get compilation for [{proj.Name}].";

        var diags = compilation.GetDiagnostics().ToList();
        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = diags.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"[{proj.Name}] Compilation Status");
        sb.AppendLine($"  Errors:   {errors.Count}");
        sb.AppendLine($"  Warnings: {warnings.Count}");

        if (errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"First {Math.Min(errors.Count, 10)} errors:");
            foreach (var e in errors.Take(10))
            {
                var loc = e.Location.GetLineSpan();
                var f = loc.Path ?? "?";
                sb.AppendLine($"  {Path.GetFileName(f)}:{loc.StartLinePosition.Line + 1} [{e.Id}] {e.GetMessage()}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static Project? FindProject(IWorkspaceHost host, string nameOrPath)
    {
        var projects = host.GetProjects();

        // Try exact name match first.
        var proj = projects.FirstOrDefault(p =>
            string.Equals(p.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));

        // Try file path match.
        proj ??= projects.FirstOrDefault(p =>
            string.Equals(p.FilePath, nameOrPath, StringComparison.OrdinalIgnoreCase) ||
            (p.FilePath != null && string.Equals(Path.GetFileName(p.FilePath), nameOrPath, StringComparison.OrdinalIgnoreCase)));

        // Try partial contains.
        proj ??= projects.FirstOrDefault(p =>
            p.Name.Contains(nameOrPath, StringComparison.OrdinalIgnoreCase));

        return proj;
    }
}
