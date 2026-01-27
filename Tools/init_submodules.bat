@echo off
echo Initializing XRENGINE repository submodules...
echo.

REM Change to repository root (parent of Tools directory)
cd /d "%~dp0.."

REM Synchronize local config with .gitmodules in case remote URLs changed
echo Syncing submodule configuration...
git submodule sync --recursive
if %ERRORLEVEL% neq 0 (
    echo ERROR: Failed to sync submodule configuration
    pause
    exit /b 1
)

REM Initialize and update all submodules to their tracked commits
echo Initializing and updating submodules...
git submodule update --init --recursive
if %ERRORLEVEL% neq 0 (
    echo ERROR: Failed to update submodules
    pause
    exit /b 1
)

echo.
echo Submodules initialized and updated successfully!
echo.
echo Submodule status:
git submodule status
