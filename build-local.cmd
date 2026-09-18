@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "FAILFILE=%ROOT%artifacts\build-local\last-failure.txt"

where pwsh >nul 2>&1
if errorlevel 1 (
    echo PowerShell 7 ^(pwsh^) is required. Install it from https://aka.ms/powershell
    echo Then double-click build-local.cmd again. Nothing was built, stopped or installed.
    echo.
    pause
    exit /b 1
)

rem A marker left by an earlier run must never be read as this run's outcome.
if exist "%FAILFILE%" del /q "%FAILFILE%"

pwsh -NoProfile -File "%ROOT%scripts\Build-Local.ps1" %*
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
    echo.
    echo CycleArc build/install failed ^(exit %ERR%^).
    rem Build-Local.ps1 writes the stage it actually reached. Only that file may
    rem say whether the previous installation is still intact.
    if exist "%FAILFILE%" (
        type "%FAILFILE%"
    ) else (
        echo The run failed before it could record a stage, so it did not get as far as
        echo starting CycleArc-Setup.exe. See the console output above.
    )
    echo.
    echo Logs: "%ROOT%artifacts\build-local"
    echo.
    pause
    exit /b %ERR%
)
endlocal
exit /b 0
