@echo off
setlocal

set "CONFIG_DIR=%LOCALAPPDATA%\XREngine\Sandbox\Config"
set "ENGINE_SETTINGS=%CONFIG_DIR%\engine_settings.asset"
set "USER_SETTINGS=%CONFIG_DIR%\user_settings.asset"
set "BUILD_SETTINGS=%CONFIG_DIR%\build_settings.asset"

call :DeleteFile "%ENGINE_SETTINGS%" "Sandbox editor preferences overrides"
call :DeleteFile "%USER_SETTINGS%" "Sandbox user settings"
call :DeleteFile "%BUILD_SETTINGS%" "Sandbox build settings"

endlocal
exit /b 0

:DeleteFile
set "TARGET=%~1"
set "LABEL=%~2"
if exist "%TARGET%" (
    echo Deleting %LABEL%: "%TARGET%"
    del /f /q "%TARGET%"
) else (
    echo %LABEL% not found: "%TARGET%"
)
exit /b 0
