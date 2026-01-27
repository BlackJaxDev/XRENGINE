@echo off
setlocal

REM Change to repository root (parent of Tools directory)
cd /d "%~dp0.."

REM Restore tools and serve DocFX site on port 8080
dotnet tool restore
if errorlevel 1 (
  echo Failed to restore dotnet tools.
  exit /b 1
)

dotnet docfx "docs\docfx\docfx.json" --serve --port 8080
if errorlevel 1 (
  echo DocFX serve failed.
  exit /b 1
)

endlocal
