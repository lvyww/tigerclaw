@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "CORE_EXE=%SCRIPT_DIR%TigerClaw.Core\bin\Debug\net48\TigerClaw.Core.exe"
set "CORE_REG_KEY=HKCU\Software\TigerClaw\Install"
set "CORE_REG_VALUE=CorePath"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_VALUE=TigerClawCore"

if not exist "%CORE_EXE%" (
  echo Core exe not found:
  echo   %CORE_EXE%
  echo Build first: next\build_next.bat
  exit /b 1
)

reg add "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /t REG_SZ /d "%CORE_EXE%" /f >nul || exit /b 1
reg add "%RUN_KEY%" /v "%RUN_VALUE%" /t REG_SZ /d "\"%CORE_EXE%\" --autorun --silent" /f >nul || exit /b 1

echo Done.
echo CorePath: %CORE_EXE%
echo Run value: %RUN_VALUE%
exit /b 0
