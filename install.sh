#!/usr/bin/env bash
# ============================================================
#   Roslyn MCP Server — Linux/macOS 一键安装
#
#   用法：
#     ./install.sh                     编译 + 注册到 Reasonix
#     ./install.sh /path/to/proj.sln   编译 + 注册 + 加载项目
# ============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo ""
echo "============================================"
echo "  Roslyn MCP Server — 安装"
echo "============================================"
echo ""

# ── 1. 检查前提 ──────────────────────────────────────────────────────────
echo "[1/3] 检查环境..."

if ! command -v git &>/dev/null; then
    echo "  [X] 未找到 git。请先安装 Git。"
    exit 1
fi
echo "  git [OK]"

if ! command -v dotnet &>/dev/null; then
    echo "  [X] 未找到 dotnet。请先安装 .NET SDK: https://dotnet.microsoft.com/download"
    exit 1
fi
DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "?")
echo "  .NET SDK: $DOTNET_VERSION [OK]"

REASONIX=""
for cmd in reasonix reasonix.exe; do
    if command -v "$cmd" &>/dev/null; then
        REASONIX="$cmd"
        break
    fi
done
if [ -z "$REASONIX" ]; then
    echo "  Reasonix: 未找到（将输出手动配置指引）"
else
    echo "  Reasonix: $REASONIX [OK]"
fi

# ── 2. 编译 ──────────────────────────────────────────────────────────────
echo ""
echo "[2/3] 编译 Roslyn MCP Server..."
cd "$SCRIPT_DIR"
dotnet build RoslynMcpServer.sln -c Release --nologo -clp:ErrorsOnly

# 用正斜杠构建路径（TOML 安全）
DLL_PATH="$SCRIPT_DIR/src/RoslynMcpServer/bin/Release/net11.0/roslyn-mcp-server.dll"
echo "  产物: $DLL_PATH [OK]"

# ── 3. 注册到 Reasonix ──────────────────────────────────────────────────
if [ -n "$REASONIX" ]; then
    echo ""
    echo "[3/3] 注册到 Reasonix..."

    # 先移除旧的（忽略错误）
    "$REASONIX" mcp remove roslyn 2>/dev/null || true

    if [ -n "$1" ]; then
        # 带 workspace 参数
        "$REASONIX" mcp add roslyn dotnet exec "$DLL_PATH" --workspace "$1"
        echo "  workspace: $1"
    else
        # standalone 模式
        "$REASONIX" mcp add roslyn dotnet exec "$DLL_PATH"
    fi
else
    echo ""
    echo "[3/3] Reasonix 未安装 — 手动配置指引:"
    echo ""
    echo "  方式 1: reasonix mcp add roslyn dotnet exec \"$DLL_PATH\""
    echo ""
    echo "  方式 2: 编辑 ~/.config/reasonix/config.toml:"
    echo ""
    echo "  [[plugins]]"
    echo "  name    = \"roslyn\""
    echo "  command = \"dotnet\""
    echo "  args    = [\"exec\", \"$DLL_PATH\"]"
    echo ""
    echo "  方式 3: 复制仓库中的 .mcp.json 到你的项目根目录"
fi

echo ""
echo "============================================"
echo "  安装完成！16 个 Roslyn 工具已就绪"
echo "============================================"
echo ""
echo "  验证: reasonix mcp list"
echo ""
