using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Workspace;

// ── entry point ──────────────────────────────────────────────────────────────
// Parse --workspace <path> to auto-load a .sln/.csproj on startup. When
// provided, MSBuildWorkspaceHost is used; otherwise the lightweight
// AdhocWorkspaceHost handles single-file queries. The watcher keeps the
// compilation fresh for real-time feedback.
// ─────────────────────────────────────────────────────────────────────────────

var workspacePath = ParseWorkspaceArg(args);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Warning;
});

if (workspacePath != null)
{
    var msbuildHost = new MSBuildWorkspaceHost();
    try
    {
        await msbuildHost.LoadAsync(workspacePath);
        Console.Error.WriteLine($"[roslyn-mcp] Loaded workspace: {workspacePath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[roslyn-mcp] Failed to load workspace '{workspacePath}': {ex.Message}");
    }

    builder.Services.AddSingleton<IWorkspaceHost>(msbuildHost);

    var watcher = new WorkspaceWatcher(msbuildHost, Path.GetDirectoryName(workspacePath) ?? workspacePath);
    watcher.Start();
    Console.Error.WriteLine($"[roslyn-mcp] Watching for changes in: {Path.GetDirectoryName(workspacePath) ?? workspacePath}");

    AppDomain.CurrentDomain.ProcessExit += (_, _) => watcher.Dispose();
}
else
{
    builder.Services.AddSingleton<IWorkspaceHost, AdhocWorkspaceHost>();
    Console.Error.WriteLine("[roslyn-mcp] Running in standalone mode (no workspace loaded).");
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

// ── local functions ──────────────────────────────────────────────────────────

static string? ParseWorkspaceArg(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--workspace" or "-w")
            return args[i + 1];
    }
    return null;
}
