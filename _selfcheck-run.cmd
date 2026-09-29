@echo off
setlocal
cd /d "%~dp0"
set "EXE=src\WarThunderTelemetry.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WarThunderTelemetry.exe"
if not exist "%EXE%" (
  echo EXE-NOT-FOUND: %EXE%
  exit /b 1
)
echo --- running ---
"%EXE%" --selfcheck
echo RAWEXIT=%ERRORLEVEL%
