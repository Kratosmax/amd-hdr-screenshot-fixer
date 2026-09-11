@echo off
setlocal
dotnet build "%~dp0AmdHdrScreenshotFixer.Gui\AmdHdrScreenshotFixer.Gui.csproj" -c Release
if errorlevel 1 pause
