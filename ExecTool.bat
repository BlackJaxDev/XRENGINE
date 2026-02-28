@echo off
setlocal EnableDelayedExpansion

REM ============================================================================
REM  ExecTool.bat  -  Interactive launcher for all Tools/ scripts in XRENGINE
REM ============================================================================
REM  Usage:
REM    ExecTool              (interactive menu)
REM    ExecTool <number>     (run tool by number directly)
REM    ExecTool --list       (print tool list and exit)
REM    ExecTool --help       (show help)
REM ============================================================================

cd /d "%~dp0"

REM ── Define all tools ──────────────────────────────────────────────────────
REM Format: TOOL_<N>_CMD=<command>  TOOL_<N>_DESC=<description>

set "TOOL_COUNT=0"

call :AddTool "Build" "Tools\Build-DocFx.bat" "Build the DocFX API documentation site"
call :AddTool "Build" "Tools\Build-Submodules.bat" "Compile third-party submodules (Debug by default)"
call :AddTool "Build" "Tools\Test-CookCommonAssets.ps1" "Cook CommonAssets into a .pak archive (builds editor first)"
call :AddSep
call :AddTool "Editor" "Tools\Start-Editor.bat" "Launch the XREngine Editor via dotnet run"
call :AddTool "Editor" "Tools\Start-NetworkTest.bat" "Launch a network test (server+client or pose sync)"
call :AddTool "Editor" "Tools\Capture-EditorWindow.bat" "Capture a screenshot of the Editor window to Build\Logs\capture.png"
call :AddSep
call :AddTool "Repo" "Tools\Initialize-Submodules.bat" "Initialize and update all git submodules recursively"
call :AddTool "Repo" "Tools\Reset-GlobalConfig.bat" "Delete global editor preferences (factory reset)"
call :AddTool "Repo" "Tools\Reset-SandboxConfig.bat" "Delete sandbox config (engine_settings, user_settings, build_settings)"
call :AddSep
call :AddTool "Docs" "Tools\Start-DocFxServer.bat" "Build and serve DocFX docs locally on port 8080"
call :AddTool "Docs" "Tools\Invoke-GameProject.bat" "Helper for game-project workflows (compile/build/run/publish)"
call :AddSep
call :AddTool "Reports" "Tools\Reports\Find-BuildWarnings.ps1" "Build solution and generate categorised warning report (docs\work\audit\warnings.md)"
call :AddTool "Reports" "Tools\Reports\Find-NewAllocations.ps1" "Scan C# sources for heap allocations (new ...) in engine code"
call :AddTool "Reports" "Tools\Reports\Find-StaticClasses.ps1" "Find static classes across the codebase"
call :AddTool "Reports" "Tools\Reports\Find-ThreadTaskRuns.ps1" "Find Thread/Task.Run usage across the codebase"
call :AddTool "Reports" "Tools\Reports\Find-XRBaseDirectFieldAssignments.ps1" "Find direct field assignments in XRBase-derived types"
call :AddTool "Reports" "Tools\Reports\Generate-Dependencies.ps1" "Regenerate docs\DEPENDENCIES.md and license audit"
call :AddTool "Reports" "Tools\Reports\generate_mcp_docs.ps1" "Regenerate MCP tool table in docs\features\mcp-server.md"
call :AddSep
call :AddTool "Deps" "Tools\Dependencies\Get-CoACD.ps1" "Download CoACD convex decomposition native library"
call :AddTool "Deps" "Tools\Dependencies\Build-CoACD.ps1" "Build CoACD from source (requires CMake + C++ compiler)"
call :AddTool "Deps" "Tools\Dependencies\Get-FfmpegFromFlyleaf.ps1" "Download FFmpeg DLLs from Flyleaf GitHub repo"
call :AddTool "Deps" "Tools\Dependencies\Get-NvComp.ps1" "Download NVIDIA nvCOMP compression native binaries"
call :AddTool "Deps" "Tools\Dependencies\Get-Phonon.ps1" "Download Steam Audio (Phonon) native library"
call :AddTool "Deps" "Tools\Dependencies\Get-UltralightResources.ps1" "Download Ultralight runtime resources (icudt67l.dat, cacert.pem)"
call :AddTool "Deps" "Tools\Dependencies\Get-YtDlp.ps1" "Download yt-dlp for YouTube URL extraction"

REM ── Handle arguments ──────────────────────────────────────────────────────
if "%~1"=="--help" goto :ShowHelp
if "%~1"=="-h"     goto :ShowHelp
if "%~1"=="/?"     goto :ShowHelp
if "%~1"=="--list" goto :ShowList

