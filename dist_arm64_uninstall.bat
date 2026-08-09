@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

if /I "%~1"=="--elevated" goto :elevated
net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo [Info] Requesting administrator privileges...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs"
  exit /b 0
)

:elevated
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "LOG_FILE=%SCRIPT_DIR%\uninstall_arm64.log"
set "TARGET_DIR64=%ProgramFiles%\TigerClaw"
set "TARGET_WRAPPER=%TARGET_DIR64%\TigerClaw.dll"
set "TARGET_ARM64=%TARGET_DIR64%\TigerClawARM64.dll"
set "TARGET_X64=%TARGET_DIR64%\TigerClawx64.dll"
if defined ProgramFiles(x86) (set "TARGET_DIR32=%ProgramFiles(x86)%\TigerClaw") else (set "TARGET_DIR32=%ProgramFiles%\TigerClaw")
set "TARGET_X86=%TARGET_DIR32%\TigerClaw.dll"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_VALUE=TigerClawCore"
set "RUN_VALUE_LEGACY=TigerClaw"
set "CORE_REG_KEY=HKCU\Software\TigerClaw\Install"
set "CORE_REG_KEY_HKLM=HKLM\Software\TigerClaw\Install"
set "CORE_REG_VALUE=CorePath"
set "REGSVR64=%windir%\System32\regsvr32.exe"
if exist "%windir%\Sysnative\regsvr32.exe" set "REGSVR64=%windir%\Sysnative\regsvr32.exe"
set "REGSVR32=%windir%\SysWOW64\regsvr32.exe"
set "FAILED=0"
set "LOCKED=0"

echo ====================================
echo Uninstall TigerClaw for Windows on Arm
echo ====================================
reg delete "%RUN_KEY%" /v "%RUN_VALUE%" /f >nul 2>&1
reg delete "%RUN_KEY%" /v "%RUN_VALUE_LEGACY%" /f >nul 2>&1
reg delete "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
reg delete "%CORE_REG_KEY_HKLM%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
call :unregister_if_exists "%REGSVR64%" "%TARGET_WRAPPER%"
call :unregister_if_exists "%REGSVR32%" "%TARGET_X86%"
for %%F in ("%TARGET_WRAPPER%" "%TARGET_ARM64%" "%TARGET_X64%" "%TARGET_X86%") do (
  call :delete_if_exists "%%~F" || (set "FAILED=1" & set "LOCKED=1")
)
call :remove_dir_if_exists "%TARGET_DIR64%" || (set "FAILED=1" & set "LOCKED=1")
call :remove_dir_if_exists "%TARGET_DIR32%" || (set "FAILED=1" & set "LOCKED=1")
if "!FAILED!"=="1" goto :failed
echo [Success] Uninstall completed.
exit /b 0

:failed
if "!LOCKED!"=="1" tasklist /m TigerClaw.dll
echo [Error] Uninstall incomplete. Reboot and run again if files were locked.
exit /b 1

:unregister_if_exists
if not exist "%~2" exit /b 0
if not exist "%~1" exit /b 0
"%~1" /s /u "%~2" >nul 2>&1
exit /b 0

:delete_if_exists
if not exist "%~1" exit /b 0
del /f /q "%~1" >nul 2>&1
if exist "%~1" exit /b 1
exit /b 0

:remove_dir_if_exists
if not exist "%~1" exit /b 0
rmdir /s /q "%~1" >nul 2>&1
if exist "%~1" exit /b 1
exit /b 0
