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
set "LOG_FILE=%SCRIPT_DIR%\install.log"

set "SOURCE_DLL64=%SCRIPT_DIR%\x64\TigerClaw.dll"
set "SOURCE_DLL32=%SCRIPT_DIR%\Win32\TigerClaw.dll"
set "CORE_EXE=%SCRIPT_DIR%\TigerClaw.Core.exe"
set "OVERLAY_EXE=%SCRIPT_DIR%\TigerClaw.Overlay.exe"
set "DIALOG_EXE=%SCRIPT_DIR%\TigerClaw.Dialog.exe"
set "SHARED_DLL=%SCRIPT_DIR%\TigerClaw.Shared.dll"

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

set "TIP_CLSID={14493D3C-2059-41C0-805A-1F7841DE206B}"
set "RUN_KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "RUN_VALUE=TigerClawCore"
set "RUN_VALUE_LEGACY=TigerClaw"

set "RUN_CMD=\"%CORE_EXE%\" --autorun --silent"
set "CORE_REG_KEY=HKCU\Software\TigerClaw\Install"
set "CORE_REG_KEY_HKLM=HKLM\Software\TigerClaw\Install"
set "CORE_REG_VALUE=CorePath"

set "DOTNET48_MIN_RELEASE=528040"
set "DOTNET48_DOWNLOAD_URL=https://dotnet.microsoft.com/zh-cn/download/dotnet-framework/net48"

set "REGSVR64=%windir%\System32\regsvr32.exe"
if exist "%windir%\Sysnative\regsvr32.exe" set "REGSVR64=%windir%\Sysnative\regsvr32.exe"
set "REGSVR32=%windir%\SysWOW64\regsvr32.exe"
if not exist "%REGSVR32%" set "REGSVR32=%REGSVR64%"

set "FAILED=0"
set "LOCKED=0"

call :log "===================================="
call :log "Install/Upgrade TSF DLL via Program Files"
call :log "ScriptDir=%SCRIPT_DIR%"

echo ====================================
echo 安装/升级 TigerClaw 输入法
echo ====================================
echo [信息] 源 x64 DLL  : %SOURCE_DLL64%
echo [信息] 源 x86 DLL  : %SOURCE_DLL32%
echo [信息] Core EXE    : %CORE_EXE%
echo [信息] 目标 x64 DLL: %TARGET_DLL64%
echo [信息] 目标 x86 DLL: %TARGET_DLL32%

call :require_file "源 x64 DLL" "%SOURCE_DLL64%"
if errorlevel 1 set "FAILED=1"
if "%HAS_X86_COPY%"=="1" (
    call :require_file "源 x86 DLL" "%SOURCE_DLL32%"
    if errorlevel 1 set "FAILED=1"
)
call :require_file "Core EXE" "%CORE_EXE%"
if errorlevel 1 set "FAILED=1"
call :require_file "Overlay EXE" "%OVERLAY_EXE%"
if errorlevel 1 set "FAILED=1"
call :require_file "Dialog EXE" "%DIALOG_EXE%"
if errorlevel 1 set "FAILED=1"
call :require_file "Shared DLL" "%SHARED_DLL%"
if errorlevel 1 set "FAILED=1"
if "!FAILED!"=="1" goto :failed

call :check_dotnet48
if errorlevel 1 goto :dotnet_missing

echo [1/6] 清理旧 CorePath 与自启动项
call :clear_launcher_registry

echo [2/6] 注销旧 DLL（如果存在）
call :unregister_if_exists "x64" "%REGSVR64%" "%TARGET_DLL64%"
if "%HAS_X86_COPY%"=="1" call :unregister_if_exists "Win32" "!REGSVR32!" "!TARGET_DLL32!"

echo [3/6] 删除旧 DLL
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
if "!FAILED!"=="1" goto :failed

echo [4/6] 复制并注册新 DLL
call :ensure_dir "%TARGET_DIR64%"
if errorlevel 1 set "FAILED=1"
if "%HAS_X86_COPY%"=="1" (
    call :ensure_dir "!TARGET_DIR32!"
    if errorlevel 1 set "FAILED=1"
)
if "!FAILED!"=="1" goto :failed

