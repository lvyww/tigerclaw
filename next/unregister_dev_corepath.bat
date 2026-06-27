@echo off
setlocal

set "CORE_REG_KEY=HKCU\Software\TigerClaw\Install"
set "CORE_REG_VALUE=CorePath"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_VALUE=TigerClawCore"

reg delete "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
reg delete "%RUN_KEY%" /v "%RUN_VALUE%" /f >nul 2>&1

echo Done.
exit /b 0
