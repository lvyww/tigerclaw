@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

echo ====================================
echo Publish TigerClaw.Core only (ARM64)
echo ====================================
echo NOTE: this only rebuilds TigerClaw.Core.exe. It does NOT rebuild the
echo TSF DLL, so BIME_EMBED_CORE_SHA256 in EmbeddedBuildInfo.h is left
echo untouched. If the deployed TSF DLL was built with
echo core_hash_verify_enabled=1, it will reject this new Core.exe over the
echo pipe. Run publish_arm64.bat instead when the TSF DLL also needs to
echo match, or set core_hash_verify_enabled=0 and rebuild the DLL once.

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "CONFIG_FILE=%ROOT%\publish_config.txt"
set "SHARED_BUILD_INFO=%ROOT%\next\TigerClaw.Shared\BuildInfo.cs"
set "TRIAL_EXPIRE_UTC="
set "DOTNET_CLI_HOME=%TEMP%\TigerClawDotnetHome"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"

if exist "%CONFIG_FILE%" (
  for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$cfg='%CONFIG_FILE%';$v='';$line=Get-Content -LiteralPath $cfg -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*trial_expire_utc\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1;if($line){$vv=($line -split '=',2)[1].Trim();if($vv -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$'){$v=$vv}};$v"`) do set "TRIAL_EXPIRE_UTC=%%I"
)
if not defined TRIAL_EXPIRE_UTC (
  echo ERROR: Missing or invalid trial_expire_utc in %CONFIG_FILE%
  exit /b 1
)

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
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%SHARED_BUILD_INFO%';$q=[char]34;$lines=@('namespace TigerClaw.Shared','{','    public static class BuildInfo','    {',('        public const string VersionLabel = '+$q+'%BUILD_VERSION%'+$q+';'),('        public const string Commit = '+$q+'%BUILD_COMMIT%'+$q+';'),('        public const string BuildUtc = '+$q+'%BUILD_UTC%'+$q+';'),('        public const string TrialExpireUtc = '+$q+'%TRIAL_EXPIRE_UTC%'+$q+';'),'    }','}');[IO.File]::WriteAllText($p,[string]::Join([Environment]::NewLine,$lines),(New-Object Text.UTF8Encoding($true)))" || exit /b 1

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
echo Reminder: TSF DLL was NOT rebuilt. See the NOTE above if core hash
echo verification is enabled on the deployed TigerClaw.dll/TigerClawARM64.dll.
exit /b 0
