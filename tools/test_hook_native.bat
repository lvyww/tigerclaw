@echo off
setlocal
if not defined VCToolsInstallDir call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
set "HOOK_TEST_OUT=%TEMP%\TigerClaw.HookTests"
if not exist "%HOOK_TEST_OUT%" mkdir "%HOOK_TEST_OUT%"
cl /nologo /EHsc /utf-8 /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /Fe:"%HOOK_TEST_OUT%\test_hook_native.exe" /Fo:"%HOOK_TEST_OUT%\test_hook_native.obj" "%~dp0test_hook_native.cpp" /link user32.lib advapi32.lib ole32.lib
if errorlevel 1 exit /b 1
"%HOOK_TEST_OUT%\test_hook_native.exe"
exit /b %errorlevel%
