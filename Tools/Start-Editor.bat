@echo off
setlocal

REM Change to repository root (parent of Tools directory)
cd /d "%~dp0.."

REM Launch the Editor project from its directory so relative paths resolve correctly
pushd XREngine.Editor
dotnet run --project XREngine.Editor.csproj %*
popd
