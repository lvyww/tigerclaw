@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

echo ====================================
echo Publish TigerClaw (Windows on Arm64)
echo ====================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "CONFIG_FILE=%ROOT%\publish_config.txt"
set "EMBED_INFO=%ROOT%\BimeTSF2\SampleIME\EmbeddedBuildInfo.h"
set "SHARED_BUILD_INFO=%ROOT%\next\TigerClaw.Shared\BuildInfo.cs"
set "TEXT_LOG_ENABLED=0"
set "CORE_HASH_VERIFY_ENABLED=1"
set "TRIAL_EXPIRE_UTC="
set "ARM64_DIAGNOSTIC=0"
if /I "%~1"=="--diagnostic" set "ARM64_DIAGNOSTIC=1"
set "TSF_TOOLSET_ARGS=/p:PlatformToolset=v145"
set "DOTNET_CLI_HOME=%TEMP%\TigerClawDotnetHome"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"

if exist "%CONFIG_FILE%" (
  for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$cfg='%CONFIG_FILE%';$v='0';$line=Get-Content -LiteralPath $cfg -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*text_log_enabled\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1;if($line){$vv=($line -split '=',2)[1].Trim();if($vv -match '^[01]$'){$v=$vv}};$v"`) do set "TEXT_LOG_ENABLED=%%I"
  for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$cfg='%CONFIG_FILE%';$v='1';$line=Get-Content -LiteralPath $cfg -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*core_hash_verify_enabled\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1;if($line){$vv=($line -split '=',2)[1].Trim();if($vv -match '^[01]$'){$v=$vv}};$v"`) do set "CORE_HASH_VERIFY_ENABLED=%%I"
  for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$cfg='%CONFIG_FILE%';$v='';$line=Get-Content -LiteralPath $cfg -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\s*trial_expire_utc\s*=' -and $_ -notmatch '^\s*#' } | Select-Object -First 1;if($line){$vv=($line -split '=',2)[1].Trim();if($vv -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$'){$v=$vv}};$v"`) do set "TRIAL_EXPIRE_UTC=%%I"
)
if not defined TRIAL_EXPIRE_UTC (
  echo ERROR: Missing or invalid trial_expire_utc in %CONFIG_FILE%
  exit /b 1
)
if "%ARM64_DIAGNOSTIC%"=="1" set "TEXT_LOG_ENABLED=1"