copy /Y "%SOURCE_DLL64%" "%TARGET_DLL64%" >nul
if errorlevel 1 (
    echo [错误] 复制 x64 DLL 失败。
    call :log "Copy failed x64 src=%SOURCE_DLL64% dst=%TARGET_DLL64%"
    set "FAILED=1"
)
if "%HAS_X86_COPY%"=="1" (
    copy /Y "!SOURCE_DLL32!" "!TARGET_DLL32!" >nul
    if errorlevel 1 (
        echo [错误] 复制 Win32 DLL 失败。
        call :log "Copy failed x86 src=%SOURCE_DLL32% dst=%TARGET_DLL32%"
        set "FAILED=1"
    )
)
if "!FAILED!"=="1" goto :failed

call :register "x64" "%REGSVR64%" "%TARGET_DLL64%"
if errorlevel 1 set "FAILED=1"
if "%HAS_X86_COPY%"=="1" (
    call :register "Win32" "!REGSVR32!" "!TARGET_DLL32!"
    if errorlevel 1 set "FAILED=1"
)
if "!FAILED!"=="1" goto :failed

echo [5/6] 写入 CorePath 与自启动项
call :write_launcher_registry
if errorlevel 1 set "FAILED=1"
if "!FAILED!"=="1" goto :failed

echo [6/6] 校验注册表路径
call :verify_registry_path "x64" "HKCR\CLSID\%TIP_CLSID%\InprocServer32" "%TARGET_DLL64%"
if errorlevel 1 set "FAILED=1"
if "%HAS_X86_COPY%"=="1" (
    call :verify_registry_path "Win32" "HKCR\WOW6432Node\CLSID\%TIP_CLSID%\InprocServer32" "!TARGET_DLL32!"
    if errorlevel 1 set "FAILED=1"
)
if "!FAILED!"=="1" goto :failed

echo [成功] 安装完成。
echo [信息] 请不要删除当前目录，配置和码表都保存在此目录。
call :log "Install completed successfully."
call :popup "安装完成" "安装成功。请勿删除当前目录，配置和码表均保存在此目录。" "Info"
exit /b 0

:dotnet_missing
echo [错误] 需要 .NET Framework 4.8。
echo [信息] 正在打开下载页面：%DOTNET48_DOWNLOAD_URL%
call :log "Install blocked: .NET Framework 4.8 missing."
start "" "%DOTNET48_DOWNLOAD_URL%" >nul 2>&1
call :popup "安装失败" "检测到未安装 .NET Framework 4.8。已打开下载页面，请安装后重新运行 安装.bat。" "Error"
exit /b 1

:failed
call :log "Enter failed branch failed=%FAILED% locked=%LOCKED%"
if "!LOCKED!"=="1" (
    echo [错误] Program Files 中旧 DLL 仍被占用。
    echo [错误] 已尽量注销旧输入法，请注销重新登录或重启后重新运行 安装.bat。
    call :show_locking_processes
    call :log "Install failed due to locked old DLL. reboot_required=1"
    call :popup "安装未完成" "旧 DLL 被占用。请重启系统后重新运行 安装.bat 继续安装。" "Warning"
) else (
    echo [错误] 安装失败。请查看日志：%LOG_FILE%
    call :log "Install failed."
    call :popup "安装失败" "安装失败，请查看 install.log 后重试。" "Error"
)
exit /b 1

:check_dotnet48
set "DOTNET_RELEASE="
for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| find /I "Release"') do set "DOTNET_RELEASE=%%R"
if not defined DOTNET_RELEASE (
    for /f "tokens=3" %%R in ('reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| find /I "Release"') do set "DOTNET_RELEASE=%%R"
)
if not defined DOTNET_RELEASE (
    call :log "Check .NET 4.8 failed: Release value not found."
    exit /b 1
)
set /a DOTNET_RELEASE_NUM=%DOTNET_RELEASE% >nul 2>&1
if errorlevel 1 (
    call :log "Check .NET 4.8 failed: invalid release value %DOTNET_RELEASE%."
    exit /b 1
)
if %DOTNET_RELEASE_NUM% LSS %DOTNET48_MIN_RELEASE% (
    call :log "Check .NET 4.8 failed: release=%DOTNET_RELEASE_NUM% minimum=%DOTNET48_MIN_RELEASE%."
    exit /b 1
)
call :log "Check .NET 4.8 ok: release=%DOTNET_RELEASE_NUM%."
exit /b 0

