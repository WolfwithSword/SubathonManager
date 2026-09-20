; Thin network installer for SubathonManager (Windows).
;
; It downloads the portable zip from the matching GitHub release
; or nightly as default

; Built by .github/workflows/build.yml. Required ISCC defines:
;   AppVer     display version, e.g. 2.0.3 or nightly
;   FileVer    numeric version for the version resource, e.g. 2.0.3.0
;   ZipUrl     full https URL of the release asset
;   ZipName    asset file name
;   ZipSha256  hex SHA-256 of the asset; omit to skip verification, which is what
;              nightly does since that asset is replaced on every build

#ifndef AppVer
  #define AppVer "0.0.0"
#endif
#ifndef FileVer
  #define FileVer "0.0.0.0"
#endif
#ifndef ZipUrl
  #error ZipUrl must be defined
#endif
#ifndef ZipName
  #error ZipName must be defined
#endif
#ifndef ZipSha256
  #define ZipSha256 ""
#endif

#define AppName "SubathonManager"
#define AppPublisher "WolfwithSword"
#define AppURL "https://subathonmanager.app"
#define AppExe "SubathonManager.exe"

[Setup]
AppId={{8F3A1C2E-5D47-4B9A-9E13-6C0F2A7B4D58}
AppName={#AppName}
AppVersion={#AppVer}
VersionInfoVersion={#FileVer}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL=https://github.com/WolfwithSword/SubathonManager/releases

; Per-user install. The app writes into its own folder
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

ArchiveExtraction=full

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputBaseFilename=SubathonManager_win-x64_Setup_{#AppVer}
SetupIconFile=..\..\assets\icon.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LicenseFile=LICENSE.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Downloaded to {tmp} by the code below, then extracted
Source: "{tmp}\{#ZipName}"; DestDir: "{app}"; Flags: external extractarchive ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then
    Log('Downloaded ' + FileName);
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;

  DownloadPage.Clear;
  DownloadPage.Add('{#ZipUrl}', '{#ZipName}', '{#ZipSha256}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Log('Download aborted by user.')
      else
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir, Msg: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  AppDir := ExpandConstant('{app}');
  if not DirExists(AppDir) then
    Exit;

  Msg := 'Remove your SubathonManager data as well?' + #13#10 + #13#10 +
    'This deletes the subathon database, settings, and imported widgets and overlays in:' + #13#10 +
    AppDir;

  if SuppressibleMsgBox(Msg, mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    DelTree(AppDir, True, True, True);
end;
