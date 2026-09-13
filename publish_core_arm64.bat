@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

echo ====================================
echo Publish TigerClaw.Core only (ARM64)
echo ====================================
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "SHARED_BUILD_INFO=%ROOT%\next\TigerClaw.Shared\BuildInfo.cs"
set "DOTNET_CLI_HOME=%TEMP%\TigerClawDotnetHome"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"



set "DOTNET="
for /f "delims=" %%I in ('where.exe dotnet.exe 2^>nul') do if not defined DOTNET set "DOTNET=%%~fI"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET echo ERROR: dotnet.exe not found. & exit /b 1
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%" >nul 2>&1

set "CORE_PROJECT=%ROOT%\next\TigerClaw.Core\TigerClaw.Core.csproj"
set "CORE_OUT=%ROOT%\next\_run\ReleaseArm64\core-arm64"
set "RELEASE_DIR=%ROOT%\release_arm64"

if not exist "%CORE_PROJECT%" echo ERROR: Missing %CORE_PROJECT% & exit /b 1
if not exist "%RELEASE_DIR%" echo ERROR: Missing %RELEASE_DIR%. Run publish_arm64.bat first. & exit /b 1

echo.
echo Using dotnet : %DOTNET%

echo.
echo [1/3] Update Shared BuildInfo.cs
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')"`) do set "BUILD_UTC=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('yyyy.MM.dd-HHmm')"`) do set "BUILD_VERSION=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue';Set-Location -LiteralPath '%ROOT%';git rev-parse --short=8 HEAD"`) do if not defined BUILD_COMMIT set "BUILD_COMMIT=%%I"
if not defined BUILD_COMMIT set "BUILD_COMMIT=unknown"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%SHARED_BUILD_INFO%';$q=[char]34;$lines=@('namespace TigerClaw.Shared','{','    public static class BuildInfo','    {',('        public const string VersionLabel = '+$q+'%BUILD_VERSION%'+$q+';'),('        public const string Commit = '+$q+'%BUILD_COMMIT%'+$q+';'),('        public const string BuildUtc = '+$q+'%BUILD_UTC%'+$q+';'),'    }','}');[IO.File]::WriteAllText($p,[string]::Join([Environment]::NewLine,$lines),(New-Object Text.UTF8Encoding($true)))" || exit /b 1

echo.
echo [2/3] Publish TigerClaw.Core Native AOT (win-arm64)
"%DOTNET%" publish "%CORE_PROJECT%" -c Release -r win-arm64 --self-contained true -o "%CORE_OUT%" /p:PublishAot=true || exit /b 1

if not exist "%CORE_OUT%\TigerClaw.Core.exe" echo ERROR: Missing %CORE_OUT%\TigerClaw.Core.exe & exit /b 1

echo.
echo [3/3] Copy artifacts into %RELEASE_DIR%
taskkill /F /IM TigerClaw.Core.exe /T >nul 2>&1
ping 127.0.0.1 -n 3 >nul
copy /Y "%CORE_OUT%\TigerClaw.Core.exe" "%RELEASE_DIR%\TigerClaw.Core.exe" >nul || exit /b 1
if exist "%RELEASE_DIR%\TigerClaw.Core.exe.config" del /q "%RELEASE_DIR%\TigerClaw.Core.exe.config"

echo.
echo Done
echo Publish Core-only ARM64 succeeded: %RELEASE_DIR%\TigerClaw.Core.exe
exit /b 0
