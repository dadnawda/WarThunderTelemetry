@echo off
setlocal
cd /d "%~dp0"
set "EXE=src\WarThunderTelemetry.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WarThunderTelemetry.exe"

if not exist "%EXE%" (
  echo [ERROR] EXE not found. Run _build-run.cmd first.
  echo         Expected: %EXE%
  pause
  exit /b 1
)

echo Starting WarThunderTelemetry ...
start "" "%EXE%"

timeout /t 5 /nobreak >nul

set "CRASH=src\WarThunderTelemetry.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\crash.log"
if exist "%CRASH%" (
  echo.
  echo [WARN] Startup exception detected, see crash.log:
  echo ------------------------------------------------------------
  type "%CRASH%"
  echo ------------------------------------------------------------
  pause
  exit /b 1
)

echo Started OK. Main window: "War Thunder Telemetry"
echo.
echo Tips:
echo   1. Click "Show HUD" to overlay data on the game.
echo   2. Double-click the HUD to lock and enable click-through.
echo   3. Settings page: pick fields, font size, opacity.
