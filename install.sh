#!/usr/bin/env bash
# 一键安装 Roslyn MCP Server 到 Reasonix
#
# 用法：
#   ./install.sh                    # 编译 + 注册到 Reasonix
#   ./install.sh /path/to/project   # 编译 + 注册 + 指定 workspace
#
# 前提：已安装 .NET 11 SDK 和 Reasonix

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DLL_NAME="roslyn-mcp-server.dll"

echo "╔══════════════════════════════════════════╗"
echo "║   Roslyn MCP Server — 一键安装           ║"
echo "╚══════════════════════════════════════════╝"
echo ""

# ── 1. 检查前提 ──────────────────────────────────────────────────────────
echo "▶ 检查环境..."

if ! command -v dotnet &>/dev/null; then
    echo "✗ 未找到 dotnet。请先安装 .NET SDK: https://dotnet.microsoft.com/download"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "0")
echo "  .NET SDK: $DOTNET_VERSION ✓"

REASONIX=""
for cmd in reasonix reasonix.exe; do
    if command -v "$cmd" &>/dev/null; then
        REASONIX="$cmd"
        break
    fi
done
if [ -z "$REASONIX" ]; then
    echo "  Reasonix: 未找到（可选，跳过自动注册）"
else
    echo "  Reasonix: $REASONIX ✓"
fi

# ── 2. 编译 ──────────────────────────────────────────────────────────────
echo ""
echo "▶ 编译 Roslyn MCP Server..."
cd "$SCRIPT_DIR"
dotnet build RoslynMcpServer.sln -c Release --nologo -clp:ErrorsOnly

DLL_PATH="$SCRIPT_DIR/src/RoslynMcpServer/bin/Release/net11.0/$DLL_NAME"

if [ ! -f "$DLL_PATH" ]; then
    echo "✗ 编译失败：$DLL_PATH 不存在"
    exit 1
fi

echo "  产物: $DLL_PATH ✓"

# ── 3. 注册到 Reasonix ──────────────────────────────────────────────────
if [ -n "$REASONIX" ]; then
    echo ""
    echo "▶ 注册到 Reasonix..."

    # 先移除旧的（忽略错误）
    "$REASONIX" mcp remove roslyn 2>/dev/null || true

    # 构造参数
    if [ -n "$1" ]; then
        # 带 workspace 参数
        "$REASONIX" mcp add roslyn dotnet exec "$DLL_PATH" --workspace "$1"
        echo "  workspace: $1"
    else
        # 不带 workspace（standalone 模式）
        "$REASONIX" mcp add roslyn dotnet exec "$DLL_PATH"
    fi

    echo ""
    echo "✓ 安装完成！"
    echo ""
    echo "  验证: $REASONIX mcp list"
    echo "  使用: 启动 reasonix chat，模型即可调用 roslyn_diagnostics 等工具"
else
    echo ""
    echo "✓ 编译完成！"
    echo ""
    echo "  手动注册到 Reasonix（复制到 reasonix.toml）："
    echo ""
    echo "  [[plugins]]"
    echo "  name    = \"roslyn\""
    echo "  command = \"dotnet\""
    echo "  args    = [\"exec\", \"$DLL_PATH\"]"
fi

echo ""
echo "╔══════════════════════════════════════════╗"
echo "║   安装完成！16 个 Roslyn 工具已就绪       ║"
echo "╚══════════════════════════════════════════╝"
