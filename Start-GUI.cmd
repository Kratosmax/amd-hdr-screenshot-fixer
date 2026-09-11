@echo off
setlocal
set "EXE=%~dp0AmdHdrScreenshotFixer.Gui\bin\Release\net8.0-windows\AmdHdrScreenshotFixer.exe"
if not exist "%EXE%" (
  echo GUI has not been built. Run Build-GUI.cmd first.
  pause
  exit /b 1
)
start "" "%EXE%"
