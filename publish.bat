@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

echo ====================================
echo Publish TigerClaw (Release)
echo ====================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "CONFIG_FILE=%ROOT%\publish_config.txt"
set "EMBED_INFO=%ROOT%\BimeTSF2\SampleIME\EmbeddedBuildInfo.h"
set "SHARED_BUILD_INFO=%ROOT%\next\TigerClaw.Shared\BuildInfo.cs"
set "PACK_SCRIPT=%ROOT%\pack_release.bat"
set "SENTENCE_NATIVE_BUILD=%ROOT%\next\build_sentence_native.bat"
set "TEXT_LOG_ENABLED=0"
set "CORE_HASH_VERIFY_ENABLED=1"
set "TRIAL_EXPIRE_UTC="
set "BUILD_VERSION_LABEL="
set "BUILD_COMMIT="
set "BUILD_UTC="
set "DOTNET="
set "TSF_TOOLSET_ARGS=/p:PlatformToolset=v145"
set "DOTNET_CLI_HOME=%TEMP%\TigerClawDotnetHome"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"

if exist "%CONFIG_FILE%" (
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$cfg = '%CONFIG_FILE%'; $v = '0'; if (Test-Path -LiteralPath $cfg) { $line = Get-Content -LiteralPath $cfg -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*text_log_enabled\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1; if ($line) { $vv = ($line -split '=', 2)[1].Trim(); if ($vv -match '^[01]$') { $v = $vv } } }; Write-Output $v"`) do set "TEXT_LOG_ENABLED=%%I"
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$cfg = '%CONFIG_FILE%'; $v = '1'; if (Test-Path -LiteralPath $cfg) { $line = Get-Content -LiteralPath $cfg -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*core_hash_verify_enabled\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1; if ($line) { $vv = ($line -split '=', 2)[1].Trim(); if ($vv -match '^[01]$') { $v = $vv } } }; Write-Output $v"`) do set "CORE_HASH_VERIFY_ENABLED=%%I"
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$cfg = '%CONFIG_FILE%'; $v = ''; if (Test-Path -LiteralPath $cfg) { $line = Get-Content -LiteralPath $cfg -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*trial_expire_utc\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1; if ($line) { $vv = ($line -split '=', 2)[1].Trim(); if ($vv -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { $v = $vv } } }; Write-Output $v"`) do set "TRIAL_EXPIRE_UTC=%%I"
)
if not defined TRIAL_EXPIRE_UTC (
    echo ERROR: Missing or invalid trial_expire_utc in %CONFIG_FILE%
    exit /b 1
)

for /f "delims=" %%I in ('where.exe dotnet.exe 2^>nul') do if not defined DOTNET set "DOTNET=%%~fI"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET (
    echo ERROR: dotnet.exe not found.
    exit /b 1
)
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%" >nul 2>&1

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD="
if exist "%VSWHERE%" call :FindMsbuild "%VSWHERE%"
if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD (
    for /f "delims=" %%I in ('where.exe MSBuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%~fI"
)
if not defined MSBUILD (
    echo ERROR: MSBuild.exe not found.
    exit /b 1
)

set "CORE_PROJECT=%ROOT%\next\TigerClaw.Core\TigerClaw.Core.csproj"
set "OVERLAY_PROJECT=%ROOT%\next\build_overlay.bat"
set "DIALOG_PROJECT=%ROOT%\next\TigerClaw.Dialog\TigerClaw.Dialog.csproj"
set "HOOK_NATIVE_PROJECT=%ROOT%\next\TigerClaw.Hook.Native\TigerClaw.Hook.Native.vcxproj"
set "TSF_PROJECT=%ROOT%\BimeTSF2\SampleIME\BimeTSF2.vcxproj"

if not exist "%CORE_PROJECT%" (
    echo ERROR: Missing project: %CORE_PROJECT%
    exit /b 1
)
if not exist "%OVERLAY_PROJECT%" (
    echo ERROR: Missing project: %OVERLAY_PROJECT%
    exit /b 1
)
if not exist "%DIALOG_PROJECT%" (
    echo ERROR: Missing project: %DIALOG_PROJECT%
    exit /b 1
)
if not exist "%HOOK_NATIVE_PROJECT%" (
    echo ERROR: Missing project: %HOOK_NATIVE_PROJECT%
    exit /b 1
)
if not exist "%TSF_PROJECT%" (
    echo ERROR: Missing project: %TSF_PROJECT%
    exit /b 1
)
if not exist "%PACK_SCRIPT%" (
    echo ERROR: Missing package script: %PACK_SCRIPT%
    exit /b 1
)
if not exist "%SENTENCE_NATIVE_BUILD%" (
    echo ERROR: Missing sentence native build script: %SENTENCE_NATIVE_BUILD%
    exit /b 1
)

set "CORE_OUT=%ROOT%\next\_run\Release\core-x64"
set "CORE_EXE_FOR_HASH=%CORE_OUT%\TigerClaw.Core.exe"
set "OVERLAY_OUT=%ROOT%\next\_run\Release\net48"
set "DIALOG_OUT=%ROOT%\next\_run\Release\net48"
set "SENTENCE_OUT=%ROOT%\next\_run\Release\sentence"
set "SENTENCE_MODEL_ROOT=C:\Archive\tigerclaw_sentence_ml\runtime"
set "SENTENCE_NGRAM_MODEL=%SENTENCE_MODEL_ROOT%\sentence-ngram-v2.bin"
set "SENTENCE_QWEN_MODEL=C:\Archive\tigerclaw_sentence_ml\qwen3-0.6b-gguf\downloaded\Qwen3-0.6B-Base-Q8_0.gguf"
set "SENTENCE_QWEN_LICENSE=C:\Archive\tigerclaw_sentence_ml\qwen3-0.6b-base\LICENSE"
set "HOOK_NATIVE_OUT=%ROOT%\next\_run\Release\native"
set "TSF_X64_DLL=%ROOT%\BimeTSF2\SampleIME\x64\Release\TigerClaw.dll"
set "TSF_X86_DLL=%ROOT%\BimeTSF2\SampleIME\Win32\Release\TigerClaw.dll"
if not exist "%TSF_X86_DLL%" set "TSF_X86_DLL=%ROOT%\BimeTSF2\SampleIME\Release\TigerClaw.dll"

set "RELEASE_DIR=%ROOT%\release"
set "RELEASE_TSF_X64=%RELEASE_DIR%\x64"
set "RELEASE_TSF_X86=%RELEASE_DIR%\Win32"
set "RELEASE_SENTENCE=%RELEASE_DIR%\sentence"
set "RELEASE_MODELS=%RELEASE_DIR%\Models"
set "DIST_INSTALL_TEMPLATE=%ROOT%\dist_install.bat"
set "DIST_UNINSTALL_TEMPLATE=%ROOT%\dist_uninstall.bat"
set "DIST_SELECTION_KEYS_TEMPLATE=%ROOT%\dist_selection_keys.txt"
set "CHANGELOG_FILE="
set "CHANGELOG_NAME="
set "INSTALL_SCRIPT="
set "UNINSTALL_SCRIPT="
set "SELECTION_KEYS_FILE="
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(-join ([char[]](0x66F4,0x65B0,0x65E5,0x5FD7))) + '.txt'"`) do set "CHANGELOG_NAME=%%I"
if defined CHANGELOG_NAME set "CHANGELOG_FILE=%ROOT%\!CHANGELOG_NAME!"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "[char]0x5B89 + [char]0x88C5 + '.bat'"`) do set "INSTALL_SCRIPT=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "[char]0x5378 + [char]0x8F7D + '.bat'"`) do set "UNINSTALL_SCRIPT=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(-join ([char[]](0x81EA,0x5B9A,0x4E49,0x9009,0x91CD,0x952E))) + '.txt'"`) do set "SELECTION_KEYS_FILE=%%I"

echo Using MSBuild:
echo   %MSBUILD%
echo Using dotnet:
echo   %DOTNET%

echo.
echo [1/13] Validate publish config
if not exist "%EMBED_INFO%" (
    echo ERROR: Missing embedded build info header: %EMBED_INFO%
    exit /b 1
)
if not exist "%SHARED_BUILD_INFO%" (
    echo ERROR: Missing shared build info source: %SHARED_BUILD_INFO%
    exit /b 1
)
if not exist "%SENTENCE_NGRAM_MODEL%" (
    echo ERROR: Missing sentence n-gram model: %SENTENCE_NGRAM_MODEL%
    exit /b 1
)
if not exist "%DIST_INSTALL_TEMPLATE%" (
    echo ERROR: Missing distribution install template: %DIST_INSTALL_TEMPLATE%
    exit /b 1
)
if not exist "%DIST_UNINSTALL_TEMPLATE%" (
    echo ERROR: Missing distribution uninstall template: %DIST_UNINSTALL_TEMPLATE%
    exit /b 1
)
if not exist "%DIST_SELECTION_KEYS_TEMPLATE%" (
    echo ERROR: Missing distribution selection-key template: %DIST_SELECTION_KEYS_TEMPLATE%
    exit /b 1
)
echo   text_log_enabled=%TEXT_LOG_ENABLED%
echo   core_hash_verify_enabled=%CORE_HASH_VERIFY_ENABLED%
echo   trial_expire_utc=%TRIAL_EXPIRE_UTC%

echo.
echo [2/13] Update Shared BuildInfo.cs
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')"`) do set "BUILD_UTC=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('yyyy.MM.dd-HHmm')"`) do set "BUILD_VERSION_LABEL=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue'; Set-Location -LiteralPath '%ROOT%'; git rev-parse --short=8 HEAD"`) do if not defined BUILD_COMMIT set "BUILD_COMMIT=%%I"
if not defined BUILD_COMMIT set "BUILD_COMMIT=unknown"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$path = '%SHARED_BUILD_INFO%';" ^
  "$version = '%BUILD_VERSION_LABEL%';" ^
  "$commit = '%BUILD_COMMIT%';" ^
  "$buildUtc = '%BUILD_UTC%';" ^
  "$trialUtc = '%TRIAL_EXPIRE_UTC%';" ^
  "$lines = @(" ^
  "  'namespace TigerClaw.Shared'," ^
  "  '{'," ^
  "  '    public static class BuildInfo'," ^
  "  '    {'," ^
  "  ('        public const string VersionLabel = \"' + $version + '\";')," ^
  "  ('        public const string Commit = \"' + $commit + '\";')," ^
  "  ('        public const string BuildUtc = \"' + $buildUtc + '\";')," ^
  "  ('        public const string TrialExpireUtc = \"' + $trialUtc + '\";')," ^
  "  '    }'," ^
  "  '}'" ^
  ");" ^
  "$raw = [string]::Join([Environment]::NewLine, $lines);" ^
  "Set-Content -LiteralPath $path -Value $raw -Encoding UTF8 -NoNewline;" ^
  "Write-Host ('  version=' + $version);" ^
  "Write-Host ('  commit=' + $commit);" ^
  "Write-Host ('  build_utc=' + $buildUtc);"
