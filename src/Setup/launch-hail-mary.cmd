@echo off
setlocal
set "ROOT=%~dp0"
cd /d "%ROOT%"
if exist "%ROOT%.venv\Scripts\python.exe" (
    set "HAIL_MARY_PYTHON=%ROOT%.venv\Scripts\python.exe"
)
if not exist "%ROOT%HailMary.exe" (
    echo HailMary.exe nicht gefunden in:
    echo   %ROOT%
    pause
    exit /b 1
)
start "" /D "%ROOT%" "%ROOT%HailMary.exe"
