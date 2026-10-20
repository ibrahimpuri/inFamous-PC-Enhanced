@echo off
rem The launcher's "Update now": updates the port like "Update inFamous" does, then puts the launcher's port patches
rem back (NVIDIA DLSS, PlayStation / Steam controllers, the cheat mods). An update or repair restores the port's own
rem copies of the files those patches change, so without this step they would silently disappear.
cd /d "%~dp0..\.."
"installer\infamous-installer.exe" update
echo.
echo ==^> Re-applying the launcher's port patches
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0apply-dlss.ps1"
echo.
pause
