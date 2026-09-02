@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

echo ===============================================
echo Publish TigerClaw.Core Native AOT only (ARM64)
echo ===============================================
echo NOTE: this builds the parallel Native AOT Core and directly replaces
echo release_arm64\TigerClaw.Core.exe. It does not replace config, code
echo tables, Overlay, Dialog, Sentence, Shared.dll or either TSF DLL.
echo.
echo The TSF DLL is not rebuilt. If the deployed TSF was built with
echo core_hash_verify_enabled=1, it will reject the new Core executable.
echo Rebuild the full ARM64 release when an embedded Core hash must match.

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "CONFIG_FILE=%ROOT%\publish_config.txt"
set "SHARED_BUILD_INFO=%ROOT%\next\TigerClaw.Shared\BuildInfo.cs"
set "AOT_PROJECT=%ROOT%\next\TigerClaw.Core.NativeAot\TigerClaw.Core.NativeAot.csproj"
set "AOT_OUT=%ROOT%\next\_run\NativeAotArm64"
set "AOT_EXE=%AOT_OUT%\TigerClaw.Core.exe"
set "RELEASE_DIR=%ROOT%\release_arm64"
set "RELEASE_EXE=%RELEASE_DIR%\TigerClaw.Core.exe"
set "STAGED_EXE=%RELEASE_DIR%\TigerClaw.Core.aot.new"
set "TRIAL_EXPIRE_UTC="
set "DEPLOY=1"
if /I "%~1"=="--build-only" set "DEPLOY=0"
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
if not defined DOTNET echo ERROR: dotnet.exe not found. Install the .NET 10 SDK first. & exit /b 1
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%" >nul 2>&1

if not exist "%AOT_PROJECT%" echo ERROR: Missing %AOT_PROJECT% & exit /b 1
if "%DEPLOY%"=="1" if not exist "%RELEASE_DIR%" echo ERROR: Missing %RELEASE_DIR%. Run publish_arm64.bat first. & exit /b 1

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
echo [2/3] Publish Native AOT Core (win-arm64)
"%DOTNET%" publish "%AOT_PROJECT%" -c Release -r win-arm64 --self-contained true -o "%AOT_OUT%" /p:PublishAot=true || exit /b 1
if not exist "%AOT_EXE%" echo ERROR: Missing %AOT_EXE% & exit /b 1
if "%DEPLOY%"=="0" (
  echo.
  echo Build-only publish succeeded: %AOT_EXE%
  exit /b 0
)

echo.
echo [3/3] Replace %RELEASE_EXE%
copy /Y "%AOT_EXE%" "%STAGED_EXE%" >nul || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$a=(Get-FileHash -Algorithm SHA256 -LiteralPath '%AOT_EXE%').Hash;$b=(Get-FileHash -Algorithm SHA256 -LiteralPath '%STAGED_EXE%').Hash;if($a -ne $b){throw 'Staged Core hash mismatch'}" || exit /b 1
taskkill /F /IM TigerClaw.Core.exe /T >nul 2>&1
move /Y "%STAGED_EXE%" "%RELEASE_EXE%" >nul || (
  echo ERROR: Could not replace %RELEASE_EXE%. The staged file remains at %STAGED_EXE%.
  exit /b 1
)

echo.
echo Done
echo Native AOT ARM64 Core replaced: %RELEASE_EXE%
echo Reminder: TSF DLL was NOT rebuilt. See the NOTE above if Core hash
echo verification is enabled in the deployed TSF.
exit /b 0
