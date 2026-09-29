@echo off
REM ============================================================
REM  战雷遥测 —— 构建脚本
REM
REM  注意：本项目【必须】用 Visual Studio 的 MSBuild 构建。
REM  dotnet build 会失败（MSB4062），原因是 WinUI 3 的 PRI 生成
REM  任务 MrtCore.PriGen.targets 需要 VS 目录下的
REM  AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll，
REM  而 dotnet SDK 的 MSBuild 目录下没有这份程序集。
REM ============================================================

setlocal
set "SLN=%~dp0WarThunderTelemetry.sln"

REM 依次尝试几个常见的 VS 安装路径
set "MSBUILD="
for %%P in (
    "C:\app\vs2026\MSBuild\Current\Bin\MSBuild.exe"
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
) do (
    if exist %%P if not defined MSBUILD set "MSBUILD=%%~P"
)

if not defined MSBUILD (
    echo [错误] 未找到 MSBuild.exe
    echo         请修改本脚本顶部的路径列表，指向你的 VS 安装目录。
    exit /b 1
)

echo 使用 MSBuild: %MSBUILD%
echo.

if /i "%~1"=="test" goto :test
if /i "%~1"=="app"  goto :app
if /i "%~1"=="core" goto :core

:all
echo === 构建全部工程 (Release / x64) ===
"%MSBUILD%" "%SLN%" /p:Configuration=Release /p:Platform=x64 /restore /v:minimal
goto :done

:core
echo === 仅构建 Core 并跑测试 ===
"%MSBUILD%" "%SLN%" /t:WarThunderTelemetry_Tests /p:Configuration=Debug /p:Platform=x64 /restore /v:minimal
goto :done

:app
echo === 构建桌面应用 (Debug / x64) ===
"%MSBUILD%" "%SLN%" /t:WarThunderTelemetry_App /p:Configuration=Debug /p:Platform=x64 /restore /v:minimal
goto :done

:test
echo === 运行单元测试 ===
set "TESTDLL=%~dp0src\WarThunderTelemetry.Tests\bin\Debug\net10.0-windows10.0.26100.0\WarThunderTelemetry.Tests.dll"
if not exist "%TESTDLL%" (
    echo [错误] 找不到测试程序集，请先执行 build.cmd core
    exit /b 1
)
dotnet vstest "%TESTDLL%"
goto :done

:done
endlocal