if errorlevel 1 (
    echo ERROR: Failed to update BuildInfo.cs
    exit /b 1
)

echo.
echo [3/13] Publish TigerClaw.Core Native AOT Release
"%DOTNET%" publish "%CORE_PROJECT%" -c Release -r win-x64 --self-contained true -o "%CORE_OUT%" /p:PublishAot=true
if errorlevel 1 (
    echo ERROR: TigerClaw.Core Release build failed.
    exit /b 1
)

echo.
echo [4/13] Build TigerClaw.Overlay Release
call "%OVERLAY_PROJECT%" x64 "%OVERLAY_OUT%" Release
if errorlevel 1 (
    echo ERROR: TigerClaw.Overlay Release build failed.
    exit /b 1
)

echo.
echo [5/13] Build TigerClaw.Dialog Release
"%DOTNET%" msbuild /m /nr:false "%DIALOG_PROJECT%" /restore /p:Configuration=Release /p:Platform=AnyCPU /p:OutDir="%DIALOG_OUT%\\" /v:minimal
if errorlevel 1 (
    echo ERROR: TigerClaw.Dialog Release build failed.
    exit /b 1
)

echo.
echo [6/13] Build TigerClaw.Sentence Release
call "%SENTENCE_NATIVE_BUILD%" x64 "%SENTENCE_OUT%" Release
if errorlevel 1 (
    echo ERROR: TigerClaw.Sentence C++ x64 build failed.
    exit /b 1
)