set "DOTNET="
for /f "delims=" %%I in ('where.exe dotnet.exe 2^>nul') do if not defined DOTNET set "DOTNET=%%~fI"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET echo ERROR: dotnet.exe not found. & exit /b 1
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%" >nul 2>&1

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD="
if exist "%VSWHERE%" call :FindMsbuild "%VSWHERE%"
if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD for /f "delims=" %%I in ('where.exe MSBuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%~fI"
if not defined MSBUILD echo ERROR: MSBuild.exe not found. & exit /b 1

set "CORE_PROJECT=%ROOT%\next\TigerClaw.Core\TigerClaw.Core.csproj"
set "OVERLAY_PROJECT=%ROOT%\next\build_overlay.bat"
set "DIALOG_PROJECT=%ROOT%\next\TigerClaw.Dialog\TigerClaw.Dialog.csproj"
set "SENTENCE_NATIVE_BUILD=%ROOT%\next\build_sentence_native.bat"
set "HOOK_PROJECT=%ROOT%\next\TigerClaw.Hook.Native\TigerClaw.Hook.Native.vcxproj"
set "TSF_PROJECT=%ROOT%\BimeTSF2\SampleIME\BimeTSF2.vcxproj"
set "TSF_SERVER_PROJECT=%ROOT%\BimeTSF2\SampleIME\TigerClaw.TsfServer.vcxproj"
set "WRAPPER_DIR=%ROOT%\BimeTSF2\SampleIME\arm64x_wrapper"
set "CORE_OUT=%ROOT%\next\_run\ReleaseArm64\core-arm64"
set "UI_OUT=%ROOT%\next\_run\ReleaseArm64\net481"
set "SENTENCE_OUT=%ROOT%\next\_run\ReleaseArm64\sentence"
set "SENTENCE_MODEL_ROOT=C:\Archive\tigerclaw_sentence_ml\runtime"
set "SENTENCE_QWEN_MODEL=C:\Archive\tigerclaw_sentence_ml\qwen3-0.6b-gguf\downloaded\Qwen3-0.6B-Base-Q8_0.gguf"
set "SENTENCE_QWEN_LICENSE=C:\Archive\tigerclaw_sentence_ml\qwen3-0.6b-base\LICENSE"
set "HOOK_OUT=%ROOT%\next\_run\ReleaseArm64\native\ARM64"
set "RELEASE_DIR=%ROOT%\release_arm64"
set "RELEASE_WIN32_DIR=%RELEASE_DIR%\Win32"
set "TSF_X86=%ROOT%\BimeTSF2\SampleIME\Win32\Release\TigerClaw.dll"
if not exist "%TSF_X86%" set "TSF_X86=%ROOT%\BimeTSF2\SampleIME\Release\TigerClaw.dll"
set "TSF_X64=%ROOT%\BimeTSF2\SampleIME\x64\Release\TigerClaw.dll"
set "TSF_ARM64=%ROOT%\BimeTSF2\SampleIME\ARM64\Release\TigerClaw.dll"
set "TSF_SERVER=%ROOT%\BimeTSF2\SampleIME\ARM64\Release\TigerClaw.TsfServer.exe"
set "WRAPPER_DLL=%WRAPPER_DIR%\TigerClaw.dll"
set "INSTALL_TEMPLATE=%ROOT%\dist_arm64_install.bat"
set "INSTALL_DIRECT_TEMPLATE=%ROOT%\dist_arm64_install_direct.bat"
set "INSTALL_ARM64X_TEMPLATE=%ROOT%\dist_arm64_install_arm64x.bat"
set "INSTALL_LOCALSERVER_TEMPLATE=%ROOT%\dist_arm64_install_localserver.bat"
set "DIAGNOSE_TEMPLATE=%ROOT%\dist_arm64_diagnose.bat"
set "UNINSTALL_TEMPLATE=%ROOT%\dist_arm64_uninstall.bat"
set "UPDATE_EMBED_SCRIPT=%ROOT%\tools\update_embedded_build_info.ps1"

for %%P in ("%CORE_PROJECT%" "%OVERLAY_PROJECT%" "%DIALOG_PROJECT%" "%SENTENCE_NATIVE_BUILD%" "%HOOK_PROJECT%" "%TSF_PROJECT%" "%TSF_SERVER_PROJECT%" "%WRAPPER_DIR%\build.bat" "%INSTALL_TEMPLATE%" "%INSTALL_DIRECT_TEMPLATE%" "%INSTALL_ARM64X_TEMPLATE%" "%INSTALL_LOCALSERVER_TEMPLATE%" "%DIAGNOSE_TEMPLATE%" "%UNINSTALL_TEMPLATE%" "%UPDATE_EMBED_SCRIPT%") do if not exist %%~P echo ERROR: Missing %%~P & exit /b 1

echo Using MSBuild: %MSBUILD%
echo Using dotnet : %DOTNET%
echo Diagnostic : %ARM64_DIAGNOSTIC%
echo TSF log    : %TEXT_LOG_ENABLED%

echo.
echo [1/10] Update Shared BuildInfo.cs
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')"`) do set "BUILD_UTC=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-Date).ToUniversalTime().ToString('yyyy.MM.dd-HHmm')"`) do set "BUILD_VERSION=%%I"
for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='SilentlyContinue';Set-Location -LiteralPath '%ROOT%';git rev-parse --short=8 HEAD"`) do if not defined BUILD_COMMIT set "BUILD_COMMIT=%%I"
if not defined BUILD_COMMIT set "BUILD_COMMIT=unknown"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p='%SHARED_BUILD_INFO%';$q=[char]34;$lines=@('namespace TigerClaw.Shared','{','    public static class BuildInfo','    {',('        public const string VersionLabel = '+$q+'%BUILD_VERSION%'+$q+';'),('        public const string Commit = '+$q+'%BUILD_COMMIT%'+$q+';'),('        public const string BuildUtc = '+$q+'%BUILD_UTC%'+$q+';'),('        public const string TrialExpireUtc = '+$q+'%TRIAL_EXPIRE_UTC%'+$q+';'),'    }','}');[IO.File]::WriteAllText($p,[string]::Join([Environment]::NewLine,$lines),(New-Object Text.UTF8Encoding($true)))" || exit /b 1

echo.
echo [2/10] Build .NET payload
"%DOTNET%" publish "%CORE_PROJECT%" -c Release -r win-arm64 --self-contained true -o "%CORE_OUT%" /p:PublishAot=true || exit /b 1
call "%OVERLAY_PROJECT%" ARM64 "%UI_OUT%" Release || exit /b 1
"%DOTNET%" msbuild /m /nr:false "%DIALOG_PROJECT%" /restore /p:Configuration=Release /p:Platform=AnyCPU /p:TigerClawTargetFramework=net481 /p:PlatformTarget=ARM64 /p:Prefer32Bit=false /p:OutDir="%UI_OUT%\\" /v:minimal || exit /b 1
call "%SENTENCE_NATIVE_BUILD%" ARM64 "%SENTENCE_OUT%" Release || exit /b 1
if not exist "%CORE_OUT%\Models" mkdir "%CORE_OUT%\Models"
if not exist "%SENTENCE_OUT%\Models" mkdir "%SENTENCE_OUT%\Models"
if exist "%CORE_OUT%\Models\sentence-ngram.bin" del /q "%CORE_OUT%\Models\sentence-ngram.bin"
if exist "%CORE_OUT%\Models\sentence-ngram.tcmodel" del /q "%CORE_OUT%\Models\sentence-ngram.tcmodel"
if exist "%CORE_OUT%\Models\sentence-ngram-v2.tcmodel" del /q "%CORE_OUT%\Models\sentence-ngram-v2.tcmodel"
copy /Y "%SENTENCE_MODEL_ROOT%\sentence-ngram-v2.bin" "%CORE_OUT%\Models\sentence-ngram-v2.bin" >nul || exit /b 1
copy /Y "%SENTENCE_QWEN_MODEL%" "%SENTENCE_OUT%\Models\sentence-qwen-q8.gguf" >nul || exit /b 1

echo.
echo [3/10] Update EmbeddedBuildInfo.h
powershell -NoProfile -ExecutionPolicy Bypass -File "%UPDATE_EMBED_SCRIPT%" -Path "%EMBED_INFO%" -CoreExe "%CORE_OUT%\TigerClaw.Core.exe" -TextLogEnabled "%TEXT_LOG_ENABLED%" -CoreHashVerifyEnabled "%CORE_HASH_VERIFY_ENABLED%" -TrialExpireUtc "%TRIAL_EXPIRE_UTC%" || exit /b 1

echo.
echo [4/10] Build Hook.Native ARM64
"%MSBUILD%" /m /nr:false "%HOOK_PROJECT%" /p:Configuration=Release /p:Platform=ARM64 /p:OutDir="%HOOK_OUT%\\" /v:minimal || exit /b 1

echo.
echo [5/10] Build TSF Win32/x64/ARM64
call :BuildTsf Win32 || exit /b 1
call :BuildTsf x64 || exit /b 1
call :BuildTsf ARM64 || exit /b 1

echo.
echo [6/10] Build TSF LocalServer ARM64
"%MSBUILD%" /m /nr:false "%TSF_SERVER_PROJECT%" /p:Configuration=Release /p:Platform=ARM64 /p:WholeProgramOptimization=false /v:minimal %TSF_TOOLSET_ARGS% || exit /b 1

echo.
echo [7/10] Build ARM64X wrapper
pushd "%WRAPPER_DIR%" || exit /b 1
call build.bat
set "WRAP_EC=%errorlevel%"
popd
if not "%WRAP_EC%"=="0" exit /b %WRAP_EC%

echo.
echo [8/10] Validate artifacts
for %%P in ("%CORE_OUT%\TigerClaw.Core.exe" "%UI_OUT%\TigerClaw.Overlay.exe" "%UI_OUT%\TigerClaw.Dialog.exe" "%UI_OUT%\TigerClaw.Shared.dll" "%CORE_OUT%\Models\sentence-ngram-v2.bin" "%SENTENCE_OUT%\TigerClaw.Sentence.exe" "%SENTENCE_OUT%\Models\sentence-qwen-q8.gguf" "%SENTENCE_QWEN_LICENSE%" "%ROOT%\third_party\llama.cpp\LICENSE" "%HOOK_OUT%\TigerClaw.Hook.Native.exe" "%TSF_X86%" "%TSF_X64%" "%TSF_ARM64%" "%TSF_SERVER%" "%WRAPPER_DLL%") do if not exist %%~P echo ERROR: Missing %%~P & exit /b 1

echo.
for %%F in (Overlay-THIRD-PARTY-NOTICES.txt sounds\KeyNormal.wav sounds\KeySpace.wav sounds\KeyFunc.wav) do if not exist "%UI_OUT%\%%F" (
  echo ERROR: Missing overlay payload %%F
  exit /b 1
)
echo [9/10] Copy artifacts
taskkill /F /IM TigerClaw.Sentence.exe /T >nul 2>&1
taskkill /F /IM TigerClaw.Overlay.exe /T >nul 2>&1
taskkill /F /IM TigerClaw.Dialog.exe /T >nul 2>&1
taskkill /F /IM TigerClaw.Core.exe /T >nul 2>&1
ping 127.0.0.1 -n 3 >nul
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%"
if not exist "%RELEASE_WIN32_DIR%" mkdir "%RELEASE_WIN32_DIR%"
if not exist "%RELEASE_DIR%\sentence" mkdir "%RELEASE_DIR%\sentence"
if not exist "%RELEASE_DIR%\sentence\Models" mkdir "%RELEASE_DIR%\sentence\Models"
if not exist "%RELEASE_DIR%\sentence\licenses" mkdir "%RELEASE_DIR%\sentence\licenses"
if not exist "%RELEASE_DIR%\Models" mkdir "%RELEASE_DIR%\Models"
if exist "%RELEASE_DIR%\Models\sentence-ngram.bin" del /q "%RELEASE_DIR%\Models\sentence-ngram.bin"
if exist "%RELEASE_DIR%\Models\sentence-ngram.tcmodel" del /q "%RELEASE_DIR%\Models\sentence-ngram.tcmodel"
copy /Y "%CORE_OUT%\TigerClaw.Core.exe" "%RELEASE_DIR%\TigerClaw.Core.exe" >nul || exit /b 1
if exist "%RELEASE_DIR%\TigerClaw.Core.exe.config" del /q "%RELEASE_DIR%\TigerClaw.Core.exe.config"
copy /Y "%UI_OUT%\TigerClaw.Overlay.exe" "%RELEASE_DIR%\TigerClaw.Overlay.exe" >nul || exit /b 1
if not exist "%UI_OUT%\TigerClaw.Overlay.exe.config" if exist "%RELEASE_DIR%\TigerClaw.Overlay.exe.config" del /q "%RELEASE_DIR%\TigerClaw.Overlay.exe.config"
if not exist "%RELEASE_DIR%\sounds" mkdir "%RELEASE_DIR%\sounds"
for %%F in (Overlay-THIRD-PARTY-NOTICES.txt sounds\KeyNormal.wav sounds\KeySpace.wav sounds\KeyFunc.wav) do copy /Y "%UI_OUT%\%%F" "%RELEASE_DIR%\%%F" >nul || exit /b 1
if exist "%UI_OUT%\TigerClaw.Overlay.exe.config" copy /Y "%UI_OUT%\TigerClaw.Overlay.exe.config" "%RELEASE_DIR%\TigerClaw.Overlay.exe.config" >nul || exit /b 1
copy /Y "%UI_OUT%\TigerClaw.Dialog.exe" "%RELEASE_DIR%\TigerClaw.Dialog.exe" >nul || exit /b 1
if exist "%UI_OUT%\TigerClaw.Dialog.exe.config" copy /Y "%UI_OUT%\TigerClaw.Dialog.exe.config" "%RELEASE_DIR%\TigerClaw.Dialog.exe.config" >nul || exit /b 1
copy /Y "%UI_OUT%\TigerClaw.Shared.dll" "%RELEASE_DIR%\TigerClaw.Shared.dll" >nul || exit /b 1
copy /Y "%SENTENCE_OUT%\TigerClaw.Sentence.exe" "%RELEASE_DIR%\sentence\TigerClaw.Sentence.exe" >nul || exit /b 1
for %%F in (TigerClaw.Sentence.exe.config TigerClaw.Shared.dll TigerClaw.Sentence.Native.dll) do if exist "%RELEASE_DIR%\sentence\%%F" del /q "%RELEASE_DIR%\sentence\%%F"
for %%F in (Microsoft.ML.OnnxRuntime.dll System.Buffers.dll System.Memory.dll System.Numerics.Tensors.dll System.Numerics.Vectors.dll System.Runtime.CompilerServices.Unsafe.dll onnxruntime.dll onnxruntime_providers_shared.dll) do if exist "%RELEASE_DIR%\sentence\%%F" del /q "%RELEASE_DIR%\sentence\%%F"
for %%F in (sentence-transformer.onnx sentence-transformer.json sentence-transformer.tcmodel sentence-vocabulary.json sentence-vocabulary.tcmodel) do if exist "%RELEASE_DIR%\sentence\Models\%%F" del /q "%RELEASE_DIR%\sentence\Models\%%F"
copy /Y "%SENTENCE_OUT%\Models\sentence-qwen-q8.gguf" "%RELEASE_DIR%\sentence\Models\sentence-qwen-q8.gguf" >nul || exit /b 1
copy /Y "%ROOT%\third_party\llama.cpp\LICENSE" "%RELEASE_DIR%\sentence\licenses\llama.cpp-LICENSE.txt" >nul || exit /b 1
copy /Y "%SENTENCE_QWEN_LICENSE%" "%RELEASE_DIR%\sentence\licenses\Qwen3-LICENSE.txt" >nul || exit /b 1
copy /Y "%CORE_OUT%\Models\sentence-ngram-v2.bin" "%RELEASE_DIR%\Models\sentence-ngram-v2.bin" >nul || exit /b 1
copy /Y "%HOOK_OUT%\TigerClaw.Hook.Native.exe" "%RELEASE_DIR%\TigerClaw.exe" >nul || exit /b 1
if exist "%ROOT%\next\TigerClaw.Dialog\bime.ico" copy /Y "%ROOT%\next\TigerClaw.Dialog\bime.ico" "%RELEASE_DIR%\bime.ico" >nul || exit /b 1
copy /Y "%WRAPPER_DLL%" "%RELEASE_DIR%\TigerClaw.dll" >nul || exit /b 1
copy /Y "%TSF_ARM64%" "%RELEASE_DIR%\TigerClawARM64.dll" >nul || exit /b 1
copy /Y "%TSF_SERVER%" "%RELEASE_DIR%\TigerClaw.TsfServer.exe" >nul || exit /b 1
copy /Y "%TSF_X64%" "%RELEASE_DIR%\TigerClawx64.dll" >nul || exit /b 1
copy /Y "%TSF_X86%" "%RELEASE_WIN32_DIR%\TigerClaw.dll" >nul || exit /b 1
copy /Y "%INSTALL_TEMPLATE%" "%RELEASE_DIR%\安装.bat" >nul || exit /b 1
copy /Y "%INSTALL_DIRECT_TEMPLATE%" "%RELEASE_DIR%\安装_ARM64直连诊断.bat" >nul || exit /b 1
copy /Y "%INSTALL_ARM64X_TEMPLATE%" "%RELEASE_DIR%\安装_ARM64X兼容诊断.bat" >nul || exit /b 1
copy /Y "%INSTALL_LOCALSERVER_TEMPLATE%" "%RELEASE_DIR%\安装_LocalServer32诊断.bat" >nul || exit /b 1
copy /Y "%DIAGNOSE_TEMPLATE%" "%RELEASE_DIR%\诊断_ARM64_UWP.bat" >nul || exit /b 1
copy /Y "%UNINSTALL_TEMPLATE%" "%RELEASE_DIR%\卸载.bat" >nul || exit /b 1

echo.
echo [10/10] Done
echo Publish ARM64 succeeded: %RELEASE_DIR%
echo   ARM64X wrapper: %RELEASE_DIR%\TigerClaw.dll
echo   ARM64 TSF     : %RELEASE_DIR%\TigerClawARM64.dll
echo   LocalServer   : %RELEASE_DIR%\TigerClaw.TsfServer.exe
echo   x64 TSF       : %RELEASE_DIR%\TigerClawx64.dll
echo   x86 TSF       : %RELEASE_DIR%\Win32\TigerClaw.dll
exit /b 0

:FindMsbuild
for /f "usebackq delims=" %%I in (`"%~1" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set "MSBUILD=%%~fI"
exit /b 0

:BuildTsf
set "TSF_PLATFORM=%~1"
echo   TSF platform=%TSF_PLATFORM% toolset=%TSF_TOOLSET_ARGS%
"%MSBUILD%" /m /nr:false "%TSF_PROJECT%" /p:Configuration=Release /p:Platform=%TSF_PLATFORM% /p:WholeProgramOptimization=false /v:minimal %TSF_TOOLSET_ARGS%
exit /b %errorlevel%
