@echo off
setlocal
set "OUT=%~dp0temp\AMD-HDR-Screenshot-Fixer-Single"
dotnet publish "%~dp0AmdHdrScreenshotFixer.Gui\AmdHdrScreenshotFixer.Gui.csproj" -c Release -r win-x64 --self-contained true -o "%OUT%" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 goto :failed
echo Single EXE ready: %OUT%\AmdHdrScreenshotFixer.exe
exit /b 0
:failed
pause
exit /b 1
