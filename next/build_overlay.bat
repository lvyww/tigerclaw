@echo off
setlocal EnableExtensions
if "%~2"=="" (
  echo Usage: build_overlay.bat ARCH OUTPUT_DIR [CONFIGURATION] [JOBS]
  exit /b 2
)
set "OVERLAY_CONFIGURATION=%~3"
if not defined OVERLAY_CONFIGURATION set "OVERLAY_CONFIGURATION=Release"
set "OVERLAY_JOBS=%~4"
if not defined OVERLAY_JOBS set "OVERLAY_JOBS=8"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\build_overlay.ps1" -Arch "%~1" -OutputDirectory "%~f2" -Configuration "%OVERLAY_CONFIGURATION%" -Jobs "%OVERLAY_JOBS%"
exit /b %errorlevel%
