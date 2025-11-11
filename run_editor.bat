@echo off
setlocal

REM Ensure commands run from the repository root
cd /d "%~dp0"

REM Launch the Editor project from its directory so relative paths resolve correctly
pushd XREngine.Editor
dotnet run --project XREngine.Editor.csproj %*
popd
