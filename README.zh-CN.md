# Roslyn MCP Server

[English](./README.md) | **简体中文**

一个 .NET 11 MCP（Model Context Protocol）服务器，将 **Roslyn 编译器分析能力**暴露给 AI 编程助手。让任何兼容 MCP 的客户端（Reasonix、Claude Desktop、Cursor、VS Code Copilot）获得实时 C# 代码智能——诊断、类型信息、引用查找、代码修复——无需运行 `dotnet build`。

## 为什么需要？

传统 AI 编程助手的 C# 开发流程：
```
写代码 → dotnet build → 看到错误 → 修改 → 重新编译 → ...
```

使用 Roslyn MCP Server 后：
```
写代码 → roslyn_diagnostics → 即时反馈（就像 Visual Studio 的红色波浪线）
```

AI 助手获得**编译器级别的实时分析**，底层引擎和 Visual Studio 完全一样（Roslyn）。

## 16 个工具

| 工具 | 功能说明 |
|------|----------|
| `roslyn_diagnostics` | 编译错误 + 警告（CS0123 等），精确到行列 |
| `roslyn_hover` | 符号类型签名、文档注释、成员信息 |
| `roslyn_goto_definition` | 跳转到符号定义位置 |
| `roslyn_find_references` | 跨整个解决方案查找所有引用 |
| `roslyn_completion` | 智能代码补全建议 |
| `roslyn_signature_help` | 方法参数提示（重载列表 + 参数类型） |
| `roslyn_find_symbols` | 按名称搜索全项目符号（类/方法/属性等） |
| `roslyn_get_file_members` | 文件成员树（类、接口、方法的层级结构） |
| `roslyn_find_implementations` | 查找接口/抽象类的全部实现 |
| `roslyn_preview_rename` | 重命名预览（显示所有受影响的位置，不实际修改） |
| `roslyn_get_code_fixes` | Roslyn 代码修复建议（和 VS 的灯泡一样） |
| `roslyn_apply_code_fix` | 应用代码修复，返回修改后的代码 |
| `roslyn_format_document` | 用 Roslyn 格式化器格式化代码 |
| `roslyn_workspace_info` | 解决方案/项目概览 |
| `roslyn_project_references` | 项目引用链 + 程序集引用 |
| `roslyn_nuget_packages` | 项目使用的 NuGet 包列表 |
| `roslyn_project_status` | 项目编译状态总览（错误数 + 警告数 + 首批错误） |

## 快速安装

### 前置条件

- [.NET 11 SDK](https://dotnet.microsoft.com/download)（或 .NET 10+）
- [Reasonix](https://github.com/esengine/deepseek-reasonix)（或任何 MCP 客户端）

### 一键安装（Reasonix）

**Linux/macOS：**
```bash
git clone https://github.com/SunNull/roslyn-mcp-server.git
cd roslyn-mcp-server
./install.sh                    # 或加载项目: ./install.sh /path/to/your/project.sln
```

**Windows：**
```cmd
git clone https://github.com/SunNull/roslyn-mcp-server.git
cd roslyn-mcp-server
install.bat                     REM 或加载项目: install.bat C:\path\to\your\project.sln
```

脚本会自动编译并执行 `reasonix mcp add roslyn ...` 完成注册。

### 手动安装（任何 MCP 客户端）

```bash
dotnet build -c Release
```

然后在你的 MCP 客户端配置中添加：

**Reasonix（`reasonix.toml`）：**
```toml
[[plugins]]
name    = "roslyn"
command = "dotnet"
args    = ["exec", "/path/to/roslyn-mcp-server/src/RoslynMcpServer/bin/Release/net11.0/roslyn-mcp-server.dll"]
```

**Claude Code（`.mcp.json`）：**
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

## 加载真实项目

默认以**独立模式**运行（单文件分析，不解析 NuGet 引用）。如果要分析真实项目，传入 `--workspace` 参数：

```toml
[[plugins]]
name    = "roslyn"
command = "dotnet"
args    = ["exec", "/path/to/roslyn-mcp-server.dll", "--workspace", "/path/to/YourSolution.sln"]
```

使用 `--workspace` 后，服务器会：
1. 通过 MSBuildWorkspace 加载 `.sln`/`.csproj`
2. 解析所有 NuGet 包和项目引用
3. 启动 FileSystemWatcher **监听文件变化，增量重编译**（500ms 防抖）

## 架构

```
MCP 客户端 (Reasonix / Claude / Cursor)
    │ stdio JSON-RPC（MCP 协议）
    ▼
RoslynMcpServer（.NET 11 控制台应用）
    ├── ModelContextProtocol SDK 1.4.0（官方 C# MCP SDK）
    ├── 16 个 [McpServerTool] 方法，分为 4 组
    └── Roslyn 4.13（Microsoft.CodeAnalysis）
        ├── MSBuildWorkspace — 加载 .sln/.csproj，解析全部引用
        ├── AdhocWorkspace — 独立 .cs 文件的降级方案
        └── WorkspaceWatcher — 文件变化监听 + 增量重编译
```

### 项目结构

```
roslyn-mcp-server/
├── src/
│   ├── RoslynMcpServer/              # MCP 入口 + 工具
│   │   ├── Program.cs                # stdio 传输 + workspace 加载
│   │   └── Tools/
│   │       ├── DiagnosticsTool.cs    # roslyn_diagnostics
│   │       ├── SemanticTools.cs      # hover/goto_def/find_ref/completion/sig_help
│   │       ├── StructureTools.cs     # find_symbols/file_members/impls/rename
│   │       ├── CodeFixTools.cs       # get_fixes/apply_fix/format
│   │       └── ProjectTools.cs       # workspace_info/proj_refs/nuget/status
│   │
│   └── RoslynMcpServer.Core/         # 核心层（不依赖 MCP）
│       ├── Workspace/
│       │   ├── IWorkspaceHost.cs     # 可插拔的 host 接口
│       │   ├── MSBuildWorkspaceHost.cs  # 真实项目加载
│       │   ├── AdhocWorkspaceHost.cs    # 单文件降级
│       │   └── WorkspaceWatcher.cs      # 文件变化监听
│       ├── Analysis/
│       │   ├── DiagnosticAnalyzer.cs    # 诊断提取与格式化
│       │   └── PositionMapper.cs     # 1-based ↔ 0-based 坐标转换
│       └── Models/
│           └── DiagnosticResult.cs
│
├── tests/                            # xUnit（18 个测试）
├── samples/                          # 测试样本
├── install.sh / install.bat          # 一键安装脚本
└── reasonix.toml                     # Reasonix 配置模板
```

## 技术栈

| 组件 | 版本 |
|------|------|
| .NET | 11.0（preview）/ 10.0+ 兼容 |
| ModelContextProtocol C# SDK | 1.4.0（官方） |
| Microsoft.CodeAnalysis（Roslyn） | 4.13.0 |
| C# 语言版本 | preview（启用最新特性） |
| TreatWarningsAsErrors | 开启（编译期强制安全） |

## License

MIT
