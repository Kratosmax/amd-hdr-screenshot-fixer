#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif

[Setup]
AppId={{7F0A5F16-6606-4FA7-814A-19861EB7D0BC}
AppName=AMD HDR Screenshot Fixer
AppVersion={#AppVersion}
AppPublisher=Kratosmax
AppPublisherURL=https://github.com/Kratosmax/amd-hdr-screenshot-fixer
AppSupportURL=https://github.com/Kratosmax/amd-hdr-screenshot-fixer/issues
DefaultDirName={localappdata}\Programs\AmdHdrScreenshotFixer
DefaultGroupName=AMD HDR Screenshot Fixer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile={#SourcePath}\..\assets\app-icon.ico
UninstallDisplayIcon={app}\AmdHdrScreenshotFixer.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
LicenseFile={#SourcePath}\..\LICENSE

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AMD HDR Screenshot Fixer"; Filename: "{app}\AmdHdrScreenshotFixer.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\AMD HDR Screenshot Fixer"; Filename: "{app}\AmdHdrScreenshotFixer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Run]
Filename: "{app}\AmdHdrScreenshotFixer.exe"; Description: "启动 AMD HDR Screenshot Fixer"; Flags: nowait postinstall skipifsilent

#ifdef RequireDesktopRuntime
[Code]
function HasDesktopRuntime8: Boolean;
var
  FindRec: TFindRec;
  RuntimeRoot: String;
begin
  Result := False;
  RuntimeRoot := ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*');
  if FindFirst(RuntimeRoot, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDesktopRuntime8;
  if not Result then
    if MsgBox('Lite 版本需要 .NET 8 Desktop Runtime (x64)。是否打开微软官方下载页面？', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
#endif
