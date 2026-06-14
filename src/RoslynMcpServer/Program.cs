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
// ─────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Warning;
});

var host = new UnifiedWorkspaceHost();

// Auto-detect a .sln/.csproj in the working directory (non-blocking).
_ = AutoDetectProjectAsync(host);

builder.Services.AddSingleton<IWorkspaceHost>(host);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

// ── helpers ──────────────────────────────────────────────────────────────────

static async Task AutoDetectProjectAsync(UnifiedWorkspaceHost host)
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
            return;
        }

        var csproj = Directory.GetFiles(cwd, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csproj.Length > 0)
        {
            await host.LoadProjectAsync(csproj[0]);
            Console.Error.WriteLine($"[roslyn-mcp] Auto-loaded project: {csproj[0]}");
            return;
        }

        Console.Error.WriteLine("[roslyn-mcp] No .sln/.csproj in cwd — starting in standalone mode.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[roslyn-mcp] Auto-detect failed: {ex.Message} — starting in standalone mode.");
    }
}
