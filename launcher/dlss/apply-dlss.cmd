@echo off
rem Puts NVIDIA DLSS (DLAA) back into the game after "Update inFamous" and rebuilds it. See README.md here.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0apply-dlss.ps1" %*
echo.
pause
