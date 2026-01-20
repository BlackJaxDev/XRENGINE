@echo off
setlocal

set "CONFIG_DIR=%LOCALAPPDATA%\XREngine\Global\Config"
set "PREFS_FILE=%CONFIG_DIR%\editor_preferences_global.asset"

if exist "%PREFS_FILE%" (
    echo Deleting global editor preferences: "%PREFS_FILE%"
    del /f /q "%PREFS_FILE%"
) else (
    echo Global editor preferences not found: "%PREFS_FILE%"
)

endlocal
