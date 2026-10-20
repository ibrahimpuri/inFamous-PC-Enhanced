@echo off
rem Builds "inFamous Launcher.exe" next to the game (one folder up) with the C# compiler from the
rem Visual Studio Build Tools the port already needs. Target: .NET Framework 4.8, which ships with Windows 10/11.
setlocal
cd /d "%~dp0"
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC="
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -find MSBuild\**\Roslyn\csc.exe 2^>nul`) do if not defined CSC set "CSC=%%i"
if not defined CSC if exist "%FW%\csc.exe" (
  echo Roslyn compiler not found; the .NET Framework compiler only supports C# 5 and cannot build this.
  exit /b 1
)
if not defined CSC ( echo No C# compiler found. Install Visual Studio Build Tools. & exit /b 1 )
if not exist assets\icon.ico powershell -NoProfile -ExecutionPolicy Bypass -File make_icon.ps1
rem A running launcher (the one that started an update) can't be overwritten, but it can be renamed out of the way.
if exist "..\inFamous Launcher.old" del /q "..\inFamous Launcher.old" 2>nul
if exist "..\inFamous Launcher.exe" ren "..\inFamous Launcher.exe" "inFamous Launcher.old"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:latest /nowarn:1701,1702 ^
  /out:"..\inFamous Launcher.exe" /win32icon:assets\icon.ico /win32manifest:src\app.manifest ^
  /resource:src\Theme.xaml,InfamousLauncher.Theme.xaml /resource:assets\icon.ico,InfamousLauncher.icon.ico ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll" ^
  src\*.cs
if errorlevel 1 (
  if exist "..\inFamous Launcher.old" if not exist "..\inFamous Launcher.exe" ren "..\inFamous Launcher.old" "inFamous Launcher.exe"
  exit /b 1
)
del /q "..\inFamous Launcher.old" >nul 2>&1
echo Built "%~dp0..\inFamous Launcher.exe"
