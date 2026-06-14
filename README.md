# Roslyn MCP Server

**English** | [简体中文](./README.zh-CN.md)

A .NET 11 MCP (Model Context Protocol) server that exposes **Roslyn compiler analysis** to AI coding agents. Gives any MCP-compatible client (Reasonix, Claude Desktop, Cursor, VS Code Copilot) real-time C# code intelligence — diagnostics, type info, references, code fixes — without running `dotnet build`.

## Why?

Normal AI coding workflow for C#:
```
write code → dotnet build → see errors → fix → rebuild → ...
```

With Roslyn MCP Server:
```
write code → roslyn_diagnostics → instant feedback (like Visual Studio's red squiggles)
```

The agent gets **compiler-grade analysis in real time**, powered by the same Roslyn engine that drives Visual Studio.

## 18 Tools

| Tool | What it does |
|------|-------------|
| `roslyn_diagnostics` | Compilation errors + warnings (CS0123, etc.) |
| `roslyn_load_project` | Load/switch a .sln or .csproj (usually auto-detected) |
| `roslyn_workspace_info` | Current status: is a project loaded? which projects? |
| `roslyn_hover` | Type signature, docs, member info for a symbol |
| `roslyn_goto_definition` | Jump to where a symbol is defined |
| `roslyn_find_references` | All references across the solution |
| `roslyn_completion` | Intelligent code completion suggestions |
| `roslyn_signature_help` | Method parameter info |
| `roslyn_find_symbols` | Search symbols by name across solution |
| `roslyn_get_file_members` | Hierarchical member tree of a file |
| `roslyn_find_implementations` | Find interface/abstract implementations |
| `roslyn_preview_rename` | Preview all locations affected by a rename |
| `roslyn_get_code_fixes` | Roslyn-powered code fix suggestions |
| `roslyn_apply_code_fix` | Apply a code fix and return the changed code |
| `roslyn_format_document` | Format code with Roslyn's formatter |
| `roslyn_project_references` | Project-to-project + assembly references |
| `roslyn_nuget_packages` | NuGet packages used by a project |
| `roslyn_project_status` | Error/warning count + first errors |

## Quick Install

### Prerequisites