if not "%~1"=="" (
    set /a "SEL=%~1" 2>nul
    if !SEL! GEQ 1 if !SEL! LEQ %TOOL_COUNT% goto :RunTool
    echo Invalid tool number: %~1
    echo Run ExecTool --list to see available tools.
    exit /b 1
)

REM ── Interactive menu ──────────────────────────────────────────────────────
:Menu
echo.
echo  ============================================================
echo   XRENGINE Tool Launcher
echo  ============================================================
echo.

set "LAST_CAT="
for /L %%i in (1,1,%TOOL_COUNT%) do (
    if "!TOOL_%%i_CMD!"=="" (
        echo.
    ) else (
        if not "!TOOL_%%i_CAT!"=="!LAST_CAT!" (
            echo  --- !TOOL_%%i_CAT! ---
            set "LAST_CAT=!TOOL_%%i_CAT!"
        )
        if %%i LSS 10 (
            echo    %%i.  !TOOL_%%i_DESC!
        ) else (
            echo   %%i.  !TOOL_%%i_DESC!
        )
        echo        !TOOL_%%i_CMD!
    )
)

echo.
echo   0.  Exit
echo.

set /p "SEL=  Select tool [0-%TOOL_COUNT%]: "

if "%SEL%"=="0" goto :End
if "%SEL%"==""  goto :End

set /a "SEL=%SEL%" 2>nul
if %SEL% LSS 1 (
    echo  Invalid selection.
    goto :Menu
)
if %SEL% GTR %TOOL_COUNT% (
    echo  Invalid selection.
    goto :Menu
)
if "!TOOL_%SEL%_CMD!"=="" (
    echo  That entry is a separator, not a tool.
    goto :Menu
)

:RunTool
echo.
echo  Running: !TOOL_%SEL%_CMD!
echo  ============================================================
echo.

set "CMD=!TOOL_%SEL%_CMD!"

REM Route based on extension
if /I "!CMD:~-4!"==".ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "!CMD!"
) else if /I "!CMD:~-4!"==".bat" (
    call "!CMD!"
) else (
    echo  Unknown script type: !CMD!
    exit /b 1
)

echo.
echo  ============================================================
echo  Tool finished with exit code: %ERRORLEVEL%
echo  ============================================================

if "%~1"=="" (
    echo.
    pause
    goto :Menu
)
goto :End

REM ── Help ──────────────────────────────────────────────────────────────────
:ShowHelp
echo.
echo  ExecTool.bat - Interactive launcher for XRENGINE Tools/ scripts
echo.
echo  Usage:
echo    ExecTool              Launch interactive menu
echo    ExecTool ^<number^>     Run a specific tool by its menu number
echo    ExecTool --list       Print the tool list and exit
echo    ExecTool --help       Show this help
echo.
echo  All scripts live under the Tools/ directory. Reports are written
echo  to docs/work/audit/. Dependency installers place binaries under
echo  Build/Dependencies/ or the appropriate runtimes/ folder.
echo.
goto :End

REM ── List ──────────────────────────────────────────────────────────────────
:ShowList
echo.
echo  XRENGINE Tools
echo  ============================================================

set "LAST_CAT="
for /L %%i in (1,1,%TOOL_COUNT%) do (
    if "!TOOL_%%i_CMD!"=="" (
        echo.
    ) else (
        if not "!TOOL_%%i_CAT!"=="!LAST_CAT!" (
            echo.
            echo  [!TOOL_%%i_CAT!]
            set "LAST_CAT=!TOOL_%%i_CAT!"
        )
        if %%i LSS 10 (
            echo    %%i. !TOOL_%%i_DESC!
        ) else (
            echo   %%i. !TOOL_%%i_DESC!
        )
        echo       !TOOL_%%i_CMD!
    )
)
echo.
goto :End

REM ── Helpers ───────────────────────────────────────────────────────────────
:AddTool
set /a "TOOL_COUNT+=1"
set "TOOL_%TOOL_COUNT%_CAT=%~1"
set "TOOL_%TOOL_COUNT%_CMD=%~2"
set "TOOL_%TOOL_COUNT%_DESC=%~3"
exit /b

:AddSep
set /a "TOOL_COUNT+=1"
set "TOOL_%TOOL_COUNT%_CAT="
set "TOOL_%TOOL_COUNT%_CMD="
set "TOOL_%TOOL_COUNT%_DESC="
exit /b

:End
endlocal
exit /b 0
