@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"

where pwsh >nul 2>&1
if errorlevel 1 (
    echo PowerShell 7 ^(pwsh^) is required. Install it from https://aka.ms/powershell
    echo Then double-click build-local.cmd again. The current CycleArc installation was not changed.
    echo.
    pause
    exit /b 1
)

pwsh -NoProfile -File "%ROOT%scripts\Build-Local.ps1" %*
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
    echo.
    echo CycleArc build/install failed ^(exit %ERR%^).
    echo The log is under "%ROOT%artifacts\build-local"
    echo Setup.exe was not started unless the log shows it; the previous installation was left in place.
    echo.
    pause
    exit /b %ERR%
)
endlocal
exit /b 0
