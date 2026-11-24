[Setup]
AppId=HapticBridge.Desktop
AppName=HapticBridge
AppVersion={#SetupVersion}
AppVerName=HapticBridge {#AppVersion}
AppPublisher=HapticBridge
VersionInfoVersion={#SetupVersion}
VersionInfoDescription=HapticBridge desktop setup
DefaultDirName={commonpf64}\HapticBridge
DefaultGroupName=HapticBridge
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion={#MinWindows}
WizardStyle=modern dynamic
SetupIconFile=..\src\HapticBridge.UI\Assets\RazerDualSenseInstaller.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputBaseFilename=HapticBridge-Setup-{#SetupVersion}-x64
Compression=lzma2
SolidCompression=no
SetupLogging=yes
UninstallLogging=yes
CloseApplications=yes
RestartApplications=no
AllowUNCPath=no
AllowRootDirectory=no
UsePreviousAppDir=no
UsePreviousTasks=yes
UninstallDisplayName=HapticBridge
AppReadmeFile={app}\Installation help.txt

[Messages]
WelcomeLabel1=Set up HapticBridge
WelcomeLabel2=Use your Android-connected controller for DualSense haptics in compatible PC games.%n%nSetup installs HapticBridge in Program Files for everyone on this PC. Open HapticBridge from Start and select Check PC setup to review the tools for your controller.
FinishedHeadingLabel=HapticBridge is installed
FinishedLabel=Open HapticBridge from the Start menu and select Check PC setup. Pair Artemis on your phone with Apollo on this PC, then enable haptics when you are ready.

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
#include "PayloadFiles.iss"
Source: "{#HelpFile}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{commonprograms}\HapticBridge"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{commondesktop}\HapticBridge"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\HapticBridge.Desktop_is1';
  RuntimeUrl = 'https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.15-windows-x64-installer';
  RuntimeFile = 'windowsdesktop-runtime-10.0.15-win-x64.exe';
  RuntimeDownloadUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/' + RuntimeFile;
  RuntimeSHA256 = 'e61b030c1673edfa3c0844502eff83198c46af9e7df70dfc6e2efc5b11ea513a';
var
  RuntimePage: TWizardPage;
  RuntimeDownload: TDownloadWizardPage;
  RuntimeText: TNewStaticText;
  PreviewOnly: Boolean;
  RuntimeRestartRequired: Boolean;

function FileAttributes(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function DriveType(Path: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function ContainsReparsePoint(Path: String): Boolean;
var Parent: String; Attr: Cardinal;
begin
  Result := True;
  repeat
    Attr := FileAttributes(Path);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then Exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then Break;
    Path := Parent;
  until Path = '';
  Result := False;
end;

#include "PayloadPaths.iss"

function FolderHasContents(Path: String): Boolean;
var Entry: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Path) + '*', Entry) then
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin Result := True; Exit; end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
end;

function TargetIssue(Path, Registered, PreviousVersion: String): String;
var OldVersion, NewVersion: Int64;
begin
  Result := '';
  Path := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  if DriveType(AddBackslash(ExtractFileDrive(Path))) <> 3 then
    Result := 'Choose a folder on a fixed local drive.'
  else if ContainsReparsePoint(Path) then
    Result := 'Choose a local folder without junctions or symbolic links.'
  else if Registered <> '' then begin
    if CompareText(Path, RemoveBackslashUnlessRoot(ExpandFileName(Registered))) <> 0 then
      Result := 'This account already has HapticBridge in ' + Registered + '. Use that folder to repair or upgrade, or uninstall the existing copy before moving.'
    else if not StrToVersion(PreviousVersion, OldVersion) or not StrToVersion('{#SetupVersion}', NewVersion) then
      Result := 'The installed version could not be verified. Check its entry in Windows Installed apps.'
    else if OldVersion > NewVersion then
      Result := 'A newer HapticBridge is installed. To deliberately roll back, uninstall it first, then run the earlier setup.'
    else if ('{#AppExe}' = 'HapticBridge.exe') and (PreviousVersion = '1.3.17.1') then
      Result := 'Uninstall the older HapticBridge 1.3.17.1 test installer from Windows Installed apps, then retry. This version uses a new executable name.'
    else if PayloadHasReparsePoint(Path) then
      Result := 'The installed app has a linked file or folder. Check the original folder before repairing or upgrading.'
  end else if FolderHasContents(Path) then
    Result := 'Choose an empty folder. Keep portable builds and their settings in their original location.';
end;

function CheckTarget: String;
var Registered, PreviousVersion: String;
begin
  if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue)),
      ExpandConstant('{commonpf64}\HapticBridge')) <> 0 then begin
    Result := 'Use the HapticBridge folder in Windows Program Files.'; Exit;
  end;
  if RegKeyExists(HKLM64, UninstallKey) then begin
    if not RegQueryStringValue(HKLM64, UninstallKey, 'InstallLocation', Registered) or (Registered = '') then begin
      Result := 'The existing installation has no recorded folder. Review its entry in Windows Installed apps before continuing.'; Exit;
    end;
    RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', PreviousVersion);
  end;
  if (Registered = '') and DirExists(WizardDirValue) then begin
    Result := 'Program Files already contains an unregistered HapticBridge folder. Keep a copy and move it aside before retrying setup.'; Exit;
  end;
  Result := TargetIssue(WizardDirValue, Registered, PreviousVersion);
end;

function HasFramework(Root, Framework, RequiredFile: String): Boolean;
var Base: String; Entry: TFindRec; Version: Int64;
begin
  Result := False;
  Base := AddBackslash(Root) + 'shared\' + Framework + '\';
  if FindFirst(Base + '10.0.*', Entry) then
    try
      repeat
        if ((Entry.Attributes and $10) <> 0) and StrToVersion(Entry.Name, Version) and
           FileExists(Base + Entry.Name + '\' + RequiredFile) then begin
          Result := True; Exit;
        end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
end;

function RuntimeIssue: String;
var Root: String;
begin
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root)
    or (Root = '') then Root := ExpandConstant('{commonpf64}\dotnet');
  Result := '';
  if RuntimeRestartRequired then
    Result := 'Restart Windows, then reopen HapticBridge Setup.'
  else if not FileExists(AddBackslash(Root) + 'dotnet.exe') then
    Result := 'Windows x64 .NET was not found. Run the downloaded Desktop Runtime installer and finish its setup.'
  else if not HasFramework(Root, 'Microsoft.WindowsDesktop.App', 'PresentationFramework.dll') then
    Result := 'Windows x64 .NET was found, but the .NET 10 Desktop Runtime is missing. Run the downloaded Desktop Runtime installer.'
  else if not HasFramework(Root, 'Microsoft.NETCore.App', 'coreclr.dll') then
    Result := 'The .NET 10 Desktop Runtime appears incomplete. Run its installer again and choose Repair if offered.';
end;

procedure OpenRuntime(Sender: TObject);
var ErrorCode: Integer;
begin
  if not ShellExec('open', RuntimeUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox('Open this Microsoft download page in your browser:' + #13#10 + RuntimeUrl, mbInformation, MB_OK);
end;

procedure InstallRuntime(Sender: TObject);
var Code: Integer; Issue: String;
begin
  if PreviewOnly or not IsAdminInstallMode then Exit;
  if RuntimeRestartRequired then begin
    MsgBox(RuntimeIssue, mbInformation, MB_OK);
    Exit;
  end;
  if RuntimeIssue <> '' then begin
    RuntimeDownload.Clear;
    RuntimeDownload.Add(RuntimeDownloadUrl, RuntimeFile, RuntimeSHA256);
    RuntimeDownload.Show;
    try
      try
        RuntimeDownload.Download;
      except
        if not RuntimeDownload.AbortedByUser then
          MsgBox('The runtime could not be downloaded or verified. Retry or use the manual Microsoft download.' + #13#10 +
            GetExceptionMessage, mbError, MB_OK);
        Exit;
      end;
    finally
      RuntimeDownload.Hide;
    end;
    if not Exec(ExpandConstant('{tmp}\') + RuntimeFile, '/install /passive /norestart', '',
        SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then begin
      MsgBox('Could not start the Microsoft installer. Windows error ' + IntToStr(Code) + '.', mbError, MB_OK);
      Exit;
    end;
    Log('Microsoft Desktop Runtime installer returned ' + IntToStr(Code));
    RuntimeRestartRequired := (Code = 3010) or (Code = 1641);
    if (Code <> 0) and not RuntimeRestartRequired and (RuntimeIssue <> '') then begin
      MsgBox('The Microsoft installer did not complete (code ' + IntToStr(Code) +
        '). Retry or use the manual Microsoft download.', mbError, MB_OK);
      Exit;
    end;
  end;
  Issue := RuntimeIssue;
  if Issue = '' then
    RuntimeText.Caption := 'The Windows x64 .NET 10 Desktop Runtime is ready. Select Next to continue.'
  else
    MsgBox(Issue, mbInformation, MB_OK);
end;

procedure InitializeWizard;
var Button: TNewButton;
begin
  RuntimePage := CreateCustomPage(wpWelcome, 'Install the Windows desktop runtime', 'HapticBridge needs Microsoft .NET 10 Desktop Runtime for Windows x64.');
  RuntimeText := TNewStaticText.Create(RuntimePage);
  RuntimeText.Parent := RuntimePage.Surface;
  RuntimeText.AutoSize := False;
  RuntimeText.WordWrap := True;
  RuntimeText.SetBounds(0, 0, RuntimePage.SurfaceWidth, ScaleY(94));
  RuntimeText.Caption := 'Use Install Desktop Runtime to download and run Microsoft''s installer. When it finishes, select Next. If a restart is required, reopen HapticBridge Setup after restarting.';
  RuntimeDownload := CreateDownloadPage('Downloading Desktop Runtime', 'Downloading the Windows x64 installer from Microsoft.', nil);
  RuntimeDownload.ShowBaseNameInsteadOfUrl := True;
  Button := TNewButton.Create(RuntimePage);
  Button.Parent := RuntimePage.Surface;
  Button.SetBounds(0, RuntimeText.Height + ScaleY(6), ScaleX(225), ScaleY(28));
  Button.Caption := 'Install Desktop Runtime';
  Button.Enabled := IsAdminInstallMode and not PreviewOnly;
  Button.OnClick := @InstallRuntime;
  Button := TNewButton.Create(RuntimePage);
  Button.Parent := RuntimePage.Surface;
  Button.SetBounds(0, RuntimeText.Height + ScaleY(42), ScaleX(225), ScaleY(28));
  Button.Caption := 'Open manual Microsoft download';
  Button.OnClick := @OpenRuntime;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = RuntimePage.ID) and (RuntimeIssue = '');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Issue: String;
begin
  Issue := '';
  if CurPageID = RuntimePage.ID then Issue := RuntimeIssue
  else if CurPageID = wpSelectDir then Issue := CheckTarget;
  Result := Issue = '';
  if not Result then MsgBox(Issue, mbInformation, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if PreviewOnly or not IsAdminInstallMode then begin
    Result := 'Preview complete. Close the wizard to leave this PC as it is.'; Exit;
  end;
  Result := CheckTarget;
  if Result = '' then Result := RuntimeIssue;
end;

procedure CheckEmbedded(Name, Expected: String);
begin
  ExtractTemporaryFile(Name);
  if CompareText(GetSHA256OfFile(ExpandConstant('{tmp}\') + Name), Expected) <> 0 then
    RaiseException('Payload verification failed for ' + Name);
end;
#include "PayloadChecks.iss"

function InitializeSetup: Boolean;
var Work: String; Index: Integer; VerifyOnly: Boolean;
begin
  Result := True;
  VerifyOnly := False;
  for Index := 1 to ParamCount do begin
    if CompareText(ParamStr(Index), '/VERIFYONLY') = 0 then VerifyOnly := True;
    if CompareText(ParamStr(Index), '/PREVIEWONLY') = 0 then PreviewOnly := True;
  end;
  if VerifyOnly then begin
    VerifyPayload;
    Work := ExpandConstant('{tmp}');
    if (TargetIssue(Work, '', '') = '') or
       (TargetIssue(Work, Work, '99.0.0.0') = '') or
       (TargetIssue(Work, Work + '-other', '{#SetupVersion}') = '') or
       (TargetIssue(Work, Work, '{#SetupVersion}') <> '') or
       (TargetIssue(Work + '\empty', '', '') <> '') then RaiseException('Installer policy checks failed.');
    if ('{#AppExe}' = 'HapticBridge.exe') and
       (TargetIssue(Work, Work, '1.3.17.1') = '') then RaiseException('Legacy upgrade check failed.');
    Log('HapticBridge embedded payload and target policy verified. No installation requested.');
    Result := False;
  end else if not (IsAdminInstallMode or PreviewOnly) then begin
    SuppressibleMsgBox('HapticBridge setup needs administrator approval to install in Program Files. Open setup again without /CURRENTUSER.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := UninstallSilent or (MsgBox('Remove the HapticBridge app and its shortcuts?' + #13#10#13#10 +
    'Your drivers, game audio settings and backups stay on this PC. If you granted HidHide access to this copy, review its exact executable path in the HidHide configuration client before reusing this folder.' + #13#10#13#10 +
    'Choose No to review Installation help first.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = idYes);
end;
