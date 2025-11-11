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

set "MSBUILD_EXE="

call :ensure_dotnet
if errorlevel 1 (
    echo ERROR: .NET SDK not detected. Install it from https://aka.ms/dotnet/download and retry.
    set "SUBMODULE_FAILURE=1"
) else (
    call :build_project "Build\Submodules\OpenVR.NET\OpenVR.NET\OpenVR.NET.csproj"
    if errorlevel 1 set "SUBMODULE_FAILURE=1"

    call :build_project "Build\Submodules\Flyleaf\FlyleafLib\FlyleafLib.csproj"
    if errorlevel 1 set "SUBMODULE_FAILURE=1"

    call :build_project "Build\Submodules\OscCore-NET9\OscCore.csproj"
    if errorlevel 1 set "SUBMODULE_FAILURE=1"

    call :build_rivesharp_managed
    if errorlevel 1 set "SUBMODULE_FAILURE=1"
)

if "%SUBMODULE_FAILURE%"=="0" (
    call :ensure_premake
    if errorlevel 1 (
        set "SUBMODULE_FAILURE=1"
    ) else (
        call :build_rive_native
        if errorlevel 1 set "SUBMODULE_FAILURE=1"
    )
)

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

:ensure_dotnet
set "DOTNET_SDK_FOUND="
for /f "delims=" %%I in ('dotnet --list-sdks 2^>nul') do (
    set "DOTNET_SDK_FOUND=1"
    goto dotnet_found
)
dotnet --version >nul 2>&1
if errorlevel 1 exit /b 1
if not defined DOTNET_SDK_FOUND exit /b 1

:dotnet_found
exit /b 0

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

:ensure_msbuild
if not "%MSBUILD_EXE%"=="" exit /b 0
for /f "delims=" %%I in ('where msbuild 2^>nul') do (
    set "MSBUILD_EXE=%%I"
    goto msbuild_found
)
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"

:msbuild_found
if "%MSBUILD_EXE%"=="" (
    echo ERROR: MSBuild not found. Install Visual Studio Build Tools with the "Desktop development with C++" workload.
    exit /b 1
)
exit /b 0

:ensure_vctargets
if defined VCTargetsPath if exist "%VCTargetsPath%Microsoft.Cpp.Default.props" exit /b 0

set "VCTargetsPath="
for %%T in (
    "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VC\v170\"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VC\v170\"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VC\v170\"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VC\v170\"
    "%ProgramFiles%\Microsoft Visual Studio\2026\BuildTools\MSBuild\Microsoft\VC\v180\"
    "%ProgramFiles%\Microsoft Visual Studio\2026\Community\MSBuild\Microsoft\VC\v180\"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2026\BuildTools\MSBuild\Microsoft\VC\v180\"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2026\Community\MSBuild\Microsoft\VC\v180\"
    "%ProgramFiles%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Microsoft\VC\v142\"
    "%ProgramFiles%\Microsoft Visual Studio\2019\Community\MSBuild\Microsoft\VC\v142\"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Microsoft\VC\v142\"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Microsoft\VC\v142\"
) do (
    if exist "%%~T\Microsoft.Cpp.Default.props" (
        set "VCTargetsPath=%%~T"
        goto vctargets_found
    )
)

if "%VCTargetsPath%"=="" (
    for /f "delims=" %%I in ('dir /s /b "%ProgramFiles%\Microsoft Visual Studio\*\MSBuild\Microsoft\VC\*\Microsoft.Cpp.Default.props" 2^>nul') do (
        set "VCTargetsPath=%%~dpI"
        goto vctargets_found
    )
)

if "%VCTargetsPath%"=="" (
    for /f "delims=" %%I in ('dir /s /b "%ProgramFiles(x86)%\Microsoft Visual Studio\*\MSBuild\Microsoft\VC\*\Microsoft.Cpp.Default.props" 2^>nul') do (
        set "VCTargetsPath=%%~dpI"
        goto vctargets_found
    )
)

:vctargets_found
if "%VCTargetsPath%"=="" (
    echo ERROR: Microsoft.Cpp.Default.props not found. Install Visual Studio Build Tools with the "Desktop development with C++" workload.
    exit /b 1
)

set "VCTargetsPath=%VCTargetsPath%"
exit /b 0

:build_rivesharp_managed
call :ensure_msbuild
if errorlevel 1 (
    echo Skipping RiveSharp managed build due to missing MSBuild.
    exit /b 1
)

