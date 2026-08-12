@echo off
chcp 65001 >nul
title YTM.Lyrics 环境准备

echo ========================================
echo   YTM.Lyrics — 一键环境检查 + 还原 + 构建
echo ========================================
echo.

:: ---- 1. 检查 .NET 8 SDK ----
echo [1/3] 检查 .NET 8 SDK...
dotnet --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [错误] 未找到 dotnet 命令。
    echo 请安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0
    echo 选 "SDK 8.0.x" 下的 Windows x64 安装包。
    pause
    exit /b 1
)

for /f "tokens=1 delims=." %%v in ('dotnet --version') do set DOTNET_MAJOR=%%v
if not "%DOTNET_MAJOR%"=="8" (
    echo [警告] 当前 .NET 版本不是 8.x（检测到 %DOTNET_MAJOR%.x），可能构建失败。
    echo 建议安装 .NET 8 SDK。
)

echo .NET SDK 已就绪：dotnet --version
dotnet --version
echo.

:: ---- 2. dotnet restore ----
echo [2/3] 还原 NuGet 包（需要网络）...
dotnet restore YTM.Lyrics.sln
if %ERRORLEVEL% NEQ 0 (
    echo [错误] NuGet 还原失败。请检查网络连接。
    pause
    exit /b 1
)
echo 还原完成。
echo.

:: ---- 3. dotnet build ----
echo [3/3] 构建项目...
dotnet build YTM.Lyrics.sln
if %ERRORLEVEL% NEQ 0 (
    echo [错误] 构建失败。请检查上方错误信息。
    pause
    exit /b 1
)
echo.
echo ========================================
echo   一切就绪！
echo   启动：dist\YTM.Lyrics.exe
echo   桌面歌词热键：Alt + Shift + W
echo   设置文件：%%APPDATA%%\YTM.Lyrics\settings.json
echo ========================================
echo.
pause
