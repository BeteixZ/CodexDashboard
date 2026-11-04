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
#ifndef ProductShortcutDirectory
  #define ProductShortcutDirectory "{group}"
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
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.ExitBeforeUninstall=Please exit %1 before uninstalling. Your records and settings will be retained.
chinesesimplified.ExitBeforeUninstall=请先退出 %1，然后重试卸载。记录与设置将保留。
english.KeepInstallationDirectory=Install this upgrade in the existing program folder (%1). To change folders, uninstall the existing version first. Your records and settings will be retained.
chinesesimplified.KeepInstallationDirectory=请在现有程序目录（%1）中升级。如需更换目录，请先卸载现有版本。记录与设置将保留。
english.SeparateDataDirectory=Choose a program folder that is separate from the data folder (%1). Neither folder may contain the other.
chinesesimplified.SeparateDataDirectory=请选择与数据目录（%1）分开的程序目录。两个目录不能相同或互相包含。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PackageDirectory}\*"; DestDir: "{code:GetReleaseDirectory}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "languages\LICENSE.txt"; DestDir: "{code:GetReleaseDirectory}\licenses"; DestName: "Inno-Chinese-Translation.txt"; Flags: ignoreversion

[Icons]
Name: "{#ProductShortcutDirectory}\{#ProductName}"; Filename: "{code:GetReleaseDirectory}\{#ProductExe}"; Parameters: "--data-dir ""{#ProductDataDirectory}"" --protocol-profile installed"; WorkingDir: "{code:GetReleaseDirectory}"; Check: not WizardNoIcons
Name: "{autodesktop}\{#ProductName}"; Filename: "{code:GetReleaseDirectory}\{#ProductExe}"; Parameters: "--data-dir ""{#ProductDataDirectory}"" --protocol-profile installed"; WorkingDir: "{code:GetReleaseDirectory}"; Tasks: desktopicon
Name: "{#ProductShortcutDirectory}\{cm:UninstallProgram,{#ProductName}}"; Filename: "{uninstallexe}"; Check: not WizardNoIcons

[Run]
Filename: "{code:GetReleaseDirectory}\{#ProductExe}"; Parameters: "--data-dir ""{#ProductDataDirectory}"" --protocol-profile installed"; WorkingDir: "{code:GetReleaseDirectory}"; Description: "{cm:LaunchProgram,{#ProductName}}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  ReleaseDirectory: String;
  ReleaseRoot: String;

function ValidateInstallationDirectory: String;
var
  ProgramPath, DataPath, PreviousPath: String;
begin
  Result := '';
  ProgramPath := AddBackslash(ExpandFileName(WizardDirValue));
  DataPath := AddBackslash(ExpandFileName(ExpandConstant('{#ProductDataDirectory}')));
  if (CompareText(Copy(ProgramPath, 1, Length(DataPath)), DataPath) = 0) or
     (CompareText(Copy(DataPath, 1, Length(ProgramPath)), ProgramPath) = 0) then
  begin
    Result := FmtMessage(CustomMessage('SeparateDataDirectory'), [DataPath]);
    Exit;
  end;
  { UsePreviousAppDir only sets a default. Installing the same AppId in a
    different directory replaces the uninstall registration for the old one. }
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductAppId}_is1',
    'Inno Setup: App Path', PreviousPath) and (PreviousPath <> '') and
    (CompareText(ProgramPath, AddBackslash(ExpandFileName(PreviousPath))) <> 0) then
    Result := FmtMessage(CustomMessage('KeepInstallationDirectory'), [PreviousPath]);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ErrorMessage: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  ErrorMessage := ValidateInstallationDirectory;
  if ErrorMessage <> '' then
  begin
    SuppressibleMsgBox(ErrorMessage, mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { An unattended installation can bypass the directory selection page. }
  Result := ValidateInstallationDirectory;
end;

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
              SuppressibleMsgBox(FmtMessage(CustomMessage('ExitBeforeUninstall'), ['{#ProductName}']), mbError, MB_OK, IDOK);
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
