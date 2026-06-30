@echo off
setlocal EnableDelayedExpansion
title Hail Mary — Python einrichten

set "ROOT=%~dp0"
set "VENV=%ROOT%.venv"
set "VENV_PY=%VENV%\Scripts\python.exe"

echo.
echo ========================================
echo  Hail Mary — Python-Umgebung einrichten
echo ========================================
echo.
echo Dieses Skript richtet Python fuer die Hintergrund-Jobs ein
echo (Oxco, Bitrate, Exporte usw.). Die App selbst ist keine Python-App.
echo.

REM --- Python finden oder installieren ---
set "PY_CMD="
where python >nul 2>&1 && set "PY_CMD=python"
if not defined PY_CMD (
    where py >nul 2>&1 && set "PY_CMD=py -3"
)

if not defined PY_CMD (
    echo Python wurde auf diesem PC nicht gefunden.
    echo.
    echo Es wird versucht, Python 3.12 per winget zu installieren.
    echo Dafuer kann ein Bestaetigungsfenster erscheinen — bitte zulassen.
    echo.
    winget install -e --id Python.Python.3.12 --accept-package-agreements --accept-source-agreements
    if errorlevel 1 (
        echo.
        echo Automatische Installation fehlgeschlagen.
        echo Bitte Python manuell installieren:
        echo   https://www.python.org/downloads/
        echo Beim Setup „Add python.exe to PATH“ ankreuzen, dann dieses Skript erneut starten.
        echo.
        pause
        exit /b 1
    )
    echo.
    echo Python wurde installiert. Bitte dieses Fenster schliessen,
    echo ein NEUES Eingabeaufforderungs-Fenster oeffnen und setup_python.bat nochmal starten
    echo (damit PATH aktualisiert wird).
    echo.
    pause
    exit /b 0
)

echo Gefundenes Python: %PY_CMD%
echo.

REM --- Virtuelle Umgebung (.venv) ---
if exist "%VENV_PY%" (
    echo Virtuelle Umgebung existiert bereits: %VENV%
) else (
    echo Erstelle virtuelle Umgebung in .venv ...
    %PY_CMD% -m venv "%VENV%"
    if errorlevel 1 (
        echo venv konnte nicht erstellt werden.
        pause
        exit /b 1
    )
    echo OK.
)

echo.
echo Installiere Python-Pakete (OpenCV, NumPy, Pillow) ...
echo Das kann einige Minuten dauern.
echo.
"%VENV_PY%" -m pip install --upgrade pip
"%VENV_PY%" -m pip install -r "%ROOT%requirements.txt"
if errorlevel 1 (
    echo.
    echo Paket-Installation fehlgeschlagen.
    pause
    exit /b 1
)

echo.
echo ========================================
echo  Fertig!
echo ========================================
echo.
echo Python fuer Hail Mary liegt hier:
echo   %VENV_PY%
echo.
echo Beim Start ueber start_hail_mary.bat wird diese Python-Version automatisch genutzt.
echo Alternativ in der App unter Einstellungen ^> Python-Pfad eintragen.
echo.
echo WICHTIG: FFmpeg muss separat installiert und im PATH sein — siehe README.md
echo.
pause
endlocal
