@echo off
setlocal EnableDelayedExpansion

set "REPO_ROOT=%~dp0.."
for %%I in ("%REPO_ROOT%") do set "REPO_ROOT=%%~fI"

set "EDITOR_EXE=%REPO_ROOT%\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.exe"
set "SERVER_EXE=%REPO_ROOT%\XREngine.Server\bin\Debug\net10.0-windows10.0.26100.0\XREngine.Server.exe"
set "SMOKE_DIR=%REPO_ROOT%\Build\Logs\network-smoke"

set "SESSION_ID=11111111-1111-1111-1111-111111111111"
set "SESSION_TOKEN=local-smoke-token"
set "WORLD_ID=xre-network-smoke"
set "WORLD_REVISION=rev-1"
set "WORLD_HASH=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
set "BAD_WORLD_HASH=sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
set "WORLD_SCHEMA=1"
set "WORLD_BUILD=dev"
set "SERVER_HOST=127.0.0.1"
set "SERVER_PORT=5000"
set "MULTICAST_PORT=5000"

cd /d "%REPO_ROOT%"

echo ============================================================
echo   XREngine Realtime Network Smoke Launcher
echo ============================================================
echo.
echo Usage:
echo   Start-NetworkTest.bat              ^(server + two clients^)
echo   Start-NetworkTest.bat two-clients  ^(server + two clients^)
echo   Start-NetworkTest.bat mismatch     ^(server + good client + rejected client^)
echo   Start-NetworkTest.bat pose         ^(legacy pose source + receiver flow^)
echo.

if /I "%~1"=="pose" goto :POSE_TEST

if not exist "%EDITOR_EXE%" (
    echo Editor executable not found at:
    echo   %EDITOR_EXE%
    echo Building editor first...
    dotnet build XREngine.Editor\XREngine.Editor.csproj
    if errorlevel 1 goto :BUILD_FAILED
    echo.
)

if not exist "%SERVER_EXE%" (
    echo Dedicated server executable not found at:
    echo   %SERVER_EXE%
    echo Building server first...
    dotnet build XREngine.Server\XREngine.Server.csproj
    if errorlevel 1 goto :BUILD_FAILED
    echo.
)

if not exist "%SMOKE_DIR%" mkdir "%SMOKE_DIR%"

set "GOOD_PAYLOAD=%SMOKE_DIR%\join-good.json"
set "BAD_PAYLOAD=%SMOKE_DIR%\join-bad-world.json"
call :WRITE_PAYLOAD "%GOOD_PAYLOAD%" "%WORLD_HASH%"
call :WRITE_PAYLOAD "%BAD_PAYLOAD%" "%BAD_WORLD_HASH%"

call :START_SERVER
timeout /t 2 /nobreak > nul

call :START_CLIENT "XRE Client 1" "XRE Editor (Client 1)" "%GOOD_PAYLOAD%" 5001
timeout /t 1 /nobreak > nul

if /I "%~1"=="mismatch" (
    call :START_CLIENT "XRE Client Mismatch" "XRE Editor (World Mismatch)" "%BAD_PAYLOAD%" 5002
    echo.
    echo ============================================================
    echo   Negative smoke launched.
    echo   The mismatch client should refuse realtime startup before
    echo   sending a UDP join request.
    echo ============================================================
    goto :DONE
)

call :START_CLIENT "XRE Client 2" "XRE Editor (Client 2)" "%GOOD_PAYLOAD%" 5002

echo.
echo ============================================================
echo   Positive smoke launched.
echo   Dedicated server + two clients share the same realtime
echo   session and world identity.
echo ============================================================
goto :DONE

:START_SERVER
echo Starting dedicated server...
set "XRE_SESSION_ID=%SESSION_ID%"
set "XRE_SESSION_TOKEN=%SESSION_TOKEN%"
set "XRE_WORLD_ID=%WORLD_ID%"
set "XRE_WORLD_REVISION=%WORLD_REVISION%"
set "XRE_WORLD_CONTENT_HASH=%WORLD_HASH%"
set "XRE_WORLD_ASSET_SCHEMA_VERSION=%WORLD_SCHEMA%"
set "XRE_WORLD_REQUIRED_BUILD_VERSION=%WORLD_BUILD%"
set "XRE_UDP_BIND_PORT=%SERVER_PORT%"
set "XRE_UDP_ADVERTISED_PORT=%SERVER_PORT%"
set "XRE_UDP_MULTICAST_PORT=%MULTICAST_PORT%"
start "XRE Dedicated Server" /D "%REPO_ROOT%\XREngine.Server" "%SERVER_EXE%"
exit /b