echo.
echo [7/13] Validate sentence n-gram model
call :RequireFile "%SENTENCE_NGRAM_MODEL%" "sentence n-gram model" || exit /b 1

echo.
echo [8/13] Update EmbeddedBuildInfo.h
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$path = '%EMBED_INFO%';" ^
  "$coreExe = '%CORE_EXE_FOR_HASH%';" ^
  "$enabled = '%TEXT_LOG_ENABLED%';" ^
  "$verifyEnabled = '%CORE_HASH_VERIFY_ENABLED%';" ^
  "$trialUtc = '%TRIAL_EXPIRE_UTC%';" ^
  "if (-not (Test-Path -LiteralPath $coreExe)) { throw ('Core exe not found: ' + $coreExe) };" ^
  "if ($verifyEnabled -notmatch '^[01]$') { throw ('Invalid core_hash_verify_enabled: ' + $verifyEnabled) };" ^
  "if ($trialUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') { throw ('Invalid trial_expire_utc: ' + $trialUtc) };" ^
  "$coreHash = (Get-FileHash -LiteralPath $coreExe -Algorithm SHA256).Hash.ToLowerInvariant();" ^
  "if ($coreHash -notmatch '^[0-9a-f]{64}$') { throw ('Invalid core sha256: ' + $coreHash) };" ^
  "$raw = Get-Content -LiteralPath $path -Raw -ErrorAction Stop;" ^
  "if (-not [regex]::IsMatch($raw, '(?m)^#define\s+BIME_EMBED_TEXT_LOG_ENABLED\s+\d+\s*$')) { throw 'Missing BIME_EMBED_TEXT_LOG_ENABLED in EmbeddedBuildInfo.h' };" ^
  "if (-not [regex]::IsMatch($raw, '(?m)^#define\s+BIME_EMBED_VERBOSE_LOG_ENABLED\s+\d+\s*$')) { throw 'Missing BIME_EMBED_VERBOSE_LOG_ENABLED in EmbeddedBuildInfo.h' };" ^
  "if (-not [regex]::IsMatch($raw, '(?m)^#define\s+BIME_EMBED_CORE_HASH_VERIFY_ENABLED\s+\d+\s*$')) { throw 'Missing BIME_EMBED_CORE_HASH_VERIFY_ENABLED in EmbeddedBuildInfo.h' };" ^
  "$getKey = { param([string]$name) $m = [regex]::Match($raw, '(?m)^#define\s+' + [regex]::Escape($name) + '\s+0x([0-9A-Fa-f]{1,2})\s*$'); if (-not $m.Success) { throw ('Missing key: ' + $name) }; [Convert]::ToInt32($m.Groups[1].Value, 16) };" ^
  "$keys = @((& $getKey 'BIME_EMBED_XOR_KEY0'), (& $getKey 'BIME_EMBED_XOR_KEY1'), (& $getKey 'BIME_EMBED_XOR_KEY2'), (& $getKey 'BIME_EMBED_XOR_KEY3'));" ^
  "$encode = { param([string]$text) $src = [System.Text.Encoding]::ASCII.GetBytes($text); $dst = New-Object byte[] $src.Length; for ($i = 0; $i -lt $src.Length; $i++) { $mask = ($keys[$i %% $keys.Count] -bxor (($i * 13 + 0x5A) -band 0xFF)); $dst[$i] = ($src[$i] -bxor $mask) }; return ,$dst };" ^
  "$fmt = { param([byte[]]$arr) (($arr | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', ') };" ^
  "$trialEnc = & $encode $trialUtc;" ^
  "$coreEnc = & $encode $coreHash;" ^
  "$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_TEXT_LOG_ENABLED\s+\d+\s*$', '#define BIME_EMBED_TEXT_LOG_ENABLED ' + $enabled);" ^
  "$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_VERBOSE_LOG_ENABLED\s+\d+\s*$', '#define BIME_EMBED_VERBOSE_LOG_ENABLED ' + $enabled);" ^
  "$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_CORE_HASH_VERIFY_ENABLED\s+\d+\s*$', '#define BIME_EMBED_CORE_HASH_VERIFY_ENABLED ' + $verifyEnabled);" ^
  "$raw = [regex]::Replace($raw, '(?m)^static const unsigned char BIME_EMBED_TRIAL_EXPIRE_UTC_ENC\[\]\s*=\s*\{[^}]*\};\s*$', 'static const unsigned char BIME_EMBED_TRIAL_EXPIRE_UTC_ENC[] = { ' + (& $fmt $trialEnc) + ' };');" ^
  "$raw = [regex]::Replace($raw, '(?m)^static const unsigned int BIME_EMBED_TRIAL_EXPIRE_UTC_LEN\s*=\s*\d+u;\s*$', 'static const unsigned int BIME_EMBED_TRIAL_EXPIRE_UTC_LEN = ' + $trialEnc.Length + 'u;');" ^
  "$raw = [regex]::Replace($raw, '(?m)^static const unsigned char BIME_EMBED_CORE_SHA256_ENC\[\]\s*=\s*\{[^}]*\};\s*$', 'static const unsigned char BIME_EMBED_CORE_SHA256_ENC[] = { ' + (& $fmt $coreEnc) + ' };');" ^
  "$raw = [regex]::Replace($raw, '(?m)^static const unsigned int BIME_EMBED_CORE_SHA256_LEN\s*=\s*\d+u;\s*$', 'static const unsigned int BIME_EMBED_CORE_SHA256_LEN = ' + $coreEnc.Length + 'u;');" ^
  "Set-Content -LiteralPath $path -Value $raw -Encoding Ascii -NoNewline;" ^
  "Write-Host ('  embedded core_hash_verify_enabled=' + $verifyEnabled);" ^
  "Write-Host ('  embedded trial_expire_utc=' + $trialUtc);" ^
  "Write-Host ('  embedded core_sha256=' + $coreHash)"
