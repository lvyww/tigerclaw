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

call :unreg "%ROOT%SampleIME\x64\Debug\BimeTSF2.dll"
call :unreg "%ROOT%x64\Debug\BimeTSF2.dll"

del /Q "%ROOT%SampleIME\x64\Debug\SampleIMESimplifiedQuanPin.txt" >nul 2>&1
del /Q "%ROOT%x64\Debug\SampleIMESimplifiedQuanPin.txt" >nul 2>&1

echo Done.
exit /b 0

:unreg
set "DLL_PATH=%~1"
if not exist "%DLL_PATH%" (
    echo Skip (not found): %DLL_PATH%
    exit /b 0
)

echo Unregistering:
echo   %DLL_PATH%
%windir%\system32\regsvr32.exe /s /u "%DLL_PATH%"
if %errorLevel% neq 0 (
    echo WARN: regsvr32 /u failed: %DLL_PATH%
)
exit /b 0

