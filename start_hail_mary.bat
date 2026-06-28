@echo off
setlocal EnableDelayedExpansion
title Hail Mary

set "ROOT=%~dp0"
set "HAIL_MARY_PROJECTS_ROOT=%ROOT%.."
set "PROJ=%ROOT%src\HailMary"
set "EXE=%PROJ%\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\HailMary.exe"

echo Hail Mary wird gestartet...
echo Projects-Root: %HAIL_MARY_PROJECTS_ROOT%
echo.

if not exist "%EXE%" (
    echo EXE nicht gefunden — baue zuerst...
    pushd "%PROJ%"
    dotnet build -c Debug -p:Platform=x64
    if errorlevel 1 (
        echo.
        echo BUILD FEHLGESCHLAGEN.
        pause
        exit /b 1
    )
    popd
)

if not exist "%EXE%" (
    echo EXE immer noch nicht gefunden:
    echo   %EXE%
    pause
    exit /b 1
)

echo Starte: %EXE%
echo.
start "" "%EXE%"

if errorlevel 1 (
    echo Start fehlgeschlagen.
    pause
    exit /b 1
)

echo Fenster sollte erscheinen. Dieses Fenster schliesst sich in 3 Sekunden...
timeout /t 3 /nobreak >nul
endlocal
