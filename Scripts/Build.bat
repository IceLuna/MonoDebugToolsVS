@echo off
rem Builds EagleDebugTools.sln from the command line (Release by default) using the newest Visual Studio's MSBuild.
rem Usage: Scripts\Build.bat [Debug|Release]
setlocal
pushd "%~dp0.."

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

if not exist "EagleDebugTools.sln" (
    echo [Build] EagleDebugTools.sln not found. Run Scripts\Setup.bat first.
    goto :fail
)

set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
if not exist "%VSWHERE%" (
    echo [Build] vswhere.exe not found. Is Visual Studio 2022 installed?
    goto :fail
)

set MSBUILD=
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
    if not defined MSBUILD set "MSBUILD=%%i"
)
if not defined MSBUILD (
    echo [Build] MSBuild not found.
    goto :fail
)

"%MSBUILD%" EagleDebugTools.sln -restore -m -nologo -v:minimal -p:Configuration=%CONFIG%
if errorlevel 1 goto :fail

echo.
echo [Build] Done: bin\%CONFIG%\EagleDebugTools.vsix
popd
exit /b 0

:fail
echo [Build] FAILED
popd
exit /b 1
