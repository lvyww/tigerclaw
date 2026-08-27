@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "RUST_DIR=%ROOT%\rust\TigerClaw.Core.Rust"
set "TARGET=aarch64-pc-windows-msvc"
set "OUTPUT=%RUST_DIR%\target\%TARGET%\release\tigerclaw_core_rust.exe"
set "RELEASE_DIR=%ROOT%\release_arm64"
set "CORE_DST=%RELEASE_DIR%\TigerClaw.Core.exe"
set "CORE_CSHARP_BAK=%RELEASE_DIR%\TigerClaw.Core.csharp.exe"
set "CORE_RUST_COPY=%RELEASE_DIR%\TigerClaw.Core.rust.exe"

if not exist "%RUST_DIR%\Cargo.toml" (
  echo ERROR: Missing Rust Core project: %RUST_DIR%
  exit /b 1
)

where.exe cargo.exe >nul 2>&1 || (
  echo ERROR: cargo.exe was not found. Install Rust MSVC and run from an ARM64 Developer Command Prompt.
  exit /b 1
)

echo Building Rust Core for %TARGET%...
pushd "%RUST_DIR%" || exit /b 1
cargo build --release --target %TARGET%
set "BUILD_ERROR=%ERRORLEVEL%"
popd
if not "%BUILD_ERROR%"=="0" exit /b %BUILD_ERROR%

if not exist "%OUTPUT%" (
  echo ERROR: Missing build artifact: %OUTPUT%
  exit /b 1
)
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%" || exit /b 1

if not exist "%RELEASE_DIR%\TigerClaw.Overlay.exe" (
  echo WARNING: %RELEASE_DIR% has no Overlay/Dialog package.
  echo          Run publish_arm64.bat first if you want a full ARM64 test tree.
)

taskkill /F /IM TigerClaw.Core.exe /T >nul 2>&1
for /L %%I in (1,1,50) do (
  tasklist /FI "IMAGENAME eq TigerClaw.Core.exe" /NH 2>nul | findstr /I /C:"TigerClaw.Core.exe" >nul || goto core_stopped
  >nul 2>&1 ping.exe -n 2 127.0.0.1
)
echo ERROR: TigerClaw.Core.exe did not exit in time.
exit /b 1
:core_stopped

if exist "%CORE_DST%" if not exist "%CORE_CSHARP_BAK%" (
  copy /Y "%CORE_DST%" "%CORE_CSHARP_BAK%" >nul || exit /b 1
  echo Backed up C# Core: %CORE_CSHARP_BAK%
)

copy /Y "%OUTPUT%" "%CORE_DST%" >nul || exit /b 1
copy /Y "%OUTPUT%" "%CORE_RUST_COPY%" >nul || exit /b 1

echo Rust Core ARM64 deployed for replacement test:
echo   Active : %CORE_DST%
echo   Rust   : %CORE_RUST_COPY%
if exist "%CORE_CSHARP_BAK%" echo   C# bak : %CORE_CSHARP_BAK%
echo Restore C# Core with:
echo   copy /Y "%CORE_CSHARP_BAK%" "%CORE_DST%"
exit /b 0