- **None!** Pre-built binaries are downloaded automatically. No .NET SDK needed.
- [Reasonix](https://github.com/esengine/deepseek-reasonix) (or any MCP client)

### One-command install (Reasonix)

**Linux/macOS:**
```bash
curl -sL https://raw.githubusercontent.com/SunNull/roslyn-mcp-server/main/install.sh | bash
# 或 clone 后运行：
# ./install.sh                    # 下载预编译二进制 + 注册
```

**Windows:**
```powershell
# 下载 install.bat 并运行，或 clone 后：
install.bat
```

The script downloads a **self-contained binary** (no .NET SDK needed) from
GitHub Releases, then runs `reasonix mcp add roslyn ...` to register it.

Fallback: if no pre-built binary matches your platform, the script compiles
from source (requires .NET SDK) with `--from-source`.

### Manual install (any MCP client)

1. Download the archive for your platform from [Releases](https://github.com/SunNull/roslyn-mcp-server/releases)
2. Unzip to any directory
3. Add to your MCP client's config:

**Reasonix (`reasonix.toml`):**
```toml
[[plugins]]
name    = "roslyn"
command = "/path/to/roslyn-mcp-server"
```

**Claude Code (`.mcp.json`):**
```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/path/to/roslyn-mcp-server"
    }
  }
}
```

## How It Works

The server uses a **UnifiedWorkspaceHost** that transparently picks the right analysis mode — no manual configuration needed:

1. **Startup**: auto-detects `.sln`/`.csproj` in the working directory. Found → full semantic analysis active immediately.
2. **File query**: when the AI queries a `.cs` file, the host searches upward for its containing project (like git finding `.git`). Found → loads it and resolves all NuGet references. Not found → falls back to adhoc compilation (syntax + basic types).
3. **Explicit control**: the AI can call `roslyn_load_project` at any time to load or switch projects.

```
AI calls roslyn_diagnostics("src/Program.cs")
  │
  ├─ File in loaded project? → full Roslyn analysis (NuGet refs resolved)
  │
  ├─ File not in project? → search upward for .sln/.csproj
  │   ├─ Found → auto-load → full analysis
  │   └─ Not found → adhoc fallback (syntax check)
  │
  AI can also: roslyn_load_project("/path/to/OtherSolution.sln")
```

No `--workspace` flag, no mode switching — the AI just passes file paths.

## Architecture

```
MCP Client (Reasonix / Claude / Cursor)
    │ stdio JSON-RPC (MCP protocol)
    ▼
RoslynMcpServer (.NET 11 console app)
    ├── ModelContextProtocol SDK 1.4.0 (official C# MCP SDK)
    ├── 18 [McpServerTool] methods across 5 groups
    └── Roslyn 4.13 (Microsoft.CodeAnalysis)
        └── UnifiedWorkspaceHost
            ├── Auto-detect .sln/.csproj on startup
            ├── Auto-discover project from .cs file path
            ├── MSBuildWorkspace (full NuGet + project refs)
            ├── Adhoc compilation (standalone file fallback)
            └── WorkspaceWatcher (FileSystemWatcher + incremental recompile)
```

### Project Structure

```
roslyn-mcp-server/
├── src/
│   ├── RoslynMcpServer/              # MCP entry + tools
│   │   ├── Program.cs                # auto-detect project + stdio transport
│   │   └── Tools/
│   │       ├── DiagnosticsTool.cs    # roslyn_diagnostics
│   │       ├── SemanticTools.cs      # hover/goto_def/find_ref/completion/sig_help
│   │       ├── StructureTools.cs     # find_symbols/file_members/impls/rename
│   │       ├── CodeFixTools.cs       # get_fixes/apply_fix/format
│   │       └── ProjectManagementTools.cs  # load_project/workspace_info/refs/nuget/status
│   │
│   └── RoslynMcpServer.Core/         # Core (no MCP dependency)
│       ├── Workspace/
│       │   ├── IWorkspaceHost.cs     # unified host interface
│       │   ├── UnifiedWorkspaceHost.cs  # project + adhoc auto-switching
│       │   └── WorkspaceWatcher.cs      # file change monitoring
│       ├── Analysis/
│       │   ├── DiagnosticAnalyzer.cs
│       │   └── PositionMapper.cs     # 1-based ↔ 0-based conversion
│       └── Models/
│           └── DiagnosticResult.cs
│
├── tests/                            # xUnit (17 tests)
├── samples/                          # test fixtures
├── .mcp.json                         # MCP manifest (auto-detect by installers)
├── install.sh / install.bat          # one-command install
└── reasonix.toml                     # Reasonix config template
```

## Tech Stack

| Component | Version |
|-----------|---------|
| .NET | 11.0 (preview) / 10.0+ compatible |
| ModelContextProtocol C# SDK | 1.4.0 (official) |
| Microsoft.CodeAnalysis (Roslyn) | 4.13.0 |
| C# language version | preview (latest features) |
| TreatWarningsAsErrors | enabled (compile-time safety) |

## Known Limitations

| Limitation | Impact | Workaround / Future Fix |
|-----------|--------|------------------------|
| **Code fix providers not loaded in self-contained publish** — `roslyn_get_code_fixes` / `roslyn_apply_code_fix` reflect over `AppDomain` assemblies, but single-file publish doesn't bundle IDE analyzer assemblies. | These two tools return "No code fixes available" for most diagnostics. `roslyn_format_document` and all other 16 tools work normally. | Future: bundle a curated set of analyzer assemblies (Roslynator, FxCop). For now, use `roslyn_diagnostics` + manual fixes. |
| **Adhoc references dropped in single-file mode** — `Assembly.Location` returns empty string when published as single-file, so `AdhocWorkspaceHost` can't build `MetadataReference` from BCL paths. | Standalone `.cs` files (not in a loaded project) get CS0518 "Predefined type not found" for basic types. Project mode (loaded `.sln`/`.csproj`) is unaffected — MSBuild resolves refs independently. | Use project mode (the default — the server auto-detects `.sln`/`.csproj`). Future: switch to `MetadataReference.CreateFromStream` from embedded runtime. |
| **Concurrent reads of `_solution` are unlocked** — `GetCompilationAsync` reads the solution snapshot without holding the load lock. Under high concurrency (many parallel tool calls during a reload), a tool might see a half-loaded state. | Rare — the MCP protocol serializes tool calls per session. Only triggers if the watcher reloads mid-query. | Future: switch to immutable snapshot pattern (replace `_solution` atomically). |

## License

MIT
