@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "TIP_CLSID={14493D3C-2059-41C0-805A-1F7841DE206B}"
set "TSF_LOG=%USERPROFILE%\bime_tsf.log"
set "INSTALL_LOG=%SCRIPT_DIR%\install_arm64.log"

echo ====================================
echo TigerClaw ARM64 UWP diagnostics
echo ====================================
echo.

echo [OS]
echo PROCESSOR_ARCHITECTURE=%PROCESSOR_ARCHITECTURE%
echo PROCESSOR_ARCHITEW6432=%PROCESSOR_ARCHITEW6432%
ver
echo.

echo [Files]
for %%F in ("%ProgramFiles%\TigerClaw\TigerClaw.dll" "%ProgramFiles%\TigerClaw\TigerClawARM64.dll" "%ProgramFiles%\TigerClaw\TigerClawx64.dll" "%ProgramFiles(x86)%\TigerClaw\TigerClaw.dll") do (
  if exist %%~F (echo OK %%~F) else echo MISSING %%~F
)
echo.

echo [Registry]
powershell -NoProfile -ExecutionPolicy Bypass -Command "$keys=@('Registry::HKCR\CLSID\%TIP_CLSID%\InprocServer32','Registry::HKCR\CLSID\%TIP_CLSID%\LocalServer32','Registry::HKCR\WOW6432Node\CLSID\%TIP_CLSID%\InprocServer32','Registry::HKLM\SOFTWARE\Microsoft\CTF\TIP\%TIP_CLSID%'); foreach($k in $keys){$item=Get-Item -Path $k -ErrorAction SilentlyContinue; if($item){$def=$item.GetValue(''); $enable=$item.GetValue('Enable'); Write-Host ($k + ' default=' + $def + ' Enable=' + $enable)} else {Write-Host ($k + ' MISSING')}}"
echo.

echo [Loaded TigerClaw.dll]
tasklist /m TigerClaw.dll 2>nul
echo.

echo [TigerClaw processes]
tasklist /fi "imagename eq TigerClaw.Core.exe"
tasklist /fi "imagename eq TigerClaw.Overlay.exe"
tasklist /fi "imagename eq TigerClaw.Dialog.exe"
tasklist /fi "imagename eq TigerClaw.TsfServer.exe"
echo.

echo [TSF log tail]
if exist "%TSF_LOG%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:TSF_LOG -Tail 120"
) else (
  echo Missing %TSF_LOG%
)
echo.

echo [Install log tail]
if exist "%INSTALL_LOG%" (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Content -LiteralPath $env:INSTALL_LOG -Tail 80"
) else (
  echo Missing %INSTALL_LOG%
)

endlocal
exit /b 0