if errorlevel 1 (
    echo ERROR: Failed to update EmbeddedBuildInfo.h
    exit /b 1
)

echo.
echo [9/13] Build TigerClaw.Hook.Native Release
"%MSBUILD%" /m /nr:false "%HOOK_NATIVE_PROJECT%" /p:Configuration=Release /p:Platform=x64 /p:OutDir="%HOOK_NATIVE_OUT%\\" /v:minimal
if errorlevel 1 (
    echo ERROR: TigerClaw.Hook.Native Release build failed.
    exit /b 1
)

echo.
echo [10/13] Build TigerClaw TSF x64/Win32 Release
call :BuildTsfRelease x64
if errorlevel 1 (
    echo ERROR: TigerClaw TSF x64 Release build failed.
    exit /b 1
)
call :BuildTsfRelease Win32
if errorlevel 1 (
    echo ERROR: TigerClaw TSF Win32 Release build failed.
    exit /b 1
)

echo.
echo [11/13] Copy artifacts to release directory
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%"
if not exist "%RELEASE_TSF_X64%" mkdir "%RELEASE_TSF_X64%"
if not exist "%RELEASE_TSF_X86%" mkdir "%RELEASE_TSF_X86%"
if not exist "%RELEASE_SENTENCE%" mkdir "%RELEASE_SENTENCE%"
if not exist "%RELEASE_SENTENCE%\Models" mkdir "%RELEASE_SENTENCE%\Models"
if not exist "%RELEASE_SENTENCE%\licenses" mkdir "%RELEASE_SENTENCE%\licenses"
if not exist "%RELEASE_MODELS%" mkdir "%RELEASE_MODELS%"

