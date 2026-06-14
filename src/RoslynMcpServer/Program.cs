using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using RoslynMcpServer.Core.Workspace;

// ── entry point ──────────────────────────────────────────────────────────────
// The server uses UnifiedWorkspaceHost, which transparently handles both
// project-grade analysis (MSBuildWorkspace) and standalone-file analysis
// (adhoc). The AI doesn't need to pick a mode — it just queries files.
//
// On startup we auto-detect a .sln/.csproj in the current directory. If found,
// full semantic analysis is active from the start. If not, the server runs in
// standalone mode until the AI loads a project (roslyn_load_project) or a .cs
// file query triggers upward auto-discovery.
//
// When a project is loaded, a WorkspaceWatcher monitors .cs/.csproj files and
// incrementally reloads the compilation on change (500ms debounce).
// ─────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Warning;
});

var host = new UnifiedWorkspaceHost();

// Auto-detect + load .sln/.csproj in the working directory, then start the
// file watcher if a project was found. Blocking so the first tool call always
// sees the loaded state.
await AutoDetectAndWatchAsync(host);

builder.Services.AddSingleton<IWorkspaceHost>(host);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

// ── helpers ──────────────────────────────────────────────────────────────────

static async Task AutoDetectAndWatchAsync(UnifiedWorkspaceHost host)
{
    try
    {
        var cwd = Environment.CurrentDirectory;

        // Search for .sln first (multi-project), then .csproj.
        var sln = Directory.GetFiles(cwd, "*.sln", SearchOption.TopDirectoryOnly);
        if (sln.Length > 0)
        {
            await host.LoadProjectAsync(sln[0]);
            Console.Error.WriteLine($"[roslyn-mcp] Auto-loaded solution: {sln[0]}");
            StartWatcher(host, cwd);
            return;
        }

        var csproj = Directory.GetFiles(cwd, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csproj.Length > 0)
        {
            await host.LoadProjectAsync(csproj[0]);
            Console.Error.WriteLine($"[roslyn-mcp] Auto-loaded project: {csproj[0]}");
            StartWatcher(host, cwd);
            return;
        }

        Console.Error.WriteLine("[roslyn-mcp] No .sln/.csproj in cwd — starting in standalone mode.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[roslyn-mcp] Auto-detect failed: {ex.Message} — starting in standalone mode.");
    }
}

static void StartWatcher(IWorkspaceHost host, string root)
{
    try
    {
        var watcher = new WorkspaceWatcher(host, root);
        watcher.Start();
        Console.Error.WriteLine($"[roslyn-mcp] Watching for changes in: {root}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => watcher.Dispose();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[roslyn-mcp] Watcher failed to start: {ex.Message}");
    }
}
