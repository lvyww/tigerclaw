@echo off
setlocal EnableExtensions
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish_cpp_core.ps1" %*
exit /b %errorlevel%
