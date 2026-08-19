@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "RUST_DIR=%ROOT%\rust\TigerClaw.Core.Rust"
set "TARGET=aarch64-pc-windows-msvc"
set "OUTPUT=%RUST_DIR%\target\%TARGET%\release\tigerclaw_core_rust.exe"
set "RELEASE_DIR=%ROOT%\release_arm64"

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

taskkill /F /IM TigerClaw.Core.exe /T >nul 2>&1
copy /Y "%OUTPUT%" "%RELEASE_DIR%\TigerClaw.Core.exe" >nul || exit /b 1

echo Rust Core ARM64 published: %RELEASE_DIR%\TigerClaw.Core.exe
exit /b 0
