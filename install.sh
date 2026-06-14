#!/usr/bin/env bash
# ============================================================
#   Roslyn MCP Server — Linux/macOS 一键安装
#
#   自动下载预编译二进制（无需 .NET SDK），注册到 Reasonix。
#   如果没有匹配的预编译包，回退到从源码编译。
#
#   用法：
#     ./install.sh                     下载 + 注册
#     ./install.sh --from-source       从源码编译（需要 .NET SDK）
#     ./install.sh /path/to/proj.sln   下载 + 注册 + 加载项目
# ============================================================

set -e

REPO="SunNull/roslyn-mcp-server"
INSTALL_DIR="${HOME}/.roslyn-mcp-server"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo ""
echo "============================================"
echo "  Roslyn MCP Server — 安装"
echo "============================================"
echo ""

# ── 检测平台 ─────────────────────────────────────────────────────────────
OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)
case "$OS-$ARCH" in
  linux-x86_64|linux-amd64)   RID="linux-x64" ;;
  linux-aarch64|linux-arm64)  RID="linux-arm64" ;;
  darwin-x86_64|darwin-amd64) RID="osx-x64" ;;
  darwin-arm64|darwin-aarch64) RID="osx-arm64" ;;
  *) echo "[X] 不支持的平台: $OS-$ARCH"; exit 1 ;;
esac
echo "  平台: $RID"

# ── 解析参数 ─────────────────────────────────────────────────────────────
FROM_SOURCE=false
WORKSPACE=""
for arg in "$@"; do
  case "$arg" in
    --from-source) FROM_SOURCE=true ;;
    --*) ;;
    *) WORKSPACE="$arg" ;;
  esac
done

# ── 获取最新版本号 ───────────────────────────────────────────────────────
get_latest_tag() {
  if command -v gh &>/dev/null; then
    gh api "repos/$REPO/releases/latest" --jq '.tag_name' 2>/dev/null
  elif command -v curl &>/dev/null; then
    curl -sL "https://api.github.com/repos/$REPO/releases/latest" | grep '"tag_name"' | head -1 | cut -d'"' -f4
  fi
}

# ── 安装 ─────────────────────────────────────────────────────────────────
if [ "$FROM_SOURCE" = true ]; then
  # ── 从源码编译 ──
  echo ""
  echo "[1/3] 从源码编译..."

  if ! command -v dotnet &>/dev/null; then
    echo "  [X] 未找到 dotnet。--from-source 需要 .NET SDK。"
    exit 1
  fi

  cd "$SCRIPT_DIR"
  dotnet publish src/RoslynMcpServer -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -o "$INSTALL_DIR" --nologo -clp:ErrorsOnly

  EXE="$INSTALL_DIR/roslyn-mcp-server"
  chmod +x "$EXE"
  echo "  编译完成: $EXE"

else
  # ── 下载预编译包 ──
  TAG=$(get_latest_tag)

  if [ -z "$TAG" ]; then
    echo ""
    echo "[!] 未找到 Release 预编译包，回退到从源码编译..."
    echo "    （如需跳过，先创建一个 Release: git tag v0.1.0 && git push origin v0.1.0）"

    if ! command -v dotnet &>/dev/null; then
      echo "  [X] 无法下载预编译包，也没有 .NET SDK 来编译。"
      echo "      请安装 .NET SDK: https://dotnet.microsoft.com/download"
      exit 1
    fi

    cd "$SCRIPT_DIR"
    dotnet publish src/RoslynMcpServer -c Release -r "$RID" --self-contained true \
      -p:PublishSingleFile=true -o "$INSTALL_DIR" --nologo -clp:ErrorsOnly
    EXE="$INSTALL_DIR/roslyn-mcp-server"
    chmod +x "$EXE"
  else
    echo "  最新版本: $TAG"
    URL="https://github.com/$REPO/releases/download/$TAG/roslyn-mcp-server-$RID.tar.gz"

    echo ""
    echo "[1/3] 下载预编译包..."
    echo "  $URL"

    mkdir -p "$INSTALL_DIR"
    TMPFILE=$(mktemp /tmp/roslyn-mcp-XXXXXX.tar.gz)

    if command -v curl &>/dev/null; then
      curl -sL -o "$TMPFILE" "$URL"
    elif command -v wget &>/dev/null; then
      wget -q -O "$TMPFILE" "$URL"
    else
      echo "  [X] 需要 curl 或 wget 来下载。"
      exit 1
    fi

    tar -xzf "$TMPFILE" -C "$INSTALL_DIR"
    rm "$TMPFILE"

    EXE="$INSTALL_DIR/roslyn-mcp-server"
    chmod +x "$EXE" 2>/dev/null || true
    echo "  安装到: $INSTALL_DIR"
  fi
fi

# ── 注册到 Reasonix ──────────────────────────────────────────────────────
echo ""
echo "[2/3] 注册到 Reasonix..."

REASONIX=""
for cmd in reasonix reasonix.exe; do
  if command -v "$cmd" &>/dev/null; then
    REASONIX="$cmd"
    break
  fi
done

if [ -z "$REASONIX" ]; then
  echo "  Reasonix 未找到 — 手动配置:"
  echo ""
  echo "  [[plugins]]"
  echo "  name    = \"roslyn\""
  echo "  command = \"$EXE\""
  echo ""
  echo "  或: reasonix mcp add roslyn \"$EXE\""
else
  "$REASONIX" mcp remove roslyn 2>/dev/null || true
  "$REASONIX" mcp add roslyn "$EXE"
  echo "  已注册到 Reasonix [OK]"
fi

# ── 完成 ─────────────────────────────────────────────────────────────────
echo ""
echo "[3/3] 完成！"
echo ""
echo "============================================"
echo "  安装完成！18 个 Roslyn 工具已就绪"
echo "============================================"
echo ""
echo "  二进制位置: $EXE"
echo "  验证: reasonix mcp list"
echo ""
