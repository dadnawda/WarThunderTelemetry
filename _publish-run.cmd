@echo off
REM ============================================================
REM  Publish a self-contained Release build (x64).
REM  Output: publish\WarThunderTelemetry\  (all deps included)
REM  Log   : publish-log.txt
REM
REM  NOTE: keep this file pure ASCII - cmd mangles non-ASCII bytes.
REM  The readme (Chinese filename) is copied by MSBuild, not here.
REM ============================================================

setlocal
set "LOG=%~dp0publish-log.txt"
set "PROJ=%~dp0src\WarThunderTelemetry.App\WarThunderTelemetry.App.csproj"
set "OUT=%~dp0publish\WarThunderTelemetry"
set "OUTROOT=%~dp0publish"

set "MSBUILD=C:\app\vs2026\MSBuild\Current\Bin\MSBuild.exe"

if not exist "%MSBUILD%" (
    echo MSBUILD NOT FOUND > "%LOG%"
    exit /b 1
)

echo === Publish WarThunderTelemetry (Release / x64 / self-contained) === > "%LOG%"

if exist "%OUTROOT%" rmdir /s /q "%OUTROOT%"

"%MSBUILD%" "%PROJ%" /t:Publish ^
  /p:Configuration=Release ^
  /p:Platform=x64 ^
  /p:RuntimeIdentifier=win-x64 ^
  /p:SelfContained=true ^
  /p:WindowsAppSDKSelfContained=true ^
  /p:PublishDir="%OUT%/" ^
  /restore /v:minimal >> "%LOG%" 2>&1

echo EXITCODE=%ERRORLEVEL% >> "%LOG%"
set "PUBLISH_EXIT=%ERRORLEVEL%"

REM Strip debug symbols - not needed in a distribution package.
if "%PUBLISH_EXIT%"=="0" (
    del /q "%OUT%\*.pdb" >> "%LOG%" 2>&1
)

endlocal & exit /b %PUBLISH_EXIT%
