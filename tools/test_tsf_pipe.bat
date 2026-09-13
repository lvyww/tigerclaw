@echo off
setlocal
if not defined VCToolsInstallDir call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
set "TSF_TEST_OUT=%TEMP%\TigerClaw.PipeTests"
if not exist "%TSF_TEST_OUT%" mkdir "%TSF_TEST_OUT%"
cl /nologo /EHsc /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /Fe:"%TSF_TEST_OUT%\test_tsf_pipe.exe" /Fo:"%TSF_TEST_OUT%\test_tsf_pipe.obj" "%~dp0test_tsf_pipe.cpp" /link user32.lib advapi32.lib ole32.lib
if errorlevel 1 exit /b 1
"%TSF_TEST_OUT%\test_tsf_pipe.exe"
exit /b %errorlevel%
