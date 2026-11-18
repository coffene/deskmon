@echo off
title PC Resource Monitor
setlocal
set "EXE_PATH=%~dp0PCMonitorServer.exe"
if exist "%EXE_PATH%" (
    start "" "%EXE_PATH%"
    exit /b 0
)

set "VENV_PY=%~dp0.venv\Scripts\python.exe"
if exist "%VENV_PY%" (
    "%VENV_PY%" "%~dp0server.py"
) else (
    python "%~dp0server.py"
)
