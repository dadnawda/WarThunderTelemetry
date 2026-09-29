@echo off
setlocal
set "LOG=%~dp0build-log.txt"
set "SLN=%~dp0WarThunderTelemetry.sln"

set "MSBUILD=C:\app\vs2026\MSBuild\Current\Bin\MSBuild.exe"

if not exist "%MSBUILD%" (
    echo MSBUILD NOT FOUND > "%LOG%"
    exit /b 1
)

"%MSBUILD%" "%SLN%" /p:Configuration=Debug /p:Platform=x64 /restore /v:minimal > "%LOG%" 2>&1
echo EXITCODE=%ERRORLEVEL% >> "%LOG%"
endlocal
