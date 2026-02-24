@echo off
setlocal

set "REPO_ROOT=%~dp0.."
set "EDITOR_EXE=%REPO_ROOT%\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.exe"
cd /d "%REPO_ROOT%"

echo ============================================================
echo   XREngine Network Test Launcher
echo ============================================================
echo.
echo Usage:
echo   run_network_test.bat           ^(server + client^)
echo   run_network_test.bat pose      ^(server + pose source + pose receiver^)
echo.

if not exist "%EDITOR_EXE%" (
    echo Editor executable not found at:
    echo   %EDITOR_EXE%
    echo Building editor first...
    dotnet build XREngine.Editor\XREngine.Editor.csproj
    if errorlevel 1 (
        echo Build failed! Please fix errors before running network test.
        pause
        exit /b 1
    )
    echo.
)

if /I "%~1"=="pose" goto :POSE_TEST

echo Starting Server instance...
start "XRE Server" cmd /c "set XRE_NET_MODE=Server && set XRE_WINDOW_TITLE=XRE Editor (Server) && start \"\" /D "%REPO_ROOT%" "%EDITOR_EXE%""
timeout /t 2 /nobreak > nul

echo Starting Client instance...
start "XRE Client" cmd /c "set XRE_NET_MODE=Client && set XRE_UDP_CLIENT_RECEIVE_PORT=5001 && set XRE_WINDOW_TITLE=XRE Editor (Client 1) && start \"\" /D "%REPO_ROOT%" "%EDITOR_EXE%""

echo.
echo ============================================================
echo   Server + client launched.
echo ============================================================
goto :DONE

:POSE_TEST
echo Starting Server instance...
start "XRE Server" cmd /c "set XRE_WORLD_MODE=UnitTesting && set XRE_UNIT_TEST_WORLD_KIND=NetworkingPose && set XRE_NETWORKING_POSE_ROLE=server && set XRE_NET_MODE=Server && set XRE_WINDOW_TITLE=XRE Editor (Pose Server) && start \"\" /D "%REPO_ROOT%" "%EDITOR_EXE%""
timeout /t 2 /nobreak > nul

echo Starting Pose Source client...
start "XRE Pose Source" cmd /c "set XRE_WORLD_MODE=UnitTesting && set XRE_UNIT_TEST_WORLD_KIND=NetworkingPose && set XRE_NETWORKING_POSE_ROLE=sender && set XRE_NET_MODE=Client && set XRE_UDP_CLIENT_RECEIVE_PORT=5001 && set XRE_POSE_ENTITY_ID=4242 && set XRE_POSE_BROADCAST_ENABLED=1 && set XRE_POSE_RECEIVE_ENABLED=0 && set XRE_WINDOW_TITLE=XRE Editor (Pose Source) && start \"\" /D "%REPO_ROOT%" "%EDITOR_EXE%""
timeout /t 1 /nobreak > nul

echo Starting Pose Receiver client...
start "XRE Pose Receiver" cmd /c "set XRE_WORLD_MODE=UnitTesting && set XRE_UNIT_TEST_WORLD_KIND=NetworkingPose && set XRE_NETWORKING_POSE_ROLE=receiver && set XRE_NET_MODE=Client && set XRE_UDP_CLIENT_RECEIVE_PORT=5002 && set XRE_POSE_ENTITY_ID=4242 && set XRE_POSE_BROADCAST_ENABLED=0 && set XRE_POSE_RECEIVE_ENABLED=1 && set XRE_WINDOW_TITLE=XRE Editor (Pose Receiver) && start \"\" /D "%REPO_ROOT%" "%EDITOR_EXE%""

echo.
echo ============================================================
echo   Pose sync test launched.
echo   Move avatar on Pose Source and verify Pose Receiver follows.
echo ============================================================

:DONE
echo.
echo Press any key to exit this launcher (instances will keep running)...
pause > nul
