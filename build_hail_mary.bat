@echo off
setlocal
title Hail Mary — Build

set "PROJ=%~dp0src\HailMary\HailMary.csproj"
echo Baue Hail Mary (x64, Debug)...
echo.

dotnet build "%PROJ%" -c Debug -p:Platform=x64
if errorlevel 1 (
    echo.
    echo BUILD FEHLGESCHLAGEN.
    pause
    exit /b 1
)

echo.
echo OK — EXE:
echo   %~dp0src\HailMary\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\HailMary.exe
echo.
echo Zum Starten: start_hail_mary.bat
pause
endlocal
