@echo off
setlocal

cd /d "%~dp0"
set "HAIL_MARY_PROJECTS_ROOT=%~dp0.."

if exist "%~dp0.venv\Scripts\python.exe" (
    set "HAIL_MARY_PYTHON=%~dp0.venv\Scripts\python.exe"
)

call "%~dp0start_hail_mary.bat"
exit /b %errorlevel%
