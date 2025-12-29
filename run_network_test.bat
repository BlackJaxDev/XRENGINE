@echo off
setlocal

REM ============================================================================
REM  XREngine Network Testing Script
REM  Launches two editor instances for server<->client networking tests.
REM ============================================================================

REM Ensure commands run from the repository root
cd /d "%~dp0"

echo ============================================================
echo   XREngine Network Test Launcher
echo ============================================================
echo.
echo This script will launch two editor instances:
echo   1. Server instance (XRE_NET_MODE=Server)
echo   2. Client instance (XRE_NET_MODE=Client)
echo.

REM Check if the editor has been built
if not exist "XREngine.Editor\bin\Debug\net8.0\XREngine.Editor.dll" (
    echo Building the editor first...
    dotnet build XREngine.Editor\XREngine.Editor.csproj
    if errorlevel 1 (
        echo Build failed! Please fix errors before running network test.
        pause
        exit /b 1
    )
    echo.
)

echo Starting Server instance...
start "XRE Server" cmd /c "cd /d "%~dp0XREngine.Editor" && set XRE_NET_MODE=Server && dotnet run --project XREngine.Editor.csproj --no-build"

REM Give the server a moment to start up
timeout /t 3 /nobreak > nul

echo Starting Client instance...
start "XRE Client" cmd /c "cd /d "%~dp0XREngine.Editor" && set XRE_NET_MODE=Client && dotnet run --project XREngine.Editor.csproj --no-build"

echo.
echo ============================================================
echo   Both instances launched!
echo   - Server window title: "XRE Server"
echo   - Client window title: "XRE Client"
echo ============================================================
echo.
echo Press any key to exit this launcher (instances will keep running)...
pause > nul
