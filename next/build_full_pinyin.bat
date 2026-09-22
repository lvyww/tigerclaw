@echo off
setlocal EnableExtensions
set "PINYIN_ARCH=%~1"
if /I not "%PINYIN_ARCH%"=="ARM64" if /I not "%PINYIN_ARCH%"=="x64" (
  echo Usage: build_full_pinyin.bat ARM64^|x64
  exit /b 2
)
if /I "%PINYIN_ARCH%"=="ARM64" (set "PINYIN_ARCH=ARM64") else (set "PINYIN_ARCH=x64")
set "PINYIN_RID=win-x64"
if /I "%PINYIN_ARCH%"=="ARM64" set "PINYIN_RID=win-arm64"
set "PINYIN_ROOT=%~dp0.."
set "PINYIN_OUT=%~dp0_run\FullPinyin"
set "PINYIN_DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
set "PINYIN_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "PINYIN_MSBUILD="
for /f "usebackq delims=" %%I in (`"%PINYIN_VSWHERE%" -latest -products * -find MSBuild\Current\Bin\MSBuild.exe`) do if not defined PINYIN_MSBUILD set "PINYIN_MSBUILD=%%I"
if not defined PINYIN_MSBUILD exit /b 1
if not exist "%PINYIN_OUT%" mkdir "%PINYIN_OUT%"
call "%~dp0build_pinyin_native.bat" %PINYIN_ARCH% "%PINYIN_OUT%\%PINYIN_ARCH%" Release 2 || exit /b 1
"%PINYIN_DOTNET%" publish "%~dp0TigerClaw.Core\TigerClaw.Core.csproj" -c Release -r %PINYIN_RID% --self-contained -o "%PINYIN_OUT%\Aot-%PINYIN_ARCH%" || exit /b 1
call "%~dp0build_overlay.bat" %PINYIN_ARCH% "%PINYIN_OUT%\Overlay-%PINYIN_ARCH%" Release 2 || exit /b 1
"%PINYIN_DOTNET%" build -t:Rebuild "%~dp0TigerClaw.Dialog\TigerClaw.Dialog.csproj" -c Release /p:TigerClawTargetFramework=net481 /p:PlatformTarget=%PINYIN_ARCH% /p:Prefer32Bit=false -o "%PINYIN_OUT%\Dialog-%PINYIN_ARCH%" || exit /b 1
"%PINYIN_MSBUILD%" "%PINYIN_ROOT%\BimeTSF2\SampleIME\BimeTSF2.vcxproj" /m:2 /nr:false /p:Configuration=Release /p:Platform=%PINYIN_ARCH% /p:WholeProgramOptimization=false /p:OutDir="%PINYIN_OUT%\TSF-%PINYIN_ARCH%\\" /p:IntDir="%PINYIN_OUT%\obj-tsf-%PINYIN_ARCH%\\" /v:minimal || exit /b 1
"%PINYIN_MSBUILD%" "%PINYIN_ROOT%\BimeTSF2\SampleIME\BimeTSF2.vcxproj" /m:2 /nr:false /p:Configuration=Release /p:Platform=Win32 /p:WholeProgramOptimization=false /p:OutDir="%PINYIN_OUT%\TSF-Win32\\" /p:IntDir="%PINYIN_OUT%\obj-tsf-Win32\\" /v:minimal || exit /b 1
if /I "%PINYIN_ARCH%"=="ARM64" (
  "%PINYIN_MSBUILD%" "%PINYIN_ROOT%\BimeTSF2\SampleIME\BimeTSF2.vcxproj" /m:2 /nr:false /p:Configuration=Release /p:Platform=x64 /p:WholeProgramOptimization=false /p:OutDir="%PINYIN_OUT%\TSF-x64\\" /p:IntDir="%PINYIN_OUT%\obj-tsf-x64\\" /v:minimal || exit /b 1
  if not exist "%PINYIN_OUT%\Wrapper\arm64x_wrapper" mkdir "%PINYIN_OUT%\Wrapper\arm64x_wrapper"
  for %%E in (cpp c def bat) do copy /Y "%PINYIN_ROOT%\BimeTSF2\SampleIME\arm64x_wrapper\*.%%E" "%PINYIN_OUT%\Wrapper\arm64x_wrapper\" >nul || exit /b 1
  copy /Y "%PINYIN_ROOT%\BimeTSF2\SampleIME\EmbeddedBuildInfo.h" "%PINYIN_OUT%\Wrapper\EmbeddedBuildInfo.h" >nul || exit /b 1
  call "%PINYIN_OUT%\Wrapper\arm64x_wrapper\build.bat" || exit /b 1
  popd
)
echo Isolated full-pinyin build complete. Nothing was installed or deployed.
exit /b 0