call :RequireFile "%CORE_OUT%\TigerClaw.Core.exe" "TigerClaw.Core.exe" || exit /b 1
call :RequireFile "%OVERLAY_OUT%\TigerClaw.Overlay.exe" "TigerClaw.Overlay.exe" || exit /b 1
for %%F in (Overlay-THIRD-PARTY-NOTICES.txt sounds\KeyNormal.wav sounds\KeySpace.wav sounds\KeyFunc.wav) do call :RequireFile "%OVERLAY_OUT%\%%F" "%%F" || exit /b 1
call :RequireFile "%DIALOG_OUT%\TigerClaw.Dialog.exe" "TigerClaw.Dialog.exe" || exit /b 1
call :RequireFile "%SENTENCE_OUT%\TigerClaw.Sentence.exe" "TigerClaw.Sentence.exe" || exit /b 1
call :RequireFile "%SENTENCE_NGRAM_MODEL%" "sentence n-gram model" || exit /b 1
call :RequireFile "%SENTENCE_QWEN_MODEL%" "Qwen Q8 model" || exit /b 1
call :RequireFile "%ROOT%\third_party\llama.cpp\LICENSE" "llama.cpp license" || exit /b 1
call :RequireFile "%SENTENCE_QWEN_LICENSE%" "Qwen license" || exit /b 1
call :RequireFile "%HOOK_NATIVE_OUT%\TigerClaw.Hook.Native.exe" "TigerClaw.Hook.Native.exe" || exit /b 1
call :RequireFile "%TSF_X64_DLL%" "TigerClaw.dll x64" || exit /b 1
call :RequireFile "%TSF_X86_DLL%" "TigerClaw.dll Win32" || exit /b 1

