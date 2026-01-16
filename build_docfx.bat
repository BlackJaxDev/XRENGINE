@echo off
setlocal

REM Build the DocFX site
dotnet tool restore
if errorlevel 1 (
  echo Failed to restore dotnet tools.
  exit /b 1
)

dotnet docfx "docs\docfx\docfx.json"
if errorlevel 1 (
  echo DocFX build failed.
  exit /b 1
)

echo DocFX build complete. Output: docs\docfx\_site
endlocal
