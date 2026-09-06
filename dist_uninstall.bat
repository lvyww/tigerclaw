@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 936 >nul

if /I "%~1"=="--elevated" goto :elevated
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo [信息] 正在请求管理员权限...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs"
    exit /b 0
)

:elevated
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "LOG_FILE=%SCRIPT_DIR%\uninstall.log"

set "TARGET_DIR64=%ProgramFiles%\TigerClaw"
set "TARGET_DLL64=%TARGET_DIR64%\TigerClaw.dll"
if defined ProgramFiles(x86) (
    set "TARGET_DIR32=%ProgramFiles(x86)%\TigerClaw"
) else (
    set "TARGET_DIR32=%ProgramFiles%\TigerClaw"
)
set "TARGET_DLL32=%TARGET_DIR32%\TigerClaw.dll"

set "HAS_X86_COPY=1"
if /I "%TARGET_DLL32%"=="%TARGET_DLL64%" set "HAS_X86_COPY=0"

set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_VALUE=TigerClawCore"
set "RUN_VALUE_LEGACY=TigerClaw"

set "CORE_REG_KEY=HKCU\Software\TigerClaw\Install"
set "CORE_REG_KEY_HKLM=HKLM\Software\TigerClaw\Install"
set "CORE_REG_VALUE=CorePath"

set "REGSVR64=%windir%\System32\regsvr32.exe"
if exist "%windir%\Sysnative\regsvr32.exe" set "REGSVR64=%windir%\Sysnative\regsvr32.exe"
set "REGSVR32=%windir%\SysWOW64\regsvr32.exe"
if not exist "%REGSVR32%" set "REGSVR32=%REGSVR64%"

set "FAILED=0"
set "LOCKED=0"

call :log "===================================="
call :log "Uninstall TSF DLL from Program Files"

echo ====================================
echo 卸载 TigerClaw 输入法
echo ====================================
echo [信息] 目标 x64 DLL : %TARGET_DLL64%
echo [信息] 目标 x86 DLL : %TARGET_DLL32%

echo [1/4] 清理 CorePath 和自启动项
call :clear_launcher_registry

echo [2/4] 注销已安装 DLL（如果存在）
call :unregister_if_exists "x64" "%REGSVR64%" "%TARGET_DLL64%"
if "%HAS_X86_COPY%"=="1" call :unregister_if_exists "Win32" "!REGSVR32!" "!TARGET_DLL32!"

echo [3/4] 删除已安装 DLL
call :delete_if_exists "x64" "%TARGET_DLL64%"
if errorlevel 1 (
    set "FAILED=1"
    set "LOCKED=1"
)
if "%HAS_X86_COPY%"=="1" (
    call :delete_if_exists "Win32" "!TARGET_DLL32!"
    if errorlevel 1 (
        set "FAILED=1"
        set "LOCKED=1"
    )
)

echo [4/4] 删除 Program Files 中的 TigerClaw 目录
call :remove_dir_if_exists "x64" "%TARGET_DIR64%"
if errorlevel 1 (
    set "FAILED=1"
    set "LOCKED=1"
)
if "%HAS_X86_COPY%"=="1" (
    call :remove_dir_if_exists "Win32" "!TARGET_DIR32!"
    if errorlevel 1 (
        set "FAILED=1"
        set "LOCKED=1"
    )
)

if "!FAILED!"=="1" goto :failed

echo [成功] 卸载完成。
echo [信息] 已清理注册信息，原始 bime 目录会保留。
call :log "Uninstall completed successfully."
call :popup "卸载完成" "卸载成功。已清理注册信息，原始 bime 目录已保留。" "Info"
exit /b 0

:failed
call :log "Enter failed branch failed=%FAILED% locked=%LOCKED%"
if "!LOCKED!"=="1" (
    echo [错误] 仍有 DLL 或目录被占用，未能完全清理。
    echo [错误] 请注销重新登录或重启系统后再次运行 卸载.bat。
    call :show_locking_processes
    call :log "Uninstall incomplete due to locked DLL or directory. reboot_required=1"
    call :popup "卸载未完成" "仍有文件被占用。请重启系统后再次运行 卸载.bat 继续清理。" "Warning"
) else (
    echo [错误] 卸载失败。请查看日志：%LOG_FILE%
    call :log "Uninstall failed."
    call :popup "卸载失败" "卸载失败，请查看 uninstall.log 后重试。" "Error"
)
exit /b 1