call :CopyFileStrict "%CORE_OUT%\TigerClaw.Core.exe" "%RELEASE_DIR%\TigerClaw.Core.exe" || exit /b 1
if exist "%RELEASE_DIR%\TigerClaw.Core.exe.config" del /q "%RELEASE_DIR%\TigerClaw.Core.exe.config"
if exist "%CORE_OUT%\TigerClaw.Core.pdb" del /q "%RELEASE_DIR%\TigerClaw.Core.pdb" >nul 2>&1

call :CopyFileStrict "%OVERLAY_OUT%\TigerClaw.Overlay.exe" "%RELEASE_DIR%\TigerClaw.Overlay.exe" || exit /b 1
if not exist "%OVERLAY_OUT%\TigerClaw.Overlay.exe.config" if exist "%RELEASE_DIR%\TigerClaw.Overlay.exe.config" del /q "%RELEASE_DIR%\TigerClaw.Overlay.exe.config"
if not exist "%RELEASE_DIR%\sounds" mkdir "%RELEASE_DIR%\sounds"
for %%F in (Overlay-THIRD-PARTY-NOTICES.txt sounds\KeyNormal.wav sounds\KeySpace.wav sounds\KeyFunc.wav) do call :CopyFileStrict "%OVERLAY_OUT%\%%F" "%RELEASE_DIR%\%%F" || exit /b 1
if exist "%OVERLAY_OUT%\TigerClaw.Overlay.exe.config" call :CopyFileStrict "%OVERLAY_OUT%\TigerClaw.Overlay.exe.config" "%RELEASE_DIR%\TigerClaw.Overlay.exe.config" || exit /b 1
if exist "%OVERLAY_OUT%\TigerClaw.Overlay.pdb" del /q "%RELEASE_DIR%\TigerClaw.Overlay.pdb" >nul 2>&1

call :CopyFileStrict "%DIALOG_OUT%\TigerClaw.Dialog.exe" "%RELEASE_DIR%\TigerClaw.Dialog.exe" || exit /b 1
if exist "%DIALOG_OUT%\TigerClaw.Dialog.exe.config" call :CopyFileStrict "%DIALOG_OUT%\TigerClaw.Dialog.exe.config" "%RELEASE_DIR%\TigerClaw.Dialog.exe.config" || exit /b 1
if exist "%DIALOG_OUT%\TigerClaw.Dialog.pdb" del /q "%RELEASE_DIR%\TigerClaw.Dialog.pdb" >nul 2>&1

