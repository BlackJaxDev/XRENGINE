@echo off
setlocal

REM Compile third-party submodules for a given configuration/platform.
set "REPO_ROOT=%~dp0"
pushd "%REPO_ROOT%" >nul 2>&1

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"
if /I "%CONFIG%"=="DEBUG" set "CONFIG=Debug"
if /I "%CONFIG%"=="RELEASE" set "CONFIG=Release"

set "PLATFORM=%~2"
if "%PLATFORM%"=="" set "PLATFORM=AnyCPU"
if /I "%PLATFORM%"=="ANYCPU" set "PLATFORM=AnyCPU"
if /I "%PLATFORM%"=="ANY CPU" set "PLATFORM=AnyCPU"
if /I "%PLATFORM%"=="X64" set "PLATFORM=x64"

if /I NOT "%PLATFORM%"=="ANYCPU" if /I NOT "%PLATFORM%"=="X64" (
    echo ERROR: Unsupported platform "%PLATFORM%". Use "AnyCPU" or "x64".
    popd >nul 2>&1
    endlocal
    exit /b 1
)

echo Building submodules with configuration %CONFIG% and platform %PLATFORM%...
echo.

set "SUBMODULE_FAILURE=0"

call :build_project "Build\Submodules\OpenVR.NET\OpenVR.NET\OpenVR.NET.csproj"
if errorlevel 1 set "SUBMODULE_FAILURE=1"

call :build_project "Build\Submodules\Flyleaf\FlyleafLib\FlyleafLib.csproj"
if errorlevel 1 set "SUBMODULE_FAILURE=1"

call :build_project "Build\Submodules\OscCore-NET9\OscCore.csproj"
if errorlevel 1 set "SUBMODULE_FAILURE=1"

call :build_project "Build\Submodules\rive-sharp\RiveSharp\RiveSharp.csproj"
if errorlevel 1 set "SUBMODULE_FAILURE=1"

echo.
if "%SUBMODULE_FAILURE%"=="0" (
    echo All submodules built successfully.
) else (
    echo One or more submodules failed to build. See errors above.
)

popd >nul 2>&1
echo.
if "%SUBMODULE_FAILURE%"=="0" (
    endlocal
    exit /b 0
) else (
    endlocal
    exit /b 1
)

:build_project
set "PROJECT_PATH=%~1"
if not exist "%PROJECT_PATH%" (
    echo ERROR: Project file "%PROJECT_PATH%" not found.
    exit /b 1
)

echo Building %PROJECT_PATH%...
dotnet build "%PROJECT_PATH%" -c %CONFIG% -p:Platform=%PLATFORM%
if errorlevel 1 (
    echo Failed to build %PROJECT_PATH%.
    exit /b 1
)
exit /b 0
