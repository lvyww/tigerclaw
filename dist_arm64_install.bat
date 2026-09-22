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
set "LOG_FILE=%SCRIPT_DIR%\install_arm64.log"
set "SOURCE_WRAPPER=%SCRIPT_DIR%\TigerClaw.dll"
set "SOURCE_ARM64=%SCRIPT_DIR%\TigerClawARM64.dll"
set "SOURCE_X64=%SCRIPT_DIR%\TigerClawx64.dll"
set "SOURCE_X86=%SCRIPT_DIR%\Win32\TigerClaw.dll"
set "CORE_EXE=%SCRIPT_DIR%\TigerClaw.Core.exe"
set "OVERLAY_EXE=%SCRIPT_DIR%\TigerClaw.Overlay.exe"
set "DIALOG_EXE=%SCRIPT_DIR%\TigerClaw.Dialog.exe"
set "SHARED_DLL=%SCRIPT_DIR%\TigerClaw.Shared.dll"
set "TARGET_DIR64=%ProgramFiles%\TigerClaw"
set "TARGET_WRAPPER=%TARGET_DIR64%\TigerClaw.dll"
set "TARGET_ARM64=%TARGET_DIR64%\TigerClawARM64.dll"
set "TARGET_X64=%TARGET_DIR64%\TigerClawx64.dll"
if defined ProgramFiles(x86) (set "TARGET_DIR32=%ProgramFiles(x86)%\TigerClaw") else (set "TARGET_DIR32=%ProgramFiles%\TigerClaw")
set "TARGET_X86=%TARGET_DIR32%\TigerClaw.dll"
set "TIP_CLSID={14493D3C-2059-41C0-805A-1F7841DE206B}"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_VALUE=TigerClawCore"
set "RUN_VALUE_LEGACY=TigerClaw"
set RUN_CMD="%CORE_EXE%" --autorun --silent
set "CORE_REG_KEY=HKCU\Software\TigerClaw\Install"
set "CORE_REG_KEY_HKLM=HKLM\Software\TigerClaw\Install"
set "CORE_REG_VALUE=CorePath"
set "DOTNET481_MIN_RELEASE=533320"
set "DOTNET481_DOWNLOAD_URL=https://dotnet.microsoft.com/download/dotnet-framework/net481"
set "REGSVR64=%windir%\System32\regsvr32.exe"
if exist "%windir%\Sysnative\regsvr32.exe" set "REGSVR64=%windir%\Sysnative\regsvr32.exe"
set "REGSVR32=%windir%\SysWOW64\regsvr32.exe"
set "FAILED=0"
set "LOCKED=0"

call :log "Install TigerClaw Windows on Arm package (ARM64X wrapper default)"
echo ====================================
echo Install TigerClaw for Windows on Arm - ARM64X wrapper default
echo ====================================
call :check_arm64_os || goto :failed
for %%P in ("%SOURCE_WRAPPER%" "%SOURCE_ARM64%" "%SOURCE_X64%" "%SOURCE_X86%" "%CORE_EXE%" "%OVERLAY_EXE%" "%DIALOG_EXE%" "%SHARED_DLL%") do if not exist %%~P echo [Error] Missing %%~P & call :log "Missing %%~P" & set "FAILED=1"
if "!FAILED!"=="1" goto :failed
call :check_dotnet481 || goto :dotnet_missing

echo [1/6] Clear launcher registry
call :clear_launcher_registry

echo [2/6] Unregister installed DLLs if present
call :unregister_if_exists "ARM64X" "%REGSVR64%" "%TARGET_WRAPPER%"
call :unregister_if_exists "Win32" "%REGSVR32%" "%TARGET_X86%"

echo [3/6] Delete installed DLLs
for %%F in ("%TARGET_WRAPPER%" "%TARGET_ARM64%" "%TARGET_X64%" "%TARGET_X86%") do (
  call :delete_if_exists "%%~F" || (set "FAILED=1" & set "LOCKED=1")
)
if "!FAILED!"=="1" goto :failed

echo [4/6] Copy and register DLLs
call :ensure_dir "%TARGET_DIR64%" || set "FAILED=1"
call :ensure_dir "%TARGET_DIR32%" || set "FAILED=1"
copy /Y "%SOURCE_WRAPPER%" "%TARGET_WRAPPER%" >nul || set "FAILED=1"
copy /Y "%SOURCE_ARM64%" "%TARGET_ARM64%" >nul || set "FAILED=1"
copy /Y "%SOURCE_X64%" "%TARGET_X64%" >nul || set "FAILED=1"
copy /Y "%SOURCE_X86%" "%TARGET_X86%" >nul || set "FAILED=1"
if "!FAILED!"=="1" goto :failed
call :register "ARM64X" "%REGSVR64%" "%TARGET_WRAPPER%" || set "FAILED=1"
call :register "Win32" "%REGSVR32%" "%TARGET_X86%" || set "FAILED=1"
if "!FAILED!"=="1" goto :failed