call :CopyFileStrict "%SENTENCE_OUT%\TigerClaw.Sentence.exe" "%RELEASE_SENTENCE%\TigerClaw.Sentence.exe" || exit /b 1
for %%F in (TigerClaw.Sentence.exe.config TigerClaw.Shared.dll TigerClaw.Sentence.Native.dll) do if exist "%RELEASE_SENTENCE%\%%F" del /q "%RELEASE_SENTENCE%\%%F"
for %%F in (Microsoft.ML.OnnxRuntime.dll System.Buffers.dll System.Memory.dll System.Numerics.Tensors.dll System.Numerics.Vectors.dll System.Runtime.CompilerServices.Unsafe.dll onnxruntime.dll onnxruntime_providers_shared.dll) do (
    if exist "%RELEASE_SENTENCE%\%%F" del /q "%RELEASE_SENTENCE%\%%F"
)
if exist "%RELEASE_MODELS%\sentence-ngram.bin" del /q "%RELEASE_MODELS%\sentence-ngram.bin"
if exist "%RELEASE_MODELS%\sentence-ngram.tcmodel" del /q "%RELEASE_MODELS%\sentence-ngram.tcmodel"
if exist "%RELEASE_MODELS%\sentence-ngram-v2.bin" del /q "%RELEASE_MODELS%\sentence-ngram-v2.bin"
if exist "%RELEASE_MODELS%\sentence-ngram-v2.tcmodel" del /q "%RELEASE_MODELS%\sentence-ngram-v2.tcmodel"
if exist "%RELEASE_SENTENCE%\Models\sentence-transformer.onnx" del /q "%RELEASE_SENTENCE%\Models\sentence-transformer.onnx"
if exist "%RELEASE_SENTENCE%\Models\sentence-vocabulary.json" del /q "%RELEASE_SENTENCE%\Models\sentence-vocabulary.json"
if exist "%RELEASE_SENTENCE%\Models\sentence-transformer.tcmodel" del /q "%RELEASE_SENTENCE%\Models\sentence-transformer.tcmodel"
if exist "%RELEASE_SENTENCE%\Models\sentence-vocabulary.tcmodel" del /q "%RELEASE_SENTENCE%\Models\sentence-vocabulary.tcmodel"
if exist "%RELEASE_SENTENCE%\Models\sentence-transformer.json" del /q "%RELEASE_SENTENCE%\Models\sentence-transformer.json"
call :CopyFileStrict "%SENTENCE_QWEN_MODEL%" "%RELEASE_SENTENCE%\Models\sentence-qwen-q8.gguf" || exit /b 1
call :CopyFileStrict "%SENTENCE_NGRAM_MODEL%" "%RELEASE_MODELS%\sentence-ngram-v2.bin" || exit /b 1
call :CopyFileStrict "%ROOT%\third_party\llama.cpp\LICENSE" "%RELEASE_SENTENCE%\licenses\llama.cpp-LICENSE.txt" || exit /b 1
call :CopyFileStrict "%SENTENCE_QWEN_LICENSE%" "%RELEASE_SENTENCE%\licenses\Qwen3-LICENSE.txt" || exit /b 1

call :CopyFileStrict "%HOOK_NATIVE_OUT%\TigerClaw.Hook.Native.exe" "%RELEASE_DIR%\TigerClaw.exe" || exit /b 1
if exist "%HOOK_NATIVE_OUT%\TigerClaw.Hook.Native.pdb" del /q "%RELEASE_DIR%\TigerClaw.pdb" >nul 2>&1

if exist "%ROOT%\next\TigerClaw.Dialog\bime.ico" call :CopyFileStrict "%ROOT%\next\TigerClaw.Dialog\bime.ico" "%RELEASE_DIR%\bime.ico" || exit /b 1
call :CopyFileStrict "%DIALOG_OUT%\TigerClaw.Shared.dll" "%RELEASE_DIR%\TigerClaw.Shared.dll" || exit /b 1
call :CopyFileStrict "%TSF_X64_DLL%" "%RELEASE_TSF_X64%\TigerClaw.dll" || exit /b 1
call :CopyFileStrict "%TSF_X86_DLL%" "%RELEASE_TSF_X86%\TigerClaw.dll" || exit /b 1
if defined CHANGELOG_NAME if exist "%CHANGELOG_FILE%" call :CopyFileStrict "%CHANGELOG_FILE%" "%RELEASE_DIR%\!CHANGELOG_NAME!" || exit /b 1

