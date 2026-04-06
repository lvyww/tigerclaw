@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "RELEASE_DIR=%~dp0"
if "%RELEASE_DIR:~-1%"=="\" set "RELEASE_DIR=%RELEASE_DIR:~0,-1%"

set "SEVEN_Z=%RELEASE_DIR%\7z.exe"
set "SEVEN_Z_DLL=%RELEASE_DIR%\7z.dll"
set "STAGE_DIR=%RELEASE_DIR%\TigerClaw"
set "CONFIG_PATH=%RELEASE_DIR%\publish_config.txt"
if not exist "%CONFIG_PATH%" set "CONFIG_PATH=%RELEASE_DIR%\..\publish_config.txt"

if not exist "%SEVEN_Z%" (
    echo ERROR: 7z.exe not found: %SEVEN_Z%
    exit /b 1
)
if not exist "%SEVEN_Z_DLL%" (
    echo ERROR: 7z.dll not found: %SEVEN_Z_DLL%
    exit /b 1
)

set "PACK_RELEASE_DIR=%RELEASE_DIR%"
set "PACK_STAGE_DIR=%STAGE_DIR%"
set "PACK_CONFIG_PATH=%CONFIG_PATH%"
set "PACK_SEVEN_Z=%SEVEN_Z%"

echo ====================================
echo Pack TigerClaw Release
echo ====================================
echo ReleaseDir: %PACK_RELEASE_DIR%
echo Config: %PACK_CONFIG_PATH%

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$release=$env:PACK_RELEASE_DIR;" ^
  "$stage=$env:PACK_STAGE_DIR;" ^
  "$configPath=$env:PACK_CONFIG_PATH;" ^
  "$sevenZ=$env:PACK_SEVEN_Z;" ^
  "$imeName = -join ([char[]](0x864E,0x722A,0x8F93,0x5165,0x6CD5));" ^
  "$limitTag = -join ([char[]](0x9650,0x671F));" ^
  "$installBat = (-join ([char[]](0x5B89,0x88C5))) + '.bat';" ^
  "$uninstallBat = (-join ([char[]](0x5378,0x8F7D))) + '.bat';" ^
  "$items=@('TigerClaw.Core.exe','TigerClaw.Core.exe.config','TigerClaw.Overlay.exe','TigerClaw.Overlay.exe.config','TigerClaw.Dialog.exe','TigerClaw.Dialog.exe.config','TigerClaw.exe','TigerClaw.Shared.dll',((-join ([char[]](0x66F4,0x65B0,0x65E5,0x5FD7))) + '.txt'),$installBat,$uninstallBat,'x64','Win32');" ^
  "if(-not (Test-Path -LiteralPath $stage)){ New-Item -Path $stage -ItemType Directory -Force | Out-Null };" ^
  "foreach($item in $items){" ^
  "  $src=Join-Path $release $item;" ^
  "  if(-not (Test-Path -LiteralPath $src)){ throw ('Missing required item: {0}' -f $src) };" ^
  "  Copy-Item -LiteralPath $src -Destination $stage -Recurse -Force;" ^
  "};" ^
  "$trialLine='';" ^
  "if(Test-Path -LiteralPath $configPath){" ^
  "  $trialLine = Get-Content -LiteralPath $configPath -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*trial_expire_utc\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1;" ^
  "};" ^
  "$expireDateText='';" ^
  "if($trialLine){" ^
  "  $trialText = ($trialLine -split '=',2)[1].Trim();" ^
  "  if($trialText -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$'){" ^
  "    $expireDateText = ($trialText.Substring(0,10) -replace '-','');" ^
  "  }" ^
  "};" ^
  "$today = Get-Date -Format 'yyyyMMdd';" ^
  "$fileName = if($expireDateText){ '{0}-{1}{2}.7z' -f $imeName,$limitTag,$expireDateText } else { '{0}-{1}.7z' -f $imeName,$today };" ^
  "$archivePath = Join-Path $release $fileName;" ^
  "if(Test-Path -LiteralPath $archivePath){ Remove-Item -LiteralPath $archivePath -Force };" ^
  "Push-Location $release;" ^
  "try {" ^
  "  & $sevenZ a -t7z -mx=9 -mmt=on -- $archivePath '.\TigerClaw';" ^
  "  if($LASTEXITCODE -ne 0){ throw ('7z failed, exit=' + $LASTEXITCODE) };" ^
  "} finally { Pop-Location };" ^
  "Write-Host ('Pack done: {0}' -f $archivePath);"

if errorlevel 1 (
    echo ERROR: pack failed.
    exit /b 1
)

echo DONE.
exit /b 0
