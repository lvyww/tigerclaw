@echo off
setlocal EnableExtensions

pushd "%~dp0" || exit /b 1

call :find_toolchain
if errorlevel 1 exit /b 1

if exist TigerClaw.dll del /q TigerClaw.dll >nul 2>&1
if exist TigerClaw_x64.lib del /q TigerClaw_x64.lib >nul 2>&1
if exist TigerClaw_arm64.lib del /q TigerClaw_arm64.lib >nul 2>&1
if exist TigerClawArm64X.obj del /q TigerClawArm64X.obj >nul 2>&1
if exist TigerClawArm64X_x64.obj del /q TigerClawArm64X_x64.obj >nul 2>&1

cl.exe /nologo /EHsc /W4 /DUNICODE /D_UNICODE /c /Fo:TigerClawArm64X.obj TigerClawArm64X.cpp || exit /b 1
cl.exe /nologo /EHsc /W4 /DUNICODE /D_UNICODE /arm64EC /c /Fo:TigerClawArm64X_x64.obj TigerClawArm64X.cpp || exit /b 1

link.exe /lib /nologo /machine:arm64ec /def:TigerClaw_x64.def /out:TigerClaw_x64.lib /ignore:4104 || exit /b 1
link.exe /lib /nologo /machine:arm64 /def:TigerClaw_arm64.def /out:TigerClaw_arm64.lib /ignore:4104 || exit /b 1
link.exe /dll /nologo /machine:arm64x /defArm64Native:TigerClaw_arm64.def /def:TigerClaw_x64.def ^
  /out:TigerClaw.dll TigerClawArm64X.obj TigerClawArm64X_x64.obj TigerClaw_x64.lib TigerClaw_arm64.lib /ignore:4104 || exit /b 1

exit /b 0

:find_toolchain
set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo ERROR: vswhere.exe not found.
  exit /b 1
)

set "VSROOT="
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64EC -property installationPath`) do if not defined VSROOT set "VSROOT=%%~fI"
if not defined VSROOT (
  echo ERROR: Visual Studio ARM64EC C++ tools are not installed.
  exit /b 1
)
if not exist "%VSROOT%\Common7\Tools\VsDevCmd.bat" (
  echo ERROR: VsDevCmd.bat not found under %VSROOT%.
  exit /b 1
)

call "%VSROOT%\Common7\Tools\VsDevCmd.bat" -arch=arm64 -host_arch=x64
if errorlevel 1 exit /b 1
exit /b 0
