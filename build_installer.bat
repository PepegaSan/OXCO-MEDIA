@echo off
setlocal EnableDelayedExpansion
title Hail Mary — Installer bauen

set "ROOT=%~dp0"
set "PROJ=%ROOT%src\HailMary\HailMary.csproj"
set "ISS=%ROOT%src\Setup\Inno\hail-mary-setup.iss"
set "PUBLISH=%ROOT%dist\publish"

echo.
echo ========================================
echo  Hail Mary — Release + Inno Setup
echo ========================================
echo.

echo [1/3] dotnet publish (Release, x64, self-contained)...
echo       Publish-Ordner wird zuerst geleert (verhindert alte getrimmte DLLs)...
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"
dotnet publish "%PROJ%" -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishTrimmed=false -o "%PUBLISH%"
if errorlevel 1 (
    echo.
    echo PUBLISH FEHLGESCHLAGEN.
    pause
    exit /b 1
)

if not exist "%PUBLISH%\HailMary.exe" (
    echo.
    echo HailMary.exe nicht gefunden:
    echo   %PUBLISH%\HailMary.exe
    pause
    exit /b 1
)

echo.
echo [2/3] Inno Setup Compiler suchen...
set "ISCC=%~1"
if not defined ISCC set "ISCC=%INNO_SETUP_COMPILER%"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC (
    for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do (
        set "ISCC=%%I"
        goto :iscc_found
    )
)
:iscc_found
if not defined ISCC (
    echo.
    echo ISCC.exe nicht gefunden.
    echo Inno Setup 6 installieren oder Pfad angeben:
    echo   build_installer.bat "C:\Pfad\zu\ISCC.exe"
    echo oder Umgebungsvariable INNO_SETUP_COMPILER setzen.
    pause
    exit /b 1
)

echo Verwende: %ISCC%
echo.
echo [3/3] Installer kompilieren...
"%ISCC%" /DMyAppPublishDir="%PUBLISH%" "%ISS%"
if errorlevel 1 (
    echo.
    echo INNO SETUP FEHLGESCHLAGEN.
    pause
    exit /b 1
)

echo.
echo OK — Installer liegt in:
echo   %ROOT%dist\
echo.
dir /b "%ROOT%dist\HailMary-v*-setup-x64.exe" 2>nul
echo.
pause
endlocal
