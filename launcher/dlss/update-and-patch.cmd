@echo off
rem The launcher's "Update now": updates the port like "Update inFamous" does, puts the launcher's port patches back
rem (an install that tracks upstream gets the port's own copies of the patched files back with every update), and
rem rebuilds the launcher itself.
cd /d "%~dp0..\.."
"installer\infamous-installer.exe" update
echo.
echo ==^> Re-applying the launcher's port patches
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0apply-dlss.ps1"
echo.
echo ==^> Rebuilding the launcher
call "%~dp0..\build.cmd"
echo.
echo Done. Close and reopen the launcher to use its new version.
pause
