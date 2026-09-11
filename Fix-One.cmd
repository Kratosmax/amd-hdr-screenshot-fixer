@echo off
if "%~1"=="" (
  echo Drag an AMD PNG screenshot onto this file.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Fix-AmdScreenshot.ps1" -File "%~1"
if errorlevel 1 pause
