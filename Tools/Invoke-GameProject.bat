@echo off
setlocal EnableExtensions

REM -----------------------------------------------------------------------------
REM Helper script for common game-project workflows:
REM   compile / build / run / buildrun in dev or publish mode.
REM -----------------------------------------------------------------------------

set "REPO_ROOT=%~dp0.."
set "HELP_ONLY=0"
pushd "%REPO_ROOT%" >nul 2>&1
if errorlevel 1 (
    echo ERROR: Failed to switch to repository root "%REPO_ROOT%".
    endlocal
    exit /b 1
)

if "%~1"=="" goto :usage
if /I "%~1"=="help" set "HELP_ONLY=1" & goto :usage
if /I "%~1"=="--help" set "HELP_ONLY=1" & goto :usage
if /I "%~1"=="/?" set "HELP_ONLY=1" & goto :usage

set "ACTION=%~1"
set "MODE=%~2"
set "PROJECT_PATH_ARG=%~3"

if "%MODE%"=="" (
    echo ERROR: Missing mode. Use "dev" or "publish".
    goto :usage_error
)

if "%PROJECT_PATH_ARG%"=="" (
    echo ERROR: Missing project path. Provide a .xrproj file path.
    goto :usage_error
)

call :normalize_action "%ACTION%"
if errorlevel 1 (
    echo ERROR: Unsupported action "%ACTION%".
    goto :usage_error
)

call :normalize_mode "%MODE%"
if errorlevel 1 (
    echo ERROR: Unsupported mode "%MODE%".
    goto :usage_error
)

for %%I in ("%PROJECT_PATH_ARG%") do set "PROJECT_PATH=%%~fI"
if not exist "%PROJECT_PATH%" (
    echo ERROR: Project file not found: "%PROJECT_PATH%".
    goto :fail
)

set "EDITOR_CSPROJ=%REPO_ROOT%\XREngine.Editor\XREngine.Editor.csproj"
if not exist "%EDITOR_CSPROJ%" (
    echo ERROR: Editor project not found: "%EDITOR_CSPROJ%".
    goto :fail
)

REM Defaults that can be overridden by optional arguments.
set "BUILD_PLATFORM=Windows64"
if /I "%MODE_NORM%"=="dev" (
    set "BUILD_CONFIG=Development"
    set "OUTPUT_SUBFOLDER=Game"
    set "PUBLISH_NATIVE_AOT=false"
    set "RUN_MODE=dev"
) else (
    set "BUILD_CONFIG=Release"
    set "OUTPUT_SUBFOLDER=Publish"
    set "PUBLISH_NATIVE_AOT=true"
    set "RUN_MODE=publish"
)
set "LAUNCHER_NAME=Game.exe"

shift
shift
shift

:parse_args
if "%~1"=="" goto :args_done
if /I "%~1"=="--output-subfolder" goto :arg_output
if /I "%~1"=="--launcher-name" goto :arg_launcher
if /I "%~1"=="--build-platform" goto :arg_platform
if /I "%~1"=="--build-configuration" goto :arg_config
if /I "%~1"=="--publish-native-aot" goto :arg_nativeaot

echo ERROR: Unknown option "%~1".
goto :usage_error

:arg_output
if "%~2"=="" (
    echo ERROR: Missing value after --output-subfolder.
    goto :usage_error
)
set "OUTPUT_SUBFOLDER=%~2"
shift
shift
goto :parse_args

:arg_launcher
if "%~2"=="" (
    echo ERROR: Missing value after --launcher-name.
    goto :usage_error
)
set "LAUNCHER_NAME=%~2"
shift
shift
goto :parse_args

:arg_platform
if "%~2"=="" (
    echo ERROR: Missing value after --build-platform.
    goto :usage_error
)
set "BUILD_PLATFORM=%~2"
shift
shift
goto :parse_args

:arg_config
if "%~2"=="" (
    echo ERROR: Missing value after --build-configuration.
    goto :usage_error
)
set "BUILD_CONFIG=%~2"
shift
shift
goto :parse_args

:arg_nativeaot
if "%~2"=="" (
    echo ERROR: Missing value after --publish-native-aot.
    goto :usage_error
)
set "PUBLISH_NATIVE_AOT=%~2"
shift
shift
goto :parse_args

:args_done
if /I not "%ACTION_NORM%"=="run" (
    call :run_build_step
    if errorlevel 1 goto :fail
)

if /I "%ACTION_NORM%"=="compile" goto :success
if /I "%ACTION_NORM%"=="build" goto :success

call :run_launch_step
if errorlevel 1 goto :fail
goto :success