call :CopyFileStrict "%DIST_INSTALL_TEMPLATE%" "%RELEASE_DIR%\!INSTALL_SCRIPT!" || exit /b 1
call :CopyFileStrict "%DIST_UNINSTALL_TEMPLATE%" "%RELEASE_DIR%\!UNINSTALL_SCRIPT!" || exit /b 1
call :CopyFileStrict "%DIST_SELECTION_KEYS_TEMPLATE%" "%RELEASE_DIR%\!SELECTION_KEYS_FILE!" || exit /b 1
if exist "%RELEASE_DIR%\install.bat" del /q "%RELEASE_DIR%\install.bat" >nul 2>&1
if exist "%RELEASE_DIR%\uninstall.bat" del /q "%RELEASE_DIR%\uninstall.bat" >nul 2>&1

echo.
echo [12/13] Package release archive
call "%PACK_SCRIPT%"
if errorlevel 1 (
    echo ERROR: Release packaging failed.
    exit /b 1
)

echo.
echo [13/13] Done
echo Publish succeeded.
echo   Core    : %RELEASE_DIR%\TigerClaw.Core.exe
echo   Overlay : %RELEASE_DIR%\TigerClaw.Overlay.exe
echo   Dialog  : %RELEASE_DIR%\TigerClaw.Dialog.exe
echo   Sentence: %RELEASE_SENTENCE%\TigerClaw.Sentence.exe
echo   Hook.N  : %RELEASE_DIR%\TigerClaw.exe
echo   TSF x64 : %RELEASE_DIR%\x64\TigerClaw.dll
echo   TSF x86 : %RELEASE_DIR%\Win32\TigerClaw.dll
exit /b 0

:RequireFile
if exist "%~1" exit /b 0
echo ERROR: Missing %~2: %~1
exit /b 1

:CopyFileStrict
copy /Y "%~1" "%~2" >nul
if errorlevel 1 (
    echo ERROR: Copy failed: %~1
    echo        Destination: %~2
    echo        Hint: target file may be in use. Please close TigerClaw and host apps, then retry.
    tasklist /FI "IMAGENAME eq TigerClaw.Core.exe" 2>nul | find /I "TigerClaw.Core.exe" >nul && echo        Running: TigerClaw.Core.exe
    tasklist /FI "IMAGENAME eq TigerClaw.Overlay.exe" 2>nul | find /I "TigerClaw.Overlay.exe" >nul && echo        Running: TigerClaw.Overlay.exe
    tasklist /FI "IMAGENAME eq TigerClaw.Dialog.exe" 2>nul | find /I "TigerClaw.Dialog.exe" >nul && echo        Running: TigerClaw.Dialog.exe
    tasklist /FI "IMAGENAME eq TigerClaw.Hook.Native.exe" 2>nul | find /I "TigerClaw.Hook.Native.exe" >nul && echo        Running: TigerClaw.Hook.Native.exe
    tasklist /FI "IMAGENAME eq TigerClaw.exe" 2>nul | find /I "TigerClaw.exe" >nul && echo        Running: TigerClaw.exe
    tasklist /m TigerClaw.dll 2>nul
    exit /b 1
)
exit /b 0

:FindMsbuild
set "VSWHERE_PATH=%~1"
for /f "usebackq delims=" %%I in (`"%VSWHERE_PATH%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set "MSBUILD=%%~fI"
exit /b 0

:BuildTsfRelease
set "TSF_PLATFORM=%~1"
if "%TSF_PLATFORM%"=="" exit /b 1

if defined TSF_TOOLSET_ARGS (
    echo   TSF platform=%TSF_PLATFORM% toolset=%TSF_TOOLSET_ARGS%
)

"%MSBUILD%" /m /nr:false "%TSF_PROJECT%" /p:Configuration=Release /p:Platform=%TSF_PLATFORM% /p:WholeProgramOptimization=false /v:minimal %TSF_TOOLSET_ARGS%
if errorlevel 1 exit /b 1
exit /b 0
