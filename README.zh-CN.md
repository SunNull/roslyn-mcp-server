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

## 18 个工具

| 工具 | 功能说明 |
|------|----------|
| `roslyn_diagnostics` | 编译错误 + 警告（CS0123 等），精确到行列 |
| `roslyn_load_project` | 加载/切换 .sln 或 .csproj（通常自动探测） |
| `roslyn_workspace_info` | 当前状态：是否已加载项目？哪些项目？ |
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
| `roslyn_project_references` | 项目引用链 + 程序集引用 |
| `roslyn_nuget_packages` | 项目使用的 NuGet 包列表 |
| `roslyn_project_status` | 项目编译状态总览（错误数 + 警告数 + 首批错误） |

## 快速安装

### 前置条件

- **无！** 自动下载预编译二进制，不需要安装 .NET SDK
- [Reasonix](https://github.com/esengine/deepseek-reasonix)（或任何 MCP 客户端）

### 一键安装（Reasonix）

**Linux/macOS：**
```bash
curl -sL https://raw.githubusercontent.com/SunNull/roslyn-mcp-server/main/install.sh | bash
# 或 clone 后运行：
# ./install.sh                    # 下载预编译二进制 + 注册
```

**Windows：**
```powershell
# 下载 install.bat 并运行，或 clone 后：
install.bat
```

脚本自动从 GitHub Releases 下载**自包含二进制**（不需要 .NET SDK），然后执行
`reasonix mcp add roslyn ...` 完成注册。

回退：如果没有匹配平台的预编译包，脚本会从源码编译（需要 .NET SDK），加 `--from-source`。

### 手动安装（任何 MCP 客户端）

1. 从 [Releases](https://github.com/SunNull/roslyn-mcp-server/releases) 下载对应平台的压缩包
2. 解压到任意目录
3. 添加到 MCP 客户端配置：

**Reasonix（`reasonix.toml`）：**
```toml
[[plugins]]
name    = "roslyn"
command = "/path/to/roslyn-mcp-server"
```

**Claude Code（`.mcp.json`）：**
```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/path/to/roslyn-mcp-server"
    }
  }
}
```

## 工作原理

服务器使用 **UnifiedWorkspaceHost**，透明地选择正确的分析模式——无需手动配置：

1. **启动时**：自动探测工作目录下的 `.sln`/`.csproj`。找到 → 立即启用完整语义分析。
2. **文件查询时**：当 AI 查询一个 `.cs` 文件，host 会向上搜索其所属项目（像 git 找 `.git`）。找到 → 自动加载并解析全部 NuGet 引用。没找到 → 降级为 adhoc 编译（语法 + 基础类型检查）。
3. **显式控制**：AI 可以随时调用 `roslyn_load_project` 加载或切换项目。

```
AI 调用 roslyn_diagnostics("src/Program.cs")
  │
  ├─ 文件在已加载项目中？ → 完整 Roslyn 分析（NuGet 引用全解析）
  │
  ├─ 文件不在项目中？ → 向上搜索 .sln/.csproj
  │   ├─ 找到 → 自动加载 → 完整分析
  │   └─ 没找到 → adhoc 降级（语法检查）
  │
  AI 还可以：roslyn_load_project("/path/to/OtherSolution.sln")
```

无需 `--workspace` 参数，无需模式切换——AI 只管传文件路径。

## 架构

```
MCP 客户端 (Reasonix / Claude / Cursor)
    │ stdio JSON-RPC（MCP 协议）
    ▼
RoslynMcpServer（.NET 11 控制台应用）
    ├── ModelContextProtocol SDK 1.4.0（官方 C# MCP SDK）
    ├── 18 个 [McpServerTool] 方法，分为 5 组
    └── Roslyn 4.13（Microsoft.CodeAnalysis）
        └── UnifiedWorkspaceHost
            ├── 启动时自动探测 .sln/.csproj
            ├── 从 .cs 文件路径自动向上发现项目
            ├── MSBuildWorkspace（完整 NuGet + 项目引用）
            ├── Adhoc 编译（独立文件降级）
            └── WorkspaceWatcher（文件变化监听 + 增量重编译）
```

### 项目结构

```
roslyn-mcp-server/
├── src/
│   ├── RoslynMcpServer/              # MCP 入口 + 工具
│   │   ├── Program.cs                # 自动探测项目 + stdio 传输
│   │   └── Tools/
│   │       ├── DiagnosticsTool.cs    # roslyn_diagnostics
│   │       ├── SemanticTools.cs      # hover/goto_def/find_ref/completion/sig_help
│   │       ├── StructureTools.cs     # find_symbols/file_members/impls/rename
│   │       ├── CodeFixTools.cs       # get_fixes/apply_fix/format
│   │       └── ProjectManagementTools.cs  # load_project/workspace_info/refs/nuget/status
│   │
│   └── RoslynMcpServer.Core/         # 核心层（不依赖 MCP）
│       ├── Workspace/
│       │   ├── IWorkspaceHost.cs     # 统一 host 接口
│       │   ├── UnifiedWorkspaceHost.cs  # 项目 + adhoc 自动切换
│       │   └── WorkspaceWatcher.cs      # 文件变化监听
│       ├── Analysis/
│       │   ├── DiagnosticAnalyzer.cs    # 诊断提取与格式化
│       │   └── PositionMapper.cs     # 1-based ↔ 0-based 坐标转换
│       └── Models/
│           └── DiagnosticResult.cs
│
├── tests/                            # xUnit（17 个测试）
├── samples/                          # 测试样本
├── .mcp.json                         # MCP 清单（安装器自动识别）
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
