@echo off
setlocal
chcp 65001 >nul

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Admin permission required, relaunching...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

set "ROOT=%~dp0"
set "DLL_PATH=%ROOT%SampleIME\x64\Debug\BimeTSF2.dll"
if not exist "%DLL_PATH%" (
    set "DLL_PATH=%ROOT%x64\Debug\BimeTSF2.dll"
)

if not exist "%DLL_PATH%" (
    echo ERROR: BimeTSF2.dll not found.
    echo Tried:
    echo   %ROOT%SampleIME\x64\Debug\BimeTSF2.dll
    echo   %ROOT%x64\Debug\BimeTSF2.dll
    pause
    exit /b 1
)

echo Registering:
echo   %DLL_PATH%
%windir%\system32\regsvr32.exe /s "%DLL_PATH%"
if %errorLevel% neq 0 (
    echo ERROR: regsvr32 failed.
    pause
    exit /b 1
)

for %%I in ("%DLL_PATH%") do set "OUT_DIR=%%~dpI"
if exist "%ROOT%SampleIME\Dictionary\SampleIMESimplifiedQuanPin.txt" (
    copy /Y "%ROOT%SampleIME\Dictionary\SampleIMESimplifiedQuanPin.txt" "%OUT_DIR%" >nul
)

echo Done.
pause

