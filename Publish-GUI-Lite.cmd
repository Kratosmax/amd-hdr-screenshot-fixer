@echo off
setlocal
set "OUT=%~dp0temp\AMD-HDR-Screenshot-Fixer-Lite"
dotnet publish "%~dp0AmdHdrScreenshotFixer.Gui\AmdHdrScreenshotFixer.Gui.csproj" -c Release -r win-x64 --self-contained false -o "%OUT%"
if errorlevel 1 goto :failed
copy /Y "%~dp0config.json" "%OUT%\config.json" >nul
if exist "%~dp0calibration.json" copy /Y "%~dp0calibration.json" "%OUT%\calibration.json" >nul
copy /Y "%~dp0README.md" "%OUT%\README.md" >nul
echo Lite preview ready: %OUT%
exit /b 0
:failed
pause
exit /b 1
