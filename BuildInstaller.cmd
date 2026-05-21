@echo off
setlocal
rem Build the Clipsy installer. Double-clickable; resolves its own path
rem so the working directory does not matter.

set "REPO_DIR=%~dp0"
set "SCRIPT=%REPO_DIR%installer\build.ps1"

if not exist "%SCRIPT%" (
    echo Cannot find %SCRIPT%
    echo Make sure this .cmd lives at the repo root next to the installer folder.
    pause
    exit /b 1
)

where powershell.exe >nul 2>&1
if errorlevel 1 (
    echo PowerShell not found on PATH. Install Windows PowerShell or PowerShell 7.
    pause
    exit /b 1
)

set "VERSION_FILE=%REPO_DIR%version"
if not exist "%VERSION_FILE%" set "VERSION_FILE=%REPO_DIR%installer\version.txt"
set "CLIPSY_VERSION="

echo(%* | findstr /I /C:"-Version" /C:"/Version" >nul
if errorlevel 1 (
    if exist "%VERSION_FILE%" (
        for /f "usebackq delims=" %%V in (`powershell.exe -NoProfile -Command "(Get-Content -LiteralPath '%VERSION_FILE%' -Raw).Trim()"`) do set "CLIPSY_VERSION=%%V"
    )
    if defined CLIPSY_VERSION (
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Version "%CLIPSY_VERSION%" %*
    ) else (
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
    )
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
)
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo Build finished successfully.
) else (
    echo Build exited with code %RC%.
)
pause
exit /b %RC%