:clear_launcher_registry
reg delete "%RUN_KEY%" /v "%RUN_VALUE%" /f >nul 2>&1
reg delete "%RUN_KEY%" /v "%RUN_VALUE_LEGACY%" /f >nul 2>&1

reg delete "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
reg delete "%CORE_REG_KEY_HKLM%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
call :log "Removed CorePath/autorun values."
exit /b 0

:show_locking_processes
echo [信息] 当前加载 TigerClaw.dll 的进程：
tasklist /m TigerClaw.dll
call :log "tasklist /m TigerClaw.dll executed"
exit /b 0

:remove_dir_if_exists
set "ARCH=%~1"
set "DIR_PATH=%~2"
if not exist "!DIR_PATH!" (
    call :log "Remove dir skip (!ARCH!), path missing: !DIR_PATH!"
    exit /b 0
)
rmdir /s /q "!DIR_PATH!" >nul 2>&1
if exist "!DIR_PATH!" (
    echo [错误] 删除 !ARCH! 目录失败：!DIR_PATH!
    call :log "Remove dir failed (!ARCH!) path=!DIR_PATH!"
    exit /b 1
)
call :log "Remove dir success (!ARCH!) path=!DIR_PATH!"
exit /b 0
:delete_if_exists
set "ARCH=%~1"
set "DLL_PATH=%~2"
if not exist "!DLL_PATH!" (
    call :log "Delete skip (!ARCH!), file missing: !DLL_PATH!"
    exit /b 0
)
del /f /q "!DLL_PATH!" >nul 2>&1
if exist "!DLL_PATH!" (
    echo [错误] 删除 !ARCH! DLL 失败：!DLL_PATH!
    call :log "Delete failed (!ARCH!) path=!DLL_PATH!"
    exit /b 1
)
call :log "Delete success (!ARCH!) path=!DLL_PATH!"
exit /b 0
:unregister_if_exists
set "ARCH=%~1"
set "REGSVR=%~2"
set "DLL_PATH=%~3"
if not exist "!DLL_PATH!" (
    call :log "Unregister skip (!ARCH!), file missing: !DLL_PATH!"
    exit /b 0
)
if not exist "!REGSVR!" (
    call :log "Unregister skip (!ARCH!), regsvr32 missing: !REGSVR!"
    exit /b 0
)
"!REGSVR!" /s /u "!DLL_PATH!" >nul 2>&1
call :log "Unregister attempted (!ARCH!) ec=%errorlevel% path=!DLL_PATH!"
exit /b 0
:popup
setlocal
set "POPUP_TITLE=%~1"
set "POPUP_TEXT=%~2"
set "POPUP_ICON=%~3"
set "POPUP_STYLE=64"
if /I "%POPUP_ICON%"=="Error" set "POPUP_STYLE=16"
if /I "%POPUP_ICON%"=="Warning" set "POPUP_STYLE=48"
if /I "%POPUP_ICON%"=="Question" set "POPUP_STYLE=32"
set /a POPUP_STYLE=%POPUP_STYLE% + 4096
call :log "Popup attempt title=%POPUP_TITLE% icon=%POPUP_ICON%"
powershell -NoProfile -ExecutionPolicy Bypass -STA -Command "$w=New-Object -ComObject WScript.Shell; $null=$w.Popup($env:POPUP_TEXT,0,$env:POPUP_TITLE,[int]$env:POPUP_STYLE)" >nul 2>&1
if "%errorlevel%"=="0" ( endlocal & exit /b 0 )
call :log "Popup via WScript.Shell failed ec=%errorlevel%"
mshta "javascript:var sh=new ActiveXObject('WScript.Shell'); sh.Popup('%POPUP_TEXT%',0,'%POPUP_TITLE%',%POPUP_STYLE%);close();" >nul 2>&1
if not "%errorlevel%"=="0" call :log "Popup via mshta failed ec=%errorlevel%"
endlocal
exit /b 0
:log
setlocal DisableDelayedExpansion
set "LOG_LINE=[%date% %time%] %~1"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$line=$env:LOG_LINE; [System.IO.File]::AppendAllText($env:LOG_FILE, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($true)))" >nul 2>&1
if not "%errorlevel%"=="0" >>"%LOG_FILE%" echo [%date% %time%] %~1
endlocal
exit /b 0
