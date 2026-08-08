@echo off
chcp 65001 >nul
cd /d "%~dp0"
title Stacks - Publishing...

echo.
echo ╔══════════════════════════════════════════╗
echo ║   Stacks - Publishing Standalone Build  ║
echo ╚══════════════════════════════════════════╝
echo.

REM Clean previous
if exist ".\publish" rmdir /s /q ".\publish"

echo [1/2] Restoring packages...
dotnet restore 2>&1 | findstr /V "s_"

echo.
echo [2/2] Publishing (win-x64)...
dotnet publish -c Release -r win-x64 --no-self-contained -o ".\publish"

if errorlevel 1 (
    echo.
    echo [ERROR] Publish failed. Trying without --no-self-contained...
    dotnet publish -c Release -o ".\publish"
)

if errorlevel 1 (
    echo.
    echo [FATAL] Publish failed completely!
    pause
    exit /b 1
)

echo.
echo ========================================
echo   Publish complete!
echo   Output: .\publish\
echo ========================================
echo.
echo Run: .\publish\Stacks.exe [--debug]
echo   or: dotnet .\publish\Stacks.dll [--debug]
echo.
pause
