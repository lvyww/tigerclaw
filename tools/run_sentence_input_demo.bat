@echo off
setlocal
set "PYTHON_EXE=%LOCALAPPDATA%\TigerClawML\venv-directml\Scripts\python.exe"
if not exist "%PYTHON_EXE%" (
  echo TigerClaw ML Python environment was not found.
  echo Expected: %PYTHON_EXE%
  pause
  exit /b 1
)
"%PYTHON_EXE%" "%~dp0try_sentence_input.py" %*
if errorlevel 1 pause
endlocal
