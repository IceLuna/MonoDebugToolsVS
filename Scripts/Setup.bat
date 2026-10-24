@echo off
rem Generates EagleDebugTools.sln with premake (downloads premake if it isn't installed)
rem and fetches External\debugger-libs.
setlocal
pushd "%~dp0.."

set PREMAKE_VERSION=5.0.0-beta8
set PREMAKE=vendor\premake\premake5.exe

where git >nul 2>nul
if errorlevel 1 (
    echo [Setup] git was not found on PATH. Install Git for Windows and run this again.
    goto :fail
)

if exist "%PREMAKE%" goto :generate

where premake5 >nul 2>nul
if not errorlevel 1 (
    set PREMAKE=premake5
    goto :generate
)

echo [Setup] Downloading premake %PREMAKE_VERSION% into vendor\premake ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $v = '%PREMAKE_VERSION%'; $zip = Join-Path $env:TEMP ('premake-' + $v + '-windows.zip'); Invoke-WebRequest -UseBasicParsing ('https://github.com/premake/premake-core/releases/download/v' + $v + '/premake-' + $v + '-windows.zip') -OutFile $zip; Expand-Archive -Force $zip 'vendor\premake'; Remove-Item $zip"
if errorlevel 1 goto :fail
if not exist "%PREMAKE%" (
    echo [Setup] premake download did not produce %PREMAKE%
    goto :fail
)

:generate
"%PREMAKE%" vs2022
if errorlevel 1 goto :fail

echo.
echo [Setup] Done. Open EagleDebugTools.sln and build it, or run Scripts\Build.bat.
echo [Setup] The extension installer will be bin\^<Configuration^>\EagleDebugTools.vsix
popd
pause
exit /b 0

:fail
echo [Setup] FAILED
popd
pause
exit /b 1
