using System.ComponentModel;
using System.Text;

using Microsoft.CodeAnalysis;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Workspace;

namespace RoslynMcpServer.Tools;

/// <summary>
/// Project management tools — lets the AI explicitly load/reload a .sln,
/// .slnx or .csproj, or check what's currently loaded. Normally the host auto-discovers
/// the project, but these give the AI control when needed (e.g. switching
/// between solutions, forcing a reload after package changes).
/// </summary>
[McpServerToolType]
public static class ProjectManagementTools
{
    // ── roslyn_csharp_load_project ──────────────────────────────────────────
    [McpServerTool(Name = "roslyn_csharp_load_project")]
    [Description(
        "C# solutions/projects only. Load a .sln, .slnx or .csproj so all subsequent queries get full semantic analysis " +
        "(NuGet references, cross-project navigation, real type resolution). By default " +
        "the server auto-discovers the nearest .sln/.slnx/.csproj from any .cs file, so you " +
        "usually don't need this. Use it when: (a) you want to switch to a different " +
        "project, or (b) you added a new NuGet package and need a full reload.")]
    public static async Task<string> LoadProject(
        IWorkspaceHost host,
        [Description("Path to a .sln, .slnx or .csproj file")] string path,
        CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(path);
        if (!File.Exists(abs))
            return $"Error: file not found: {abs}";

        var ext = Path.GetExtension(abs);
        if (!ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return $"Error: expected .sln, .slnx or .csproj, got: {abs}";

        try
        {
            await host.LoadProjectAsync(abs, ct);

            var projects = host.GetProjects();
            var sb = new StringBuilder();
            sb.AppendLine($"Loaded: {Path.GetFileName(abs)}");
            sb.AppendLine($"Projects: {projects.Count}");

            foreach (var p in projects)
            {
                sb.AppendLine($"  [{p.Name}] {p.Language} — {p.DocumentIds.Count} docs, {p.MetadataReferences.Count} refs");
            }

            sb.AppendLine();
            sb.AppendLine("Full semantic analysis is now active — hover, find_references,");
            sb.AppendLine("goto_definition, and code_fixes will resolve NuGet types correctly.");

            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            return $"Error: timed out while loading '{abs}'. The solution may be too large. " +
                   "Try loading a single .csproj instead, or check for circular references.";
        }
        catch (Exception ex)
        {
            return $"Error loading project: {ex.Message}. " +
                   "Ensure the path points to a valid .sln, .slnx or .csproj and MSBuild is installed.";
        }
    }

    // ── roslyn_csharp_workspace_info ────────────────────────────────────────
    [McpServerTool(Name = "roslyn_csharp_workspace_info")]
    [Description(
        "C# solutions/projects only. Report the current workspace status: is a project loaded, which projects, " +
        "document/reference counts. Run this first when you need to understand the " +
        "project structure or check if full semantic analysis is active.")]
    public static async Task<string> WorkspaceInfo(
        IWorkspaceHost host,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Loading in progress — show progress so the AI knows to wait.
        if (host.IsLoading)
        {
            sb.AppendLine("Status: loading project (Roslyn is parsing — please wait...)");
            sb.AppendLine($"Loading: {host.LoadedProjectPath ?? "(unknown)"}");
            sb.AppendLine();
            sb.AppendLine("Queries will auto-wait until loading completes. No need to retry.");
            return sb.ToString();
        }

        if (!host.IsProjectLoaded)
        {
            sb.AppendLine("Status: standalone mode (no project loaded)");
            sb.AppendLine();
            sb.AppendLine("Queries on .cs files use adhoc compilation (base BCL refs only).");
            sb.AppendLine("NuGet types and cross-project references won't resolve.");
            sb.AppendLine();
            sb.AppendLine("To enable full analysis, call roslyn_csharp_load_project with a .sln/.slnx/.csproj,");
            sb.AppendLine("or just query a .cs file — the server auto-discovers its project.");
            return sb.ToString();
        }

        var solution = host.CurrentSolution!;
        sb.AppendLine($"Status: project loaded (full semantic analysis active)");
        sb.AppendLine($"Solution: {Path.GetFileName(solution.FilePath ?? "(unsaved)")}");
        sb.AppendLine($"Projects: {solution.Projects.Count()}");
        sb.AppendLine();

        foreach (var project in solution.Projects)
        {
            sb.AppendLine($"  [{project.Name}]");
            sb.AppendLine($"    Language: {project.Language}");
            sb.AppendLine($"    OutputType: {project.CompilationOptions?.OutputKind}");
            sb.AppendLine($"    AssemblyName: {project.AssemblyName}");
            sb.AppendLine($"    Documents: {project.DocumentIds.Count}");
            sb.AppendLine($"    References: {project.MetadataReferences.Count}");
            sb.AppendLine($"    ProjectRefs: {project.ProjectReferences.Count()}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_csharp_project_references ────────────────────────────────────
    [McpServerTool(Name = "roslyn_csharp_project_references")]
    [Description(
        "C# only. List project-to-project references and assembly (DLL/NuGet) references " +
        "for a project. Give the project name or .csproj path.")]
    public static async Task<string> ProjectReferences(
        IWorkspaceHost host,
        [Description("Project name or .csproj file path")] string project,
        CancellationToken ct = default)
    {
        var proj = FindProject(host, project);
        if (proj == null)
            return $"Project '{project}' not found. Call roslyn_csharp_load_project first.";

        var sb = new StringBuilder();
        sb.AppendLine($"Project references for [{proj.Name}]:");

        foreach (var pref in proj.ProjectReferences)
        {
            var refProj = proj.Solution.GetProject(pref.ProjectId);
            sb.AppendLine($"  -> {refProj?.Name ?? "(unknown)"}");
        }

        sb.AppendLine();
        sb.AppendLine($"Assembly references ({proj.MetadataReferences.Count}):");
        foreach (var mr in proj.MetadataReferences.Take(50))
        {
            var display = mr is Microsoft.CodeAnalysis.PortableExecutableReference pe
                ? Path.GetFileName(pe.FilePath ?? "?")
                : mr.GetType().Name;
            sb.AppendLine($"  - {display}");
        }

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_csharp_nuget_packages ────────────────────────────────────────
    [McpServerTool(Name = "roslyn_csharp_nuget_packages")]
    [Description(
        "C# only. List NuGet packages referenced by a project. Give the project name or .csproj path.")]
    public static async Task<string> NuGetPackages(
        IWorkspaceHost host,
        [Description("Project name or .csproj file path")] string project,
        CancellationToken ct = default)
    {
        var proj = FindProject(host, project);
        if (proj == null)
            return $"Project '{project}' not found. Call roslyn_csharp_load_project first.";

        var packages = new List<string>();

        foreach (var mr in proj.MetadataReferences)
        {
            if (mr is not Microsoft.CodeAnalysis.PortableExecutableReference pe ||
                string.IsNullOrEmpty(pe.FilePath))
                continue;

            var idx = pe.FilePath.IndexOf(".nuget", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                idx = pe.FilePath.IndexOf("packages", StringComparison.OrdinalIgnoreCase);

            if (idx >= 0)
                packages.Add(Path.GetFileName(Path.GetDirectoryName(pe.FilePath) ?? "?"));
        }

        if (packages.Count == 0)
            return $"No NuGet packages found for [{proj.Name}].";

        var sb = new StringBuilder();
        sb.AppendLine($"NuGet packages for [{proj.Name}] ({packages.Count}):");
        foreach (var pkg in packages.Distinct().OrderBy(p => p))
            sb.AppendLine($"  - {pkg}");

        return sb.ToString().TrimEnd();
    }

    // ── roslyn_csharp_project_status ────────────────────────────────────────
    [McpServerTool(Name = "roslyn_csharp_project_status")]
    [Description(
        "C# only. Get the overall compilation status of a project: error count, warning " +
        "count, and the first few errors. Use for 'is the project healthy' checks.")]
    public static async Task<string> ProjectStatus(
        IWorkspaceHost host,
        [Description("Project name or .csproj file path")] string project,
        CancellationToken ct = default)
    {
        var proj = FindProject(host, project);
        if (proj == null)
            return $"Project '{project}' not found. Call roslyn_csharp_load_project first.";

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
                sb.AppendLine($"  {Path.GetFileName(loc.Path ?? "?")}:{loc.StartLinePosition.Line + 1} [{e.Id}] {e.GetMessage()}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static Project? FindProject(IWorkspaceHost host, string nameOrPath)
    {
        var projects = host.GetProjects();

        var proj = projects.FirstOrDefault(p =>
            string.Equals(p.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));

        proj ??= projects.FirstOrDefault(p =>
            p.FilePath != null &&
            (string.Equals(p.FilePath, nameOrPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Path.GetFileName(p.FilePath), nameOrPath, StringComparison.OrdinalIgnoreCase)));

        proj ??= projects.FirstOrDefault(p =>
            p.Name.Contains(nameOrPath, StringComparison.OrdinalIgnoreCase));

        return proj;
    }
}
