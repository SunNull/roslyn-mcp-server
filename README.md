# Roslyn MCP Server

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

## 16 Tools

| Tool | What it does |
|------|-------------|
| `roslyn_diagnostics` | Compilation errors + warnings (CS0123, etc.) |
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
| `roslyn_workspace_info` | Solution/project overview |
| `roslyn_project_references` | Project-to-project + assembly references |
| `roslyn_nuget_packages` | NuGet packages used by a project |
| `roslyn_project_status` | Error/warning count + first errors |

## Quick Install

### Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download) (or .NET 10+)
- [Reasonix](https://github.com/esengine/deepseek-reasonix) (or any MCP client)

### One-command install (Reasonix)

**Linux/macOS:**
```bash
git clone https://github.com/yourname/roslyn-mcp-server.git
cd roslyn-mcp-server
./install.sh                    # or: ./install.sh /path/to/your/project.sln
```

**Windows:**
```cmd
git clone https://github.com/yourname/roslyn-mcp-server.git
cd roslyn-mcp-server
install.bat                     REM or: install.bat C:\path\to\your\project.sln
```

The script compiles the server and runs `reasonix mcp add roslyn ...` to register it.

### Manual install (any MCP client)

```bash
dotnet build -c Release
```

Then add to your MCP client's config:

**Reasonix (`reasonix.toml`):**
```toml
[[plugins]]
name    = "roslyn"
command = "dotnet"
args    = ["exec", "/path/to/roslyn-mcp-server/src/RoslynMcpServer/bin/Release/net11.0/roslyn-mcp-server.dll"]
```

**Claude Code (`.mcp.json`):**
```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": ["exec", "/path/to/roslyn-mcp-server.dll"]
    }
  }
}
```

## Loading a Real Project

By default the server runs in **standalone mode** (single-file analysis, no NuGet references). For real projects, pass `--workspace`:

```toml
[[plugins]]
name    = "roslyn"
command = "dotnet"
args    = ["exec", "/path/to/roslyn-mcp-server.dll", "--workspace", "/path/to/YourSolution.sln"]
```

With `--workspace`, the server:
1. Loads the `.sln`/`.csproj` via MSBuildWorkspace
2. Resolves all NuGet packages and project references
3. Starts a FileSystemWatcher that **incrementally recompiles on file change** (500ms debounce)

## Architecture

```
MCP Client (Reasonix / Claude / Cursor)
    │ stdio JSON-RPC (MCP protocol)
    ▼
RoslynMcpServer (.NET 11 console app)
    ├── ModelContextProtocol SDK 1.4.0 (official C# MCP SDK)
    ├── 16 [McpServerTool] methods across 4 groups
    └── Roslyn 4.13 (Microsoft.CodeAnalysis)
        ├── MSBuildWorkspace — loads .sln/.csproj, resolves all references
        ├── AdhocWorkspace — fallback for standalone .cs files
        └── WorkspaceWatcher — FileSystemWatcher + incremental recompile
```

### Project Structure

```
roslyn-mcp-server/
├── src/
│   ├── RoslynMcpServer/              # MCP entry + tools
│   │   ├── Program.cs                # stdio transport + workspace loading
│   │   └── Tools/
│   │       ├── DiagnosticsTool.cs    # roslyn_diagnostics
│   │       ├── SemanticTools.cs      # hover/goto_def/find_ref/completion/sig_help
│   │       ├── StructureTools.cs     # find_symbols/file_members/impls/rename
│   │       ├── CodeFixTools.cs       # get_fixes/apply_fix/format
│   │       └── ProjectTools.cs       # workspace_info/proj_refs/nuget/status
│   │
│   └── RoslynMcpServer.Core/         # Core (no MCP dependency)
│       ├── Workspace/
│       │   ├── IWorkspaceHost.cs     # pluggable host interface
│       │   ├── MSBuildWorkspaceHost.cs  # real project loading
│       │   ├── AdhocWorkspaceHost.cs    # single-file fallback
│       │   └── WorkspaceWatcher.cs      # file change monitoring
│       ├── Analysis/
│       │   ├── DiagnosticAnalyzer.cs
│       │   └── PositionMapper.cs     # 1-based ↔ 0-based conversion
│       └── Models/
│           └── DiagnosticResult.cs
│
├── tests/                            # xUnit (18 tests)
├── samples/                          # test fixtures
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

## License

MIT
