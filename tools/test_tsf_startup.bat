@echo off
setlocal
if not defined VCToolsInstallDir call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
set "STARTUP_TEST_OUT=%TEMP%\TigerClaw.StartupTests"
if not exist "%STARTUP_TEST_OUT%" mkdir "%STARTUP_TEST_OUT%"
cl /nologo /EHsc /std:c++17 /DUNICODE /D_UNICODE /Fe:"%STARTUP_TEST_OUT%\test_tsf_startup.exe" /Fo:"%STARTUP_TEST_OUT%\test_tsf_startup.obj" "%~dp0test_tsf_startup.cpp" /link user32.lib advapi32.lib
if errorlevel 1 exit /b 1
"%STARTUP_TEST_OUT%\test_tsf_startup.exe"
exit /b %errorlevel%
