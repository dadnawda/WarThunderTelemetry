@echo off
REM ============================================================
REM  Build the Windows installer (single Setup.exe) with Inno Setup.
REM
REM  Prerequisite: publish\WarThunderTelemetry\ must exist first.
REM  Run _publish-run.cmd if it does not.
REM
REM  Output: publish\WarThunderTelemetry-Setup-1.0.0.exe
REM  Log   : installer-log.txt
REM ============================================================

setlocal
set "LOG=%~dp0installer-log.txt"
set "ISS=%~dp0installer\setup.iss"

set "ISCC="
for %%P in (
    "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) do (
    if exist %%P if not defined ISCC set "ISCC=%%~P"
)

if not defined ISCC (
    echo Inno Setup 6 not found. Install it with: > "%LOG%"
    echo   winget install JRSoftware.InnoSetup >> "%LOG%"
    echo and re-run this script. >> "%LOG%"
    echo Inno Setup 6 not found. See installer-log.txt
    exit /b 1
)

if not exist "%~dp0publish\WarThunderTelemetry\WarThunderTelemetry.exe" (
    echo Publish folder missing. Run _publish-run.cmd first. > "%LOG%"
    echo Publish folder missing. Run _publish-run.cmd first.
    exit /b 1
)

echo === Building installer === > "%LOG%"
echo ISCC: %ISCC% >> "%LOG%"

"%ISCC%" "%ISS%" >> "%LOG%" 2>&1
echo EXITCODE=%ERRORLEVEL% >> "%LOG%"

echo EXITCODE=%ERRORLEVEL%
endlocal
