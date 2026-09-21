@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "FAILFILE=%ROOT%artifacts\build-local\last-failure.txt"
set "CYCLEARC_BUILD_LOCAL_CMD=1"
set "CYCLEARC_NO_PAUSE="
for %%A in (%*) do (
    if /I "%%~A"=="-SilentInstall" set "CYCLEARC_NO_PAUSE=1"
    if /I "%%~A"=="-NoPrerequisitePrompt" set "CYCLEARC_NO_PAUSE=1"
)

where pwsh >nul 2>&1
if errorlevel 1 (
    echo PowerShell 7 ^(pwsh^) is required. Install it from https://aka.ms/powershell
    echo Then double-click build-local.cmd again. Nothing was built, stopped or installed.
    echo.
    rem Even this early failure must not wait for input on a redirected/CI console.
    powershell.exe -NoProfile -NonInteractive -Command "if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected -and -not $env:CI -and -not $env:GITHUB_ACTIONS -and -not $env:TF_BUILD -and -not $env:CYCLEARC_NO_PAUSE) { exit 0 }; exit 1"
    if not errorlevel 1 pause
    exit /b 1
)

rem A marker left by an earlier run must never be read as this run's outcome.
if exist "%FAILFILE%" del /q "%FAILFILE%"

pwsh -NoProfile -File "%ROOT%scripts\Build-Local.ps1" %*
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
    rem Build-Local.ps1 already printed its recorded stage and cause once, and handles
    rem the interactive pause. A missing log cannot prove which operations ran.
    if not exist "%FAILFILE%" (
        echo PowerShell exited with code %ERR%; no failure log is available.
        echo See the console output above for the cause and any recorded stage.
    )
    exit /b %ERR%
)
endlocal
exit /b 0