:START_CLIENT
echo Starting %~1...
set "XRE_REALTIME_JOIN_PAYLOAD_FILE=%~3"
set "XRE_WORLD_ID=%WORLD_ID%"
set "XRE_WORLD_REVISION=%WORLD_REVISION%"
set "XRE_WORLD_CONTENT_HASH=%WORLD_HASH%"
set "XRE_WORLD_ASSET_SCHEMA_VERSION=%WORLD_SCHEMA%"
set "XRE_WORLD_REQUIRED_BUILD_VERSION=%WORLD_BUILD%"
set "XRE_UDP_CLIENT_RECEIVE_PORT=%~4"
set "XRE_WINDOW_TITLE=%~2"
start "%~1" /D "%REPO_ROOT%" "%EDITOR_EXE%"
exit /b

:WRITE_PAYLOAD
> "%~1" (
    echo {
    echo   "sessionId": "%SESSION_ID%",
    echo   "sessionToken": "%SESSION_TOKEN%",
    echo   "endpoint": {
    echo     "transport": "NativeUdp",
    echo     "host": "%SERVER_HOST%",
    echo     "port": %SERVER_PORT%,
    echo     "protocolVersion": "%WORLD_BUILD%",
    echo     "metadata": {}
    echo   },
    echo   "worldAsset": {
    echo     "worldId": "%WORLD_ID%",
    echo     "revisionId": "%WORLD_REVISION%",
    echo     "contentHash": "%~2",
    echo     "assetSchemaVersion": %WORLD_SCHEMA%,
    echo     "requiredBuildVersion": "%WORLD_BUILD%",
    echo     "metadata": {}
    echo   }
    echo }
)
exit /b

:POSE_TEST
if not exist "%EDITOR_EXE%" (
    echo Editor executable not found at:
    echo   %EDITOR_EXE%
    echo Building editor first...
    dotnet build XREngine.Editor\XREngine.Editor.csproj
    if errorlevel 1 goto :BUILD_FAILED
    echo.
)

echo Starting Pose Server instance...
set "XRE_WORLD_MODE=UnitTesting"
set "XRE_UNIT_TEST_WORLD_KIND=NetworkingPose"
set "XRE_NETWORKING_POSE_ROLE=server"
set "XRE_NET_MODE=Server"
set "XRE_UDP_SERVER_BIND_PORT=%SERVER_PORT%"
set "XRE_UDP_SERVER_SEND_PORT=%SERVER_PORT%"
set "XRE_WINDOW_TITLE=XRE Editor (Pose Server)"
start "XRE Pose Server" /D "%REPO_ROOT%" "%EDITOR_EXE%"
timeout /t 2 /nobreak > nul

echo Starting Pose Source client...
set "XRE_NETWORKING_POSE_ROLE=sender"
set "XRE_NET_MODE=Client"
set "XRE_UDP_CLIENT_RECEIVE_PORT=5001"
set "XRE_POSE_ENTITY_ID=4242"
set "XRE_POSE_BROADCAST_ENABLED=1"
set "XRE_POSE_RECEIVE_ENABLED=0"
set "XRE_WINDOW_TITLE=XRE Editor (Pose Source)"
start "XRE Pose Source" /D "%REPO_ROOT%" "%EDITOR_EXE%"
timeout /t 1 /nobreak > nul

echo Starting Pose Receiver client...
set "XRE_NETWORKING_POSE_ROLE=receiver"
set "XRE_NET_MODE=Client"
set "XRE_UDP_CLIENT_RECEIVE_PORT=5002"
set "XRE_POSE_BROADCAST_ENABLED=0"
set "XRE_POSE_RECEIVE_ENABLED=1"
set "XRE_WINDOW_TITLE=XRE Editor (Pose Receiver)"
start "XRE Pose Receiver" /D "%REPO_ROOT%" "%EDITOR_EXE%"

echo.
echo ============================================================
echo   Pose sync smoke launched.
echo   Move avatar on Pose Source and verify Pose Receiver follows.
echo ============================================================
goto :DONE

:BUILD_FAILED
echo Build failed. Please fix errors before running the network smoke.
pause
exit /b 1

:DONE
echo.
if exist "%GOOD_PAYLOAD%" (
    echo Handoff payloads:
    echo   %GOOD_PAYLOAD%
    echo   %BAD_PAYLOAD%
    echo.
)
echo Press any key to exit this launcher. Started instances keep running.
pause > nul
