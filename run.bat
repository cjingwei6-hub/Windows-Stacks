@echo off
chcp 65001 >nul
cd /d "%~dp0"
title Stacks - Windows 桌面叠放

echo.
echo ╔══════════════════════════════════════╗
echo ║   Stacks - Windows 桌面叠放         ║
echo ║   macOS-style Desktop Stacks        ║
echo ║   C# .NET 6 + WPF GPU Rendering    ║
echo ╚══════════════════════════════════════╝
echo.
echo [INFO] 正在启动...
echo [INFO] 桌面图标将被隐藏，通过托盘图标控制
echo.

REM Check if --debug flag is passed
set DEBUG_FLAG=
if "%1"=="--debug" set DEBUG_FLAG=--debug

dotnet run --project "%~dp0Stacks.csproj" %DEBUG_FLAG%

if errorlevel 1 (
    echo.
    echo [WARN] Stacks 异常退出
    echo [INFO] 桌面图标已自动恢复
)