:run_build_step
if /I "%ACTION_NORM%"=="compile" (
    echo Compiling managed game code ^(%BUILD_CONFIG%^|%BUILD_PLATFORM%^)...
    dotnet run --project "%EDITOR_CSPROJ%" -c Debug -p:Platform=AnyCPU -- --build-project-code "%PROJECT_PATH%" --build-configuration "%BUILD_CONFIG%" --build-platform "%BUILD_PLATFORM%"
    exit /b %ERRORLEVEL%
)

echo Building game launcher ^(%BUILD_CONFIG%^|%BUILD_PLATFORM%^)...
dotnet run --project "%EDITOR_CSPROJ%" -c Debug -p:Platform=AnyCPU -- --build-project "%PROJECT_PATH%" --build-configuration "%BUILD_CONFIG%" --build-platform "%BUILD_PLATFORM%" --output-subfolder "%OUTPUT_SUBFOLDER%" --launcher-name "%LAUNCHER_NAME%" --publish-native-aot "%PUBLISH_NATIVE_AOT%"
exit /b %ERRORLEVEL%

:run_launch_step
for %%I in ("%PROJECT_PATH%") do set "PROJECT_DIR=%%~dpI"
set "LAUNCHER_EXE=%LAUNCHER_NAME%"
for %%I in ("%LAUNCHER_EXE%") do if /I "%%~xI"=="" set "LAUNCHER_EXE=%LAUNCHER_EXE%.exe"
set "EXE_PATH=%PROJECT_DIR%Build\%OUTPUT_SUBFOLDER%\Binaries\%LAUNCHER_EXE%"

if not exist "%EXE_PATH%" (
    echo ERROR: Launcher executable not found:
    echo   %EXE_PATH%
    echo Build first with action "build" or "buildrun".
    exit /b 1
)

echo Running %EXE_PATH% --mode %RUN_MODE%
"%EXE_PATH%" --mode %RUN_MODE%
exit /b %ERRORLEVEL%

:normalize_action
set "ACTION_NORM=%~1"
if /I "%ACTION_NORM%"=="c" set "ACTION_NORM=compile"
if /I "%ACTION_NORM%"=="b" set "ACTION_NORM=build"
if /I "%ACTION_NORM%"=="r" set "ACTION_NORM=run"
if /I "%ACTION_NORM%"=="br" set "ACTION_NORM=buildrun"

if /I "%ACTION_NORM%"=="compile" exit /b 0
if /I "%ACTION_NORM%"=="build" exit /b 0
if /I "%ACTION_NORM%"=="run" exit /b 0
if /I "%ACTION_NORM%"=="buildrun" exit /b 0
exit /b 1

:normalize_mode
set "MODE_NORM=%~1"
if /I "%MODE_NORM%"=="d" set "MODE_NORM=dev"
if /I "%MODE_NORM%"=="p" set "MODE_NORM=publish"

if /I "%MODE_NORM%"=="dev" exit /b 0
if /I "%MODE_NORM%"=="publish" exit /b 0
exit /b 1

:usage_error
echo.
:usage
echo Usage:
echo   %~nx0 ^<action^> ^<mode^> ^<project.xrproj^> [options]
echo.
echo Actions:
echo   compile ^| c   Compile managed game code only.
echo   build   ^| b   Build cooked game output and launcher.
echo   run     ^| r   Run an existing launcher executable.
echo   buildrun^| br  Build then run launcher.
echo.
echo Modes:
echo   dev     ^| d   Defaults: Development, output "Game", runtime mode "dev".
echo   publish ^| p   Defaults: Release, output "Publish", runtime mode "publish", AOT true.
echo.
echo Options:
echo   --output-subfolder ^<name^>   Build output subfolder override.
echo   --launcher-name ^<name^>      Launcher executable name ^(default Game.exe^).
echo   --build-platform ^<name^>     Build platform override ^(default Windows64^).
echo   --build-configuration ^<name^> Build config override.
echo   --publish-native-aot ^<bool^>  Override AOT publish flag.
echo.
echo Examples:
echo   %~nx0 build dev "D:\MyGame\GameProject\MyGame.xrproj"
echo   %~nx0 buildrun publish "D:\MyGame\GameProject\MyGame.xrproj"
echo   %~nx0 run dev "D:\MyGame\GameProject\MyGame.xrproj" --output-subfolder Game
echo.
if "%HELP_ONLY%"=="1" goto :success
goto :fail

:success
popd >nul 2>&1
endlocal
exit /b 0

:fail
popd >nul 2>&1
endlocal
exit /b 1
