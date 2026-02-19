@echo off
REM capture_xrengine.bat
REM Captures only the XREngine.Editor window (by process name) and saves to Build\Logs\capture.png.
REM Prints pixel diagnostics.  Uses PrintWindow API so it works even if window is behind other windows.

powershell -ExecutionPolicy Bypass -File "%~dp0capture_xrengine.ps1"
