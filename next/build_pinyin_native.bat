@echo off
setlocal EnableExtensions

if "%~2"=="" (
  echo Usage: build_pinyin_native.bat ARCH OUTPUT_DIR [CONFIGURATION] [JOBS]
  exit /b 2
)

set "ARCH=%~1"
set "OUTPUT_DIR=%~f2"
set "CONFIGURATION=%~3"
if not defined CONFIGURATION set "CONFIGURATION=Release"
set "BUILD_JOBS=%~4"
if not defined BUILD_JOBS set "BUILD_JOBS=8"
powershell -NoProfile -Command "if ('%BUILD_JOBS%' -notmatch '^(?:[1-9]|[1-5][0-9]|6[0-4])$') { exit 2 }" || exit /b 2
if /I not "%ARCH%"=="x64" if /I not "%ARCH%"=="ARM64" (
  echo ERROR: Unsupported pinyin native architecture: %ARCH%
  exit /b 2
)

set "ROOT=%~dp0.."
set "SOURCE_DIR=%~dp0TigerClaw.Pinyin.Native"
set "BUILD_DIR=%~dp0_native_build\pinyin-%ARCH%"
if /I "%ARCH%"=="ARM64" set "BUILD_DIR=%~dp0_native_build\pinyin-ARM64-ClangCL"
set "CMAKE="
set "CMAKE_GENERATOR="
for /f "delims=" %%I in ('where.exe cmake.exe 2^>nul') do if not defined CMAKE set "CMAKE=%%~fI"

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not defined CMAKE if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -find Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`) do if not defined CMAKE set "CMAKE=%%~fI"
)
if not defined CMAKE if exist "%ProgramFiles%\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" (
  set "CMAKE=%ProgramFiles%\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
  set "CMAKE_GENERATOR=Visual Studio 18 2026"
)
if not defined CMAKE if exist "%ProgramFiles%\Microsoft Visual Studio\17\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" (
  set "CMAKE=%ProgramFiles%\Microsoft Visual Studio\17\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
  set "CMAKE_GENERATOR=Visual Studio 17 2022"
)
if not defined CMAKE_GENERATOR if exist "%ProgramFiles%\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" set "CMAKE_GENERATOR=Visual Studio 18 2026"
if not defined CMAKE_GENERATOR if exist "%ProgramFiles%\Microsoft Visual Studio\17\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" set "CMAKE_GENERATOR=Visual Studio 17 2022"
if not defined CMAKE (
  echo ERROR: cmake.exe not found.
  exit /b 1
)
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%" >nul 2>&1
set "CMAKE_TOOLSET_ARGS="
if /I "%ARCH%"=="ARM64" set "CMAKE_TOOLSET_ARGS=-T ClangCL"
if defined CMAKE_GENERATOR (
  "%CMAKE%" -G "%CMAKE_GENERATOR%" -S "%SOURCE_DIR%" -B "%BUILD_DIR%" -A %ARCH% %CMAKE_TOOLSET_ARGS%
) else (
  "%CMAKE%" -S "%SOURCE_DIR%" -B "%BUILD_DIR%" -A %ARCH% %CMAKE_TOOLSET_ARGS%
)
if errorlevel 1 (
  if /I "%ARCH%"=="ARM64" echo ERROR: ARM64 KenLM requires the Visual Studio C++ Clang tools component.
  exit /b 1
)
"%CMAKE%" --build "%BUILD_DIR%" --config "%CONFIGURATION%" --target jointkenlm -j %BUILD_JOBS% || exit /b 1
copy /Y "%BUILD_DIR%\%CONFIGURATION%\jointkenlm.dll" "%OUTPUT_DIR%\jointkenlm.dll" >nul || exit /b 1
exit /b 0
