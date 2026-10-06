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

rem A marker left by an earlier run must never be read as this run's outcome.
if exist "%FAILFILE%" del /q "%FAILFILE%"

where pwsh >nul 2>&1
if errorlevel 1 goto bootstrap_start
rem A legacy or preview pwsh on PATH cannot enter the stable PowerShell 7 build flow.
pwsh -NoProfile -NonInteractive -Command "if ($PSVersionTable.PSVersion.Major -ge 7 -and -not $PSVersionTable.PSVersion.PreReleaseLabel) { exit 0 }; exit 1" >nul 2>&1
if errorlevel 1 goto bootstrap_start

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

:bootstrap_start
rem Windows PowerShell prepares missing prerequisites for this interactive build.
rem -ManualPrerequisites and all suppression switches pass through unchanged.
rem The bootstrap finds a stable PowerShell outside this console's PATH too.
powershell.exe -NoProfile -File "%ROOT%scripts\Bootstrap-BuildLocal.ps1" %*
if errorlevel 1 goto bootstrap_failed
exit /b 0

:bootstrap_failed
rem The bootstrap reports prerequisite failures without input; Build-Local.ps1 owns build-stage pauses.
exit /b %ERRORLEVEL%
