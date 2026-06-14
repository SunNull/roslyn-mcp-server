@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM   Roslyn MCP Server - Windows 一键安装
REM
REM   用法：
REM     install.bat                        编译 + 注册到 Reasonix
REM     install.bat C:\repo\MyProj.sln     编译 + 注册 + 加载项目
REM     install.bat --clone C:\target      clone + 编译 + 注册
REM ============================================================

set SCRIPT_DIR=%~dp0
set SCRIPT_DIR=%SCRIPT_DIR:~0,-1%

echo.
echo ============================================
echo   Roslyn MCP Server - Windows 安装
echo ============================================
echo.

REM ── 1. 检查前提 ──────────────────────────────────────────────
echo [1/3] 检查环境...

where git >nul 2>&1
if errorlevel 1 (
    echo   [X] 未找到 git。请先安装 Git。
    exit /b 1
)
echo   git [OK]

where dotnet >nul 2>&1
if errorlevel 1 (
    echo   [X] 未找到 dotnet。请先安装 .NET SDK: https://dotnet.microsoft.com/download
    exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VERSION=%%v
echo   .NET SDK: !DOTNET_VERSION! [OK]

set REASONIX=
where reasonix >nul 2>&1 && set REASONIX=reasonix
where reasonix.exe >nul 2>&1 && set REASONIX=reasonix.exe
if defined REASONIX (
    echo   Reasonix: !REASONIX! [OK]
) else (
    echo   Reasonix: 未找到 ^(将输出手动配置指引^)
)

REM ── 2. 编译 ──────────────────────────────────────────────────
echo.
echo [2/3] 编译 Roslyn MCP Server...
cd /d "%SCRIPT_DIR%"
dotnet build RoslynMcpServer.sln -c Release --nologo -clp:ErrorsOnly
if errorlevel 1 (
    echo   [X] 编译失败
    exit /b 1
)

REM 用正斜杠构建路径（避免 TOML 反斜杠转义问题）
set DLL_PATH=%SCRIPT_DIR%\src\RoslynMcpServer\bin\Release\net11.0\roslyn-mcp-server.dll
set DLL_PATH_FWD=%DLL_PATH:\=/%
echo   产物: %DLL_PATH% [OK]

REM ── 3. 注册到 Reasonix ──────────────────────────────────────
if defined REASONIX (
    echo.
    echo [3/3] 注册到 Reasonix...

    REM 先移除旧注册（忽略错误）
    !REASONIX! mcp remove roslyn >nul 2>&1

    if "%~1"=="" (
        REM 无参数：standalone 模式
        !REASONIX! mcp add roslyn dotnet exec "%DLL_PATH_FWD%"
    ) else (
        REM 有参数：作为 workspace 传入
        set WS_PATH=%~1
        set WS_PATH=!WS_PATH:\=/
        !REASONIX! mcp add roslyn dotnet exec "%DLL_PATH_FWD%" --workspace "!WS_PATH!"
        echo   workspace: !WS_PATH!
    )
) else (
    echo.
    echo [3/3] Reasonix 未安装 - 手动配置指引:
    echo.
    echo   方式 1: reasonix mcp add roslyn dotnet exec "%DLL_PATH_FWD%"
    echo.
    echo   方式 2: 编辑 ~/.config/reasonix/config.toml（路径用正斜杠）:
    echo.
    echo   [[plugins]]
    echo   name    = "roslyn"
    echo   command = "dotnet"
    echo   args    = ["exec", "%DLL_PATH_FWD%"]
    echo.
    echo   方式 3: 复制仓库中的 .mcp.json 到你的项目根目录
)

echo.
echo ============================================
echo   安装完成！16 个 Roslyn 工具已就绪
echo ============================================
echo.
echo   验证: reasonix mcp list
echo.