call :ensure_vctargets
if errorlevel 1 (
    echo ERROR: Required Visual C++ build tools are missing.
    exit /b 1
)

set "RIVESHARP_CSPROJ=%REPO_ROOT%Build\Submodules\rive-sharp\RiveSharp\RiveSharp.csproj"
if not exist "%RIVESHARP_CSPROJ%" (
    echo ERROR: RiveSharp project file not found at "%RIVESHARP_CSPROJ%".
    exit /b 1
)

echo Building %RIVESHARP_CSPROJ% with MSBuild...
"%MSBUILD_EXE%" "%RIVESHARP_CSPROJ%" /p:Configuration=%CONFIG% /p:Platform=%PLATFORM%
if errorlevel 1 (
    echo Failed to build RiveSharp managed project.
    exit /b 1
)
exit /b 0

:ensure_premake
set "PREMAKE_EXE="
for /f "delims=" %%I in ('where premake5 2^>nul') do (
    set "PREMAKE_EXE=%%I"
    goto premake_found
)
if exist "%REPO_ROOT%Build\Tools\premake5.exe" (
    set "PREMAKE_EXE=%REPO_ROOT%Build\Tools\premake5.exe"
    goto premake_found
)

echo Premake not found. Installing local copy...
set "PREMAKE_DIR=%REPO_ROOT%Build\Tools"
if not exist "%PREMAKE_DIR%" mkdir "%PREMAKE_DIR%"

set "PREMAKE_ZIP=%PREMAKE_DIR%\premake.zip"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri 'https://github.com/premake/premake-core/releases/download/v5.0.0-beta2/premake-5.0.0-beta2-windows.zip' -OutFile '%PREMAKE_ZIP%'" || (
    echo ERROR: Failed to download Premake.
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -Path '%PREMAKE_ZIP%' -DestinationPath '%PREMAKE_DIR%' -Force" || (
    echo ERROR: Failed to extract Premake.
    exit /b 1
)
del /q "%PREMAKE_ZIP%" >nul 2>&1

for /f "delims=" %%I in ('dir /s /b "%PREMAKE_DIR%\premake5.exe" 2^>nul') do (
    set "PREMAKE_EXE=%%I"
    goto premake_found
)

echo ERROR: Premake installation failed.
exit /b 1

:premake_found
if "%PREMAKE_EXE%"=="" (
    echo ERROR: Unable to locate premake5.exe.
    exit /b 1
)
exit /b 0

:build_rive_native
call :ensure_msbuild
if errorlevel 1 (
    echo Skipping Rive native build due to missing MSBuild.
    exit /b 1
)

call :ensure_vctargets
if errorlevel 1 (
    echo ERROR: Required Visual C++ build tools are missing. Install the "Desktop development with C++" workload.
    exit /b 1
)

set "RIVE_NATIVE_DIR=%REPO_ROOT%Build\Submodules\rive-sharp\native"
if not exist "%RIVE_NATIVE_DIR%" (
    echo WARNING: Rive native directory not found. Skipping native build.
    exit /b 0
)

set "RIVE_PLATFORM=x64"
if /I "%PLATFORM%"=="ANYCPU" set "RIVE_PLATFORM=x64"
if /I "%PLATFORM%"=="X64" set "RIVE_PLATFORM=x64"

echo Generating Rive native solution with Premake...
pushd "%RIVE_NATIVE_DIR%" >nul 2>&1
"%PREMAKE_EXE%" vs2022
if errorlevel 1 (
    echo ERROR: Premake failed to generate the Rive native solution.
    popd >nul 2>&1
    exit /b 1
)

set "RIVE_SOLUTION="
for %%I in ("%RIVE_NATIVE_DIR%\rive.sln" "%RIVE_NATIVE_DIR%\rive-cpp.sln") do (
    if exist "%%~I" (
        set "RIVE_SOLUTION=%%~I"
        goto have_rive_solution
    )
)

echo ERROR: Rive native solution was not created (expected rive.sln or rive-cpp.sln).
popd >nul 2>&1
exit /b 1

:have_rive_solution
echo Building Rive native library (Configuration=%CONFIG%, Platform=%RIVE_PLATFORM%)...
"%MSBUILD_EXE%" "%RIVE_SOLUTION%" /p:Configuration=%CONFIG% /p:Platform=%RIVE_PLATFORM%
if errorlevel 1 (
    echo ERROR: Failed to build the Rive native library.
    popd >nul 2>&1
    exit /b 1
)
popd >nul 2>&1
exit /b 0
