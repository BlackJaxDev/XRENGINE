@echo off
setlocal

REM Change to repository root (parent of Tools directory)
cd /d "%~dp0.."

REM Build the DocFX site
dotnet tool restore
set "TOOL_RESTORE_EXIT=%ERRORLEVEL%"
if not "%TOOL_RESTORE_EXIT%"=="0" (
  echo Failed to restore dotnet tools.
  exit /b %TOOL_RESTORE_EXIT%
)

dotnet docfx "docs\docfx\docfx.json"
set "DOCFX_EXIT=%ERRORLEVEL%"
if not "%DOCFX_EXIT%"=="0" (
  echo DocFX build failed.
  exit /b %DOCFX_EXIT%
)

echo DocFX build complete. Output: docs\docfx\_site
endlocal
