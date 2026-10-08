@echo off
setlocal EnableExtensions
call "%~dp0publish_arm64_cpp_core.bat" %*
exit /b %errorlevel%
