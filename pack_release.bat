@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "RELEASE_DIR=%ROOT%\release"
set "STAGE_DIR=%RELEASE_DIR%\TigerClaw"
set "DIST_CONFIG=%ROOT%\dist_config.txt"
set "SEVEN_Z=%RELEASE_DIR%\7z.exe"
set "SENTENCE_LEXICON_SOURCE=%ROOT%\release_arm64"

if not exist "%SEVEN_Z%" (
    echo ERROR: 7z.exe not found: %SEVEN_Z%
    exit /b 1
)
if not exist "%DIST_CONFIG%" (
    echo ERROR: Distribution config not found: %DIST_CONFIG%
    exit /b 1
)

set "PACK_VERSION_LABEL=%~1"
set "PACK_BUILD_INFO=%ROOT%\next\TigerClaw.Shared\BuildInfo.cs"
set "PACK_RELEASE_DIR=%RELEASE_DIR%"
set "PACK_STAGE_DIR=%STAGE_DIR%"
set "PACK_DIST_CONFIG=%DIST_CONFIG%"
set "PACK_SEVEN_Z=%SEVEN_Z%"
set "PACK_SENTENCE_LEXICON_SOURCE=%SENTENCE_LEXICON_SOURCE%"

echo ====================================
echo Pack TigerClaw Release
echo ====================================
echo ReleaseDir: %PACK_RELEASE_DIR%

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$release=$env:PACK_RELEASE_DIR;" ^
  "$stage=$env:PACK_STAGE_DIR;" ^
  "$distConfig=$env:PACK_DIST_CONFIG;" ^
  "$sevenZ=$env:PACK_SEVEN_Z;" ^
  "$armRelease=$env:PACK_SENTENCE_LEXICON_SOURCE;" ^
  "$version = $env:PACK_VERSION_LABEL;" ^
  "if([string]::IsNullOrWhiteSpace($version)){ $q=[char]34; $info=Get-Content -LiteralPath $env:PACK_BUILD_INFO -Raw; $match=[regex]::Match($info,('VersionLabel\s*=\s*'+$q+'([^'+$q+']+)'+$q)); if($match.Success){ $version=$match.Groups[1].Value } };" ^
  "if([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*$'){ throw 'Missing or invalid package version' };" ^
  "$imeName = -join ([char[]](0x864E,0x722A,0x8F93,0x5165,0x6CD5));" ^
  "$installBat = (-join ([char[]](0x5B89,0x88C5))) + '.bat';" ^
  "$uninstallBat = (-join ([char[]](0x5378,0x8F7D))) + '.bat';" ^
  "$changelog = (-join ([char[]](0x66F4,0x65B0,0x65E5,0x5FD7))) + '.txt';" ^
  "$fontDir = -join ([char[]](0x5B57,0x4F53));" ^
  "$reverseDir = -join ([char[]](0x62FC,0x97F3,0x53CD,0x67E5,0x7801,0x8868));" ^
  "$lexiconDir = -join ([char[]](0x7801,0x8868));" ^
  "$selectionKeys = (-join ([char[]](0x81EA,0x5B9A,0x4E49,0x9009,0x91CD,0x952E))) + '.txt';" ^
  "$sentenceSchema = -join ([char[]](0x864E,0x6574,0x53E5));" ^
  "$sentenceLexicon = $sentenceSchema + '.txt';" ^
  "$releaseSentenceLexicon = Join-Path (Join-Path (Join-Path $release $lexiconDir) $sentenceSchema) $sentenceLexicon;" ^
  "$armSentenceLexicon = Join-Path (Join-Path (Join-Path $armRelease $lexiconDir) $sentenceSchema) $sentenceLexicon;" ^
  "if(-not (Test-Path -LiteralPath $armSentenceLexicon)){ throw ('Missing sentence lexicon: {0}' -f $armSentenceLexicon) };" ^
  "New-Item -ItemType Directory -Path (Split-Path -Parent $releaseSentenceLexicon) -Force | Out-Null;" ^
  "Copy-Item -LiteralPath $armSentenceLexicon -Destination $releaseSentenceLexicon -Force;" ^
  "$items=@('TigerClaw.Core.exe','TigerClaw.Overlay.exe','Overlay-THIRD-PARTY-NOTICES.txt','TigerClaw.Dialog.exe','TigerClaw.Dialog.exe.config','TigerClaw.exe','TigerClaw.Shared.dll','bime.ico',$changelog,$installBat,$uninstallBat,'x64','Win32','Models','sentence','sounds',$fontDir,$reverseDir,$lexiconDir,$selectionKeys);" ^
  "if(Test-Path -LiteralPath (Join-Path $release 'TigerClaw.Overlay.exe.config')){ $items += 'TigerClaw.Overlay.exe.config' };" ^
  "foreach($item in $items){ $src=Join-Path $release $item; if(-not (Test-Path -LiteralPath $src)){ throw ('Missing required item: {0}' -f $src) } };" ^
  "if(Test-Path -LiteralPath $stage){ Remove-Item -LiteralPath $stage -Recurse -Force };" ^
  "New-Item -Path $stage -ItemType Directory -Force | Out-Null;" ^
  "foreach($item in $items){ Copy-Item -LiteralPath (Join-Path $release $item) -Destination $stage -Recurse -Force };" ^
  "Copy-Item -LiteralPath $distConfig -Destination (Join-Path $stage 'config.txt') -Force;" ^
  "$fileName = '{0}-{1}.7z' -f $imeName,$version;" ^
  "$archivePath = Join-Path $release $fileName;" ^
  "if(Test-Path -LiteralPath $archivePath){ Remove-Item -LiteralPath $archivePath -Force };" ^
  "Push-Location $release;" ^
  "try { & $sevenZ a -t7z -mx=9 -mmt=on -- $archivePath '.\TigerClaw'; if($LASTEXITCODE -ne 0){ throw ('7z failed, exit=' + $LASTEXITCODE) } } finally { Pop-Location };" ^
  "Write-Host ('Pack done: {0}' -f $archivePath);"

if errorlevel 1 (
    echo ERROR: pack failed.
    exit /b 1
)

echo DONE.
exit /b 0