:clear_launcher_registry
reg delete "%RUN_KEY%" /v "%RUN_VALUE%" /f >nul 2>&1
reg delete "%RUN_KEY%" /v "%RUN_VALUE_LEGACY%" /f >nul 2>&1

reg delete "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
reg delete "%CORE_REG_KEY_HKLM%" /v "%CORE_REG_VALUE%" /f >nul 2>&1
call :log "Cleared old CorePath/autorun values."
exit /b 0

:write_launcher_registry
reg add "%CORE_REG_KEY%" /v "%CORE_REG_VALUE%" /t REG_SZ /d "%CORE_EXE%" /f >nul
if errorlevel 1 (
    call :log "Write HKCU CorePath failed path=%CORE_EXE%"
    exit /b 1
)
reg add "%CORE_REG_KEY_HKLM%" /v "%CORE_REG_VALUE%" /t REG_SZ /d "%CORE_EXE%" /f >nul
if errorlevel 1 (
    call :log "Write HKLM CorePath failed path=%CORE_EXE%"
    exit /b 1
)
reg add "%RUN_KEY%" /v "%RUN_VALUE%" /t REG_SZ /d "%RUN_CMD%" /f >nul
if errorlevel 1 (
    call :log "Write autorun failed value=%RUN_CMD%"
    exit /b 1
)
call :log "Wrote HKCU/HKLM CorePath=%CORE_EXE% and autorun."
exit /b 0

:show_locking_processes
echo [信息] 当前加载 TigerClaw.dll 的进程：
tasklist /m TigerClaw.dll
call :log "tasklist /m TigerClaw.dll executed"
exit /b 0

:ensure_dir
if exist "%~1" exit /b 0
mkdir "%~1" >nul 2>&1
if exist "%~1" (
    call :log "Created dir: %~1"
    exit /b 0
)
call :log "Create dir failed: %~1"
exit /b 1

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
:register
set "ARCH=%~1"
set "REGSVR=%~2"
set "DLL_PATH=%~3"
if not exist "!REGSVR!" (
    call :log "Register failed (!ARCH!), regsvr32 missing: !REGSVR!"
    exit /b 1
)
if not exist "!DLL_PATH!" (
    call :log "Register failed (!ARCH!), DLL missing: !DLL_PATH!"
    exit /b 1
)
"!REGSVR!" /s "!DLL_PATH!"
set "EC=%errorlevel%"
if not "!EC!"=="0" (
    call :log "Register failed (!ARCH!) ec=!EC! path=!DLL_PATH!"
    exit /b !EC!
)
call :log "Register success (!ARCH!) path=!DLL_PATH!"
exit /b 0
:verify_registry_path
set "ARCH=%~1"
set "REG_KEY=%~2"
set "EXPECTED_PATH=%~3"
set "ACTUAL_PATH="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$k=Get-Item -Path 'Registry::%REG_KEY%' -ErrorAction SilentlyContinue; if($k){$v=$k.GetValue(''); if($v){$v}}"`) do set "ACTUAL_PATH=%%I"
if not defined ACTUAL_PATH (
    call :log "Verify failed (%ARCH%) missing key=%REG_KEY%"
    exit /b 1
)
if /I "%ACTUAL_PATH%"=="%EXPECTED_PATH%" (
    call :log "Verify success (%ARCH%) path=%ACTUAL_PATH%"
    exit /b 0
)
call :log "Verify failed (%ARCH%) expected=%EXPECTED_PATH% actual=%ACTUAL_PATH%"
exit /b 1

:require_file
if exist "%~2" (
    call :log "%~1 found: %~2"
    exit /b 0
)
echo [错误] %~1 不存在：%~2
call :log "%~1 missing: %~2"
exit /b 1

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
