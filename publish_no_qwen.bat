@echo off
setlocal EnableExtensions
call "%~dp0publish.bat" --no-qwen
exit /b %errorlevel%
