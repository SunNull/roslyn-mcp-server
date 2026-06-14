@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM   Roslyn MCP Server — Windows 一键安装
REM
REM   自动下载预编译二进制（无需 .NET SDK），注册到 Reasonix。
REM   如果没有预编译包，回退到从源码编译。
REM
REM   用法：
REM     install.bat                      下载 + 注册
REM     install.bat --from-source        从源码编译（需要 .NET SDK）
REM     install.bat C:\path\proj.sln     下载 + 注册 + 加载项目
REM ============================================================

set REPO=SunNull/roslyn-mcp-server
set INSTALL_DIR=%USERPROFILE%\.roslyn-mcp-server
set SCRIPT_DIR=%~dp0
set SCRIPT_DIR=%SCRIPT_DIR:~0,-1%
set RID=win-x64

echo.
echo ============================================
echo   Roslyn MCP Server - Windows 安装
echo ============================================
echo.
echo   平台: %RID%

REM ── 解析参数 ─────────────────────────────────────────────────
set FROM_SOURCE=0
set WORKSPACE=
for %%a in (%*) do (
    if "%%a"=="--from-source" set FROM_SOURCE=1
    if not "%%a"=="--from-source" (
        if not "%%~a"=="--from-source" (
            echo %%a | findstr /r "^-" >nul
            if errorlevel 1 set WORKSPACE=%%a
        )
    )
)

REM ── 安装 ─────────────────────────────────────────────────────
if "%FROM_SOURCE%"=="1" goto :build_source

REM 尝试下载预编译包
echo.
echo [1/3] 检查预编译包...

REM 获取最新版本号
for /f "tokens=*" %%t in ('curl -sL "https://api.github.com/repos/%REPO%/releases/latest" 2^>nul ^| findstr "tag_name" 2^>nul') do set TAG_LINE=%%t
if not defined TAG_LINE (
    echo   未找到 Release 预编译包，回退到从源码编译...
    goto :build_source
)

REM 提取版本号
for /f "tokens=2 delims==" %%v in ('echo %TAG_LINE% ^| findstr /r "tag_name"') do set TAG=%%v
set TAG=%TAG:"=%
set TAG=%TAG:,=%
set TAG=%TAG: =%

if not defined TAG (
    echo   无法获取版本号，回退到从源码编译...
    goto :build_source
)

echo   最新版本: %TAG%

set URL=https://github.com/%REPO%/releases/download/%TAG%/roslyn-mcp-server-%RID%.zip
echo   下载: %URL%

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
set TMPZIP=%TEMP%\roslyn-mcp-server.zip

curl -sL -o "%TMPZIP%" "%URL%" 2>nul
if errorlevel 1 (
    echo   下载失败，回退到从源码编译...
    goto :build_source
)

powershell -NoProfile -Command "Expand-Archive -Path '%TMPZIP%' -DestinationPath '%INSTALL_DIR%' -Force" 2>nul
if errorlevel 1 (
    echo   解压失败，回退到从源码编译...
    goto :build_source
)

del "%TMPZIP%" 2>nul
set EXE=%INSTALL_DIR%\roslyn-mcp-server.exe
echo   安装到: %EXE%
goto :register

:build_source
echo.
echo [1/3] 从源码编译...

where dotnet >nul 2>&1
if errorlevel 1 (
    echo   [X] 需要 .NET SDK 来编译。请安装: https://dotnet.microsoft.com/download
    exit /b 1
)

cd /d "%SCRIPT_DIR%"
dotnet publish src/RoslynMcpServer -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -o "%INSTALL_DIR%" --nologo -clp:ErrorsOnly
if errorlevel 1 (
    echo   [X] 编译失败
    exit /b 1
)
set EXE=%INSTALL_DIR%\roslyn-mcp-server.exe
echo   编译完成: %EXE%

:register
REM ── 注册到 Reasonix ─────────────────────────────────────────
echo.
echo [2/3] 注册到 Reasonix...

set REASONIX=
where reasonix >nul 2>&1 && set REASONIX=reasonix
where reasonix.exe >nul 2>&1 && set REASONIX=reasonix.exe

if not defined REASONIX (
    echo   Reasonix 未找到 - 手动配置:
    echo.
    echo   [[plugins]]
    echo   name    = "roslyn"
    echo   command = "%EXE%"
    echo.
    echo   或: reasonix mcp add roslyn "%EXE%"
) else (
    !REASONIX! mcp remove roslyn >nul 2>&1

    if defined WORKSPACE (
        !REASONIX! mcp add roslyn "%EXE%" --workspace "%WORKSPACE%"
    ) else (
        !REASONIX! mcp add roslyn "%EXE%"
    )
    echo   已注册到 Reasonix [OK]
)

REM ── 完成 ─────────────────────────────────────────────────────
echo.
echo [3/3] 完成！
echo.
echo ============================================
echo   安装完成！18 个 Roslyn 工具已就绪
echo ============================================
echo.
echo   二进制位置: %EXE%
echo   验证: reasonix mcp list
echo.
