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
// On startup we auto-detect a .sln/.slnx/.csproj in the current directory. If found,
// full semantic analysis is active from the start. If not, the server runs in
// standalone mode until the AI loads a project (roslyn_csharp_load_project) or a .cs
// file query triggers upward auto-discovery.
//
// When a project is loaded, a WorkspaceWatcher monitors .cs/.csproj files and
// incrementally reloads the compilation on change (500ms debounce).
// ─────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    // MCP stdio: ALL logging must go to stderr. stdout is reserved for
    // JSON-RPC only — any log line on stdout corrupts the protocol.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var workspaceHost = new UnifiedWorkspaceHost();

// Auto-detect + load .sln/.slnx/.csproj in the working directory, then start the
// file watcher if a project was found. Blocking so the first tool call always
// sees the loaded state.
await AutoDetectAndWatchAsync(workspaceHost);

builder.Services.AddSingleton<IWorkspaceHost>(workspaceHost);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Build the host with a using block so Dispose runs on exit — this releases
// the MSBuildWorkspace file locks, FileSystemWatcher handles, and the load
// semaphore cleanly, even on Ctrl+C / SIGTERM / stdin-EOF shutdown.
using var app = builder.Build();

// Register graceful-shutdown cleanup: dispose the workspace host and watcher
// when the application is stopping (covers stdin-EOF, Ctrl+C, SIGTERM).
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    try { workspaceHost.Dispose(); } catch { /* best-effort cleanup */ }
});

await app.RunAsync();

// ── helpers ──────────────────────────────────────────────────────────────────

static async Task AutoDetectAndWatchAsync(UnifiedWorkspaceHost host)
{
    try
    {
        var cwd = Environment.CurrentDirectory;

        // Search for .sln/.slnx first (multi-project), then .csproj.
        var sln = Directory.GetFiles(cwd, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(cwd, "*.slnx", SearchOption.TopDirectoryOnly))
            .ToArray();
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

        Console.Error.WriteLine("[roslyn-mcp] No .sln/.slnx/.csproj in cwd — starting in standalone mode.");
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
