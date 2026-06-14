@echo off
REM 一键安装 Roslyn MCP Server 到 Reasonix (Windows)
REM
REM 用法：
REM   install.bat                       REM 编译 + 注册到 Reasonix
REM   install.bat C:\path\to\project    REM 编译 + 注册 + 指定 workspace

setlocal enabledelayedexpansion

set SCRIPT_DIR=%~dp0
set SCRIPT_DIR=%SCRIPT_DIR:~0,-1%

echo.
echo ============================================
echo   Roslyn MCP Server - 一键安装
echo ============================================
echo.

REM ── 1. 检查前提 ──────────────────────────────────────────────
echo [1/3] 检查环境...

where dotnet >nul 2>&1
if errorlevel 1 (
    echo   [X] 未找到 dotnet。请先安装 .NET SDK。
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
    echo   Reasonix: 未找到 ^(跳过自动注册^)
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

set DLL_PATH=%SCRIPT_DIR%\src\RoslynMcpServer\bin\Release\net11.0\roslyn-mcp-server.dll
echo   产物: %DLL_PATH% [OK]

REM ── 3. 注册到 Reasonix ──────────────────────────────────────
if defined REASONIX (
    echo.
    echo [3/3] 注册到 Reasonix...

    !REASONIX! mcp remove roslyn >nul 2>&1

    if "%~1"=="" (
        !REASONIX! mcp add roslyn dotnet exec "%DLL_PATH%"
    ) else (
        !REASONIX! mcp add roslyn dotnet exec "%DLL_PATH%" --workspace "%~1"
        echo   workspace: %~1
    )
) else (
    echo.
    echo [3/3] 跳过注册（Reasonix 未安装）
    echo   手动添加到 reasonix.toml:
    echo   [[plugins]]
    echo   name    = "roslyn"
    echo   command = "dotnet"
    echo   args    = ["exec", "%DLL_PATH%"]
)

echo.
echo ============================================
echo   安装完成！16 个 Roslyn 工具已就绪
echo ============================================
echo.
echo   验证: reasonix mcp list
echo   使用: 启动 reasonix chat，模型即可调用
echo.