echo [5/6] Write CorePath and autorun
call :write_launcher_registry || set "FAILED=1"
if "!FAILED!"=="1" goto :failed

echo [6/6] Verify registry paths
call :verify_registry_path "ARM64X" "HKCR\CLSID\%TIP_CLSID%\InprocServer32" "%TARGET_WRAPPER%" || set "FAILED=1"
call :verify_registry_path "Win32" "HKCR\WOW6432Node\CLSID\%TIP_CLSID%\InprocServer32" "%TARGET_X86%" || set "FAILED=1"
if "!FAILED!"=="1" goto :failed

echo [Success] Install completed.
call :log "Install completed successfully."
if /I not "%~2"=="--quiet" call :popup "TigerClaw" "Install completed with ARM64X TSF registration. Keep this release folder because Core runs from here." "Info"
exit /b 0

:dotnet_missing
echo [Error] .NET Framework 4.8.1 is required on Windows on Arm.
start "" "%DOTNET481_DOWNLOAD_URL%" >nul 2>&1
call :log "Install blocked: .NET Framework 4.8.1 missing."
exit /b 1

:failed
if "!LOCKED!"=="1" tasklist /m TigerClaw.dll
echo [Error] Install failed. See %LOG_FILE%.
call :log "Install failed."
exit /b 1

:check_arm64_os
if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" exit /b 0
if /I "%PROCESSOR_ARCHITEW6432%"=="ARM64" exit /b 0
echo [Error] This package is only for Windows on Arm64.
exit /b 1

:check_dotnet481
set "DOTNET_RELEASE="
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| find /I "Release"') do set "DOTNET_RELEASE=%%R"
if not defined DOTNET_RELEASE exit /b 1
set /a DOTNET_RELEASE_NUM=%DOTNET_RELEASE% >nul 2>&1
if errorlevel 1 exit /b 1
if %DOTNET_RELEASE_NUM% LSS %DOTNET481_MIN_RELEASE% exit /b 1
exit /b 0

:clear_launcher_registry
reg delete "%RUN_KEY%" /v "%RUN_VALUE%" /f >nul 2>&1
reg delete "%RUN_KEY%" /v "%RUN_VALUE_LEGACY%" /f >nul 2>&1
reg delete "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
reg delete "%CORE_REG_KEY_HKLM%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
exit /b 0

:write_launcher_registry
reg add "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /t REG_SZ /d "%CORE_EXE%" /f >nul || exit /b 1
reg add "%CORE_REG_KEY_HKLM%" /v "%CORE_REG_VALUE%" /t REG_SZ /d "%CORE_EXE%" /f >nul || exit /b 1
reg add "%RUN_KEY%" /v "%RUN_VALUE%" /t REG_SZ /d "%RUN_CMD%" /f >nul || exit /b 1
exit /b 0

:ensure_dir
if exist "%~1" exit /b 0
mkdir "%~1" >nul 2>&1
if exist "%~1" exit /b 0
exit /b 1

:delete_if_exists
if not exist "%~1" exit /b 0
del /f /q "%~1" >nul 2>&1
if exist "%~1" exit /b 1
exit /b 0

:unregister_if_exists
if not exist "%~3" exit /b 0
if not exist "%~2" exit /b 0
"%~2" /s /u "%~3" >nul 2>&1
exit /b 0

:register
if not exist "%~2" exit /b 1
if not exist "%~3" exit /b 1
"%~2" /s "%~3"
exit /b %errorlevel%

:verify_registry_path
set "ACTUAL_PATH="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$k=Get-Item -Path 'Registry::%~2' -ErrorAction SilentlyContinue; if($k){$v=$k.GetValue(''); if($v){$v}}"`) do set "ACTUAL_PATH=%%I"
if /I "%ACTUAL_PATH%"=="%~3" exit /b 0
exit /b 1

:popup
setlocal
set "POPUP_TITLE=%~1"
set "POPUP_TEXT=%~2"
set "POPUP_STYLE=64"
powershell -NoProfile -ExecutionPolicy Bypass -STA -Command "$w=New-Object -ComObject WScript.Shell; $null=$w.Popup($env:POPUP_TEXT,0,$env:POPUP_TITLE,[int]$env:POPUP_STYLE)" >nul 2>&1
endlocal
exit /b 0

:log
setlocal DisableDelayedExpansion
set "LOG_LINE=[%date% %time%] %~1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$line=$env:LOG_LINE; [IO.File]::AppendAllText($env:LOG_FILE,$line+[Environment]::NewLine,(New-Object Text.UTF8Encoding($true)))" >nul 2>&1
endlocal
exit /b 0
