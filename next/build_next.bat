@echo off
setlocal EnableExtensions

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD="
if exist "%VSWHERE%" call :FindMsbuild "%VSWHERE%"
if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD (
  for /f "delims=" %%I in ('where.exe MSBuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%~fI"
)
if not defined MSBUILD (
  echo MSBuild not found.
  exit /b 1
)

set "DOTNET="
for /f "delims=" %%I in ('where.exe dotnet.exe 2^>nul') do if not defined DOTNET set "DOTNET=%%~fI"
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET (
  echo dotnet not found.
  exit /b 1
)

set "UNIFIED_OUT=%~dp0_run\Debug\net48"
set "HOOK_NATIVE_PROJECT=%~dp0TigerClaw.Hook.Native\TigerClaw.Hook.Native.vcxproj"
set "HOOK_NATIVE_OUT=%~dp0_run\Debug\native"
set "SENTENCE_PROJECT=%~dp0TigerClaw.Sentence\TigerClaw.Sentence.csproj"
set "SENTENCE_OUT=%~dp0_run\Debug\sentence"
set "SENTENCE_MODEL_ROOT=C:\Archive\tigerclaw_sentence_ml\runtime"
set "SENTENCE_DATA_ROOT=C:\Archive\tigerclaw_sentence_ml\pilot200m"
set "ORT_NATIVE_ROOT=%LocalAppData%\TigerClawML\venv-directml\Lib\site-packages\onnxruntime\capi"

echo [0/6] Stop running next processes (if any)
for %%P in (TigerClaw.Core.exe TigerClaw.Overlay.exe TigerClaw.Dialog.exe TigerClaw.Sentence.exe TigerClaw.Hook.Native.exe) do (
  taskkill /F /IM %%P >nul 2>nul
)

echo [1/6] Build TigerClaw.Shared
"%DOTNET%" msbuild "%~dp0TigerClaw.Shared\TigerClaw.Shared.csproj" /restore /p:Configuration=Debug /m || exit /b 1

echo [2/6] Build TigerClaw.Core (OutDir: _run)
"%DOTNET%" msbuild "%~dp0TigerClaw.Core\TigerClaw.Core.csproj" /restore /p:Configuration=Debug /p:OutDir="%UNIFIED_OUT%\\" /m || exit /b 1

echo [3/6] Build TigerClaw.Overlay (OutDir: Core)
"%DOTNET%" msbuild "%~dp0TigerClaw.Overlay\TigerClaw.Overlay.csproj" /restore /p:Configuration=Debug /p:OutDir="%UNIFIED_OUT%\\" /m || exit /b 1

echo [4/6] Build TigerClaw.Dialog (OutDir: Core)
"%DOTNET%" msbuild "%~dp0TigerClaw.Dialog\TigerClaw.Dialog.csproj" /restore /p:Configuration=Debug /p:OutDir="%UNIFIED_OUT%\\" /m || exit /b 1

echo [5/6] Publish optional TigerClaw.Sentence sidecar
"%DOTNET%" msbuild "%SENTENCE_PROJECT%" /restore /p:Configuration=Debug /p:Platform=x64 /p:OutDir="%SENTENCE_OUT%\\" /m || exit /b 1
if exist "%ORT_NATIVE_ROOT%\onnxruntime.dll" copy /Y "%ORT_NATIVE_ROOT%\onnxruntime.dll" "%SENTENCE_OUT%\onnxruntime.dll" >nul
if exist "%ORT_NATIVE_ROOT%\onnxruntime_providers_shared.dll" copy /Y "%ORT_NATIVE_ROOT%\onnxruntime_providers_shared.dll" "%SENTENCE_OUT%\onnxruntime_providers_shared.dll" >nul
if not exist "%UNIFIED_OUT%\Models" mkdir "%UNIFIED_OUT%\Models"
if not exist "%SENTENCE_OUT%\Models" mkdir "%SENTENCE_OUT%\Models"
if exist "%UNIFIED_OUT%\Models\sentence-ngram.bin" del /q "%UNIFIED_OUT%\Models\sentence-ngram.bin"
if exist "%UNIFIED_OUT%\Models\sentence-ngram.tcmodel" del /q "%UNIFIED_OUT%\Models\sentence-ngram.tcmodel"
if exist "%SENTENCE_MODEL_ROOT%\sentence-ngram-v2.bin" copy /Y "%SENTENCE_MODEL_ROOT%\sentence-ngram-v2.bin" "%UNIFIED_OUT%\Models\sentence-ngram-v2.bin" >nul
if exist "%SENTENCE_MODEL_ROOT%\sentence-transformer.onnx" copy /Y "%SENTENCE_MODEL_ROOT%\sentence-transformer.onnx" "%SENTENCE_OUT%\Models\sentence-transformer.onnx" >nul
if exist "%SENTENCE_MODEL_ROOT%\sentence-transformer.json" copy /Y "%SENTENCE_MODEL_ROOT%\sentence-transformer.json" "%SENTENCE_OUT%\Models\sentence-transformer.json" >nul
if exist "%SENTENCE_DATA_ROOT%\vocabulary.json" copy /Y "%SENTENCE_DATA_ROOT%\vocabulary.json" "%SENTENCE_OUT%\Models\sentence-vocabulary.json" >nul

if exist "%HOOK_NATIVE_PROJECT%" (
  echo [6/6] Build TigerClaw.Hook.Native (OutDir: native)
  "%MSBUILD%" "%HOOK_NATIVE_PROJECT%" /p:Configuration=Debug /p:Platform=x64 /p:OutDir="%HOOK_NATIVE_OUT%\\" /m || exit /b 1
)

if not exist "%UNIFIED_OUT%\TigerClaw.Core.exe" (
  echo Missing output: %UNIFIED_OUT%\TigerClaw.Core.exe
  exit /b 1
)
if not exist "%UNIFIED_OUT%\TigerClaw.Overlay.exe" (
  echo Missing output: %UNIFIED_OUT%\TigerClaw.Overlay.exe
  exit /b 1
)
if not exist "%UNIFIED_OUT%\TigerClaw.Dialog.exe" (
  echo Missing output: %UNIFIED_OUT%\TigerClaw.Dialog.exe
  exit /b 1
)
if not exist "%SENTENCE_OUT%\TigerClaw.Sentence.exe" (
  echo Missing output: %SENTENCE_OUT%\TigerClaw.Sentence.exe
  exit /b 1
)
if exist "%HOOK_NATIVE_PROJECT%" if not exist "%HOOK_NATIVE_OUT%\TigerClaw.Hook.Native.exe" (
  echo Missing output: %HOOK_NATIVE_OUT%\TigerClaw.Hook.Native.exe
  exit /b 1
)

echo Done.
echo Unified debug folder:
echo   %UNIFIED_OUT%
echo Start command:
echo   "%UNIFIED_OUT%\TigerClaw.Core.exe" --with-overlay
if exist "%HOOK_NATIVE_OUT%\TigerClaw.Hook.Native.exe" (
  echo Optional native hook frontend:
  echo   "%HOOK_NATIVE_OUT%\TigerClaw.Hook.Native.exe"
)
exit /b 0

:FindMsbuild
set "VSWHERE_PATH=%~1"
for /f "usebackq delims=" %%I in (`"%VSWHERE_PATH%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set "MSBUILD=%%~fI"
exit /b 0
