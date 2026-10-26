; Build with Build-Release.ps1. No certificate is assumed: installers are unsigned.
#ifndef ProductName
  #error ProductName is required
#endif
#ifndef ProductExe
  #error ProductExe is required
#endif
#ifndef ProductAppId
  #error ProductAppId is required
#endif
#ifndef ProductVersion
  #error ProductVersion is required
#endif
#ifndef PackageDirectory
  #error PackageDirectory is required
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory is required
#endif
#ifndef ProductDataDirectory
  #define ProductDataDirectory "{localappdata}\" + ProductName + "Installed"
#endif

[Setup]
AppId={#ProductAppId}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppPublisher=BeteixZ
AppPublisherURL=https://github.com/BeteixZ/CodexDashboard
AppSupportURL=https://github.com/BeteixZ/CodexDashboard/issues
AppUpdatesURL=https://github.com/BeteixZ/CodexDashboard/releases
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
AllowNoIcons=yes
UsePreviousAppDir=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
Uninstallable=yes
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={code:GetReleaseDirectory}\{#ProductExe}
UninstallFilesDir={app}\uninstall
OutputDir={#ReleaseDirectory}
OutputBaseFilename={#ProductName}-{#ProductVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
SetupMutex={#ProductAppId}.Setup
VersionInfoVersion={#ProductVersion}.0
VersionInfoDescription={#ProductName} installation
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#ProductVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut / 创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PackageDirectory}\*"; DestDir: "{code:GetReleaseDirectory}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{code:GetReleaseDirectory}\{#ProductExe}"; Parameters: "--data-dir ""{#ProductDataDirectory}"" --protocol-profile installed"; WorkingDir: "{code:GetReleaseDirectory}"; Check: not WizardNoIcons
Name: "{autodesktop}\{#ProductName}"; Filename: "{code:GetReleaseDirectory}\{#ProductExe}"; Parameters: "--data-dir ""{#ProductDataDirectory}"" --protocol-profile installed"; WorkingDir: "{code:GetReleaseDirectory}"; Tasks: desktopicon
Name: "{group}\Uninstall {#ProductName}"; Filename: "{uninstallexe}"; Check: not WizardNoIcons

[Run]
Filename: "{code:GetReleaseDirectory}\{#ProductExe}"; Parameters: "--data-dir ""{#ProductDataDirectory}"" --protocol-profile installed"; WorkingDir: "{code:GetReleaseDirectory}"; Description: "Launch {#ProductName} / 启动程序"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  ReleaseDirectory: String;
  ReleaseRoot: String;

function GetReleaseDirectory(Param: String): String;
var
  Candidate: String;
  Suffix: Integer;
begin
  if (ReleaseDirectory = '') or (ReleaseRoot <> ExpandConstant('{app}')) then
  begin
    { A fresh directory even when reinstalling the same package: running binaries
      and older releases are never overwritten or forcibly closed. }
    ReleaseRoot := ExpandConstant('{app}');
    Candidate := ReleaseRoot + '\releases\{#ProductVersion}-' +
      GetDateTimeString('yyyymmddhhnnss', '-', ':');
    ReleaseDirectory := Candidate;
    Suffix := 0;
    while DirExists(ReleaseDirectory) do
    begin
      Suffix := Suffix + 1;
      ReleaseDirectory := Candidate + '-' + IntToStr(Suffix);
    end;
  end;
  Result := ReleaseDirectory;
end;

function InitializeUninstall: Boolean;
var
  FindRec: TFindRec;
  ExePath: String;
  Stream: TFileStream;
begin
  Result := True;
  { Probe every installed release before any uninstall deletion. No process is
    killed, no automatic restart, and no data-directory deletion is registered. }
  if FindFirst(ExpandConstant('{app}\releases\*'), FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
          (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          ExePath := ExpandConstant('{app}\releases\') + FindRec.Name + '\{#ProductExe}';
          if FileExists(ExePath) then
          begin
            try
              Stream := TFileStream.Create(ExePath, fmOpenReadWrite or fmShareExclusive);
              Stream.Free;
            except
              SuppressibleMsgBox('Please exit {#ProductName} before uninstalling. Your data will be retained.' + #13#10 +
                '请先退出 {#ProductName}，然后重试卸载。记录与设置将保留。', mbError, MB_OK, IDOK);
              Result := False;
              Exit;
            end;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;
