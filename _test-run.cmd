@echo off
setlocal
set "LOG=%~dp0test-log.txt"
set "TESTDLL=%~dp0src\WarThunderTelemetry.Tests\bin\Debug\net10.0-windows10.0.26100.0\WarThunderTelemetry.Tests.dll"

if not exist "%TESTDLL%" (
    echo TEST DLL NOT FOUND > "%LOG%"
    exit /b 1
)

dotnet vstest "%TESTDLL%" --logger:"console;verbosity=normal" > "%LOG%" 2>&1
echo EXITCODE=%ERRORLEVEL% >> "%LOG%"
endlocal
