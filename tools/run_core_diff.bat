@echo off
setlocal EnableExtensions
cd /d "%~dp0.."

where python.exe >nul 2>nul || (echo ERROR: python.exe not found. & exit /b 1)
where cargo.exe >nul 2>nul || (echo ERROR: cargo.exe not found. & exit /b 1)
for /f "delims=" %%I in ('where dotnet.exe 2^>nul') do if not defined DOTNET set "DOTNET=%%~fI"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET echo ERROR: dotnet.exe not found. & exit /b 1

set "CSHARP_PROJECT=next\TigerClaw.Core.Tests\TigerClaw.Core.Tests.csproj"
set "CSHARP_EXE=next\_run\Tests\Release\net48\TigerClaw.Core.Tests.exe"
set "RUST_MANIFEST=rust\TigerClaw.Core.Rust\Cargo.toml"
set "RUST_EXE=rust\TigerClaw.Core.Rust\target\release\tigerclaw_core_rust.exe"
if not defined CORE_DIFF_REPORT set "CORE_DIFF_REPORT=.core_diff\report.json"
if not defined CORE_DIFF_WORK set "CORE_DIFF_WORK=.core_diff\work"

"%DOTNET%" msbuild "%CSHARP_PROJECT%" /restore /p:Configuration=Release /m /v:minimal || exit /b 1
"%CSHARP_EXE%" || exit /b 1
cargo test --manifest-path "%RUST_MANIFEST%" || exit /b 1
cargo build --release --manifest-path "%RUST_MANIFEST%" || exit /b 1

python.exe tools\compare_core_key_trace.py ^
  --rust "%RUST_EXE%" ^
  --csharp "%CSHARP_EXE%" ^
  --suite tools\core_diff\traces ^
  --fixture tools\core_diff\fixture ^
  --allow-synthetic ^
  --keep-work "%CORE_DIFF_WORK%" ^
  --report "%CORE_DIFF_REPORT%"
exit /b %errorlevel%
