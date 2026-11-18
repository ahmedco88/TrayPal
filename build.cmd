@echo off
rem Builds TrayPal.exe (and Setup.exe if Inno Setup 6 is installed). No other installs needed.
setlocal
cd /d "%~dp0"
if not exist dist mkdir dist
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:assets\TrayPal.ico /out:dist\TrayPal.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll src\*.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built dist\TrayPal.exe

set ISCC=
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if "%ISCC%"=="" (echo Inno Setup 6 not found, skipping installer. & exit /b 0)
"%ISCC%" /Q installer\TrayPal.iss
if errorlevel 1 (echo INSTALLER BUILD FAILED & exit /b 1)
echo Built dist\TrayPal-Setup.exe
