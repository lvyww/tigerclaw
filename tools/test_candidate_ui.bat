@echo off
setlocal
set "CANDIDATE_ARCH=%~1"
if not defined CANDIDATE_ARCH set "CANDIDATE_ARCH=x86"
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=%CANDIDATE_ARCH% -host_arch=x64
if errorlevel 1 exit /b 1
set "CANDIDATE_OUT=%~dp0..\next\_run\CandidateUi-20261011\tests-%CANDIDATE_ARCH%"
if not exist "%CANDIDATE_OUT%" mkdir "%CANDIDATE_OUT%"
cl /nologo /EHsc /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /Fe:"%CANDIDATE_OUT%\candidate_ui.exe" /Fo:"%CANDIDATE_OUT%\candidate_ui.obj" "%~dp0test_candidate_ui.cpp" /link user32.lib ole32.lib oleaut32.lib uuid.lib
if errorlevel 1 exit /b 1
"%CANDIDATE_OUT%\candidate_ui.exe"
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /LD /Fe:"%CANDIDATE_OUT%\candidate_factory.dll" /Fo:"%CANDIDATE_OUT%\candidate_factory.obj" "%~dp0test_candidate_ui.cpp" /link user32.lib ole32.lib oleaut32.lib uuid.lib
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /Fe:"%CANDIDATE_OUT%\pipe.exe" /Fo:"%CANDIDATE_OUT%\pipe.obj" "%~dp0test_tsf_pipe.cpp" /link user32.lib advapi32.lib ole32.lib
if errorlevel 1 exit /b 1
"%CANDIDATE_OUT%\pipe.exe"
exit /b %errorlevel%
