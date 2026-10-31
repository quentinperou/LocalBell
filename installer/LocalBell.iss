; LocalBell installer (Inno Setup 6), in English and French (picked from the Windows display language).
; Built by scripts\build.ps1, which prepares dist\app first:
;   ISCC.exe /DAppVersion=1.0.0 installer\LocalBell.iss
; The app also runs it to install an update (src\LocalBell.App\Services\Updater.cs): silent, closing the running app
; through the Restart Manager (WPF quits on its WM_QUERYENDSESSION), /RELAUNCH=1.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; Windows file versions are numbers only: 1.1.0 for a 1.1.0-beta.1 pre-release.
#if Pos("-", AppVersion) > 0
  #define AppFileVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define AppFileVersion AppVersion
#endif

#define AppName "LocalBell"
#define AppExe "LocalBell.exe"

[Setup]
AppId={{E27D8A23-DEFC-474E-83B5-7C13F2235A4A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=QuentinPerou
AppPublisherURL=https://github.com/quentinperou
AppSupportURL=https://github.com/quentinperou/LocalBell
AppUpdatesURL=https://github.com/quentinperou/LocalBell/releases
VersionInfoVersion={#AppFileVersion}
DefaultDirName={autopf}\LocalBell
DisableProgramGroupPage=yes
; Admin rights: installs for every user of the PC (shop computers), lets the .NET runtime be installed, and opens
; the firewall to LocalBell (the other PCs must reach it).
PrivilegesRequired=admin
; x64 Windows only: on Arm64 the x64 build needs the x64 .NET runtime, which the check below does not look for.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=LocalBell-Setup-{#AppVersion}
SetupIconFile=..\src\LocalBell.App\Assets\LocalBell.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
; Only ask for the language when Windows uses one the installer does not have.
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
english.RuntimeMissing=LocalBell needs the Microsoft ".NET 10 Desktop Runtime", which is not installed on this PC.%nInstall it now?
french.RuntimeMissing=LocalBell a besoin du « .NET 10 Desktop Runtime » (Microsoft), absent de ce PC.%nL'installer maintenant ?
english.RuntimeInstalling=Installing the .NET 10 Desktop Runtime...
french.RuntimeInstalling=Installation du .NET 10 Desktop Runtime…
english.RuntimeFailed=The automatic installation failed. The download page will open:%nchoose ".NET Desktop Runtime 10" (x64).
french.RuntimeFailed=L'installation automatique a échoué. La page de téléchargement va s'ouvrir :%nchoisissez « .NET Desktop Runtime 10 » (x64).
english.DeleteUserData=Also delete your LocalBell settings?%n%nKeep them if you are going to install LocalBell again.
french.DeleteUserData=Supprimer aussi vos réglages LocalBell ?%n%nGardez-les si vous allez réinstaller LocalBell.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; "Start with Windows" is written by the app itself (Settings › General, on by default once installed): removed with it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LocalBell"; Flags: uninsdeletevalue dontcreatekey

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[UninstallDelete]
; Temporary files of the app: the last update downloaded.
Type: filesandordirs; Name: "{%TEMP}\LocalBell-Update"

[Run]
; The other PCs reach LocalBell on its UDP and TCP ports (53318 and the next ones): one rule for the program, every
; network profile (a network set to "Public" on one PC must not hide it). The old rule goes first (a reinstall).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LocalBell"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LocalBell"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Update installed from the app (silent, /RELAUNCH=1): start the app again, as the user, not elevated, near the clock.
Filename: "{app}\{#AppExe}"; Parameters: "--hidden"; Flags: nowait runasoriginaluser skipifnotsilent; Check: RelaunchRequested

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LocalBell"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"

[Code]
{ Set by the app when it installs an update (Updater.InstallerArguments). }
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ The app is framework-dependent: it needs the .NET 10 Desktop Runtime. }
function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

procedure EnsureDesktopRuntime;
var
  ResultCode: Integer;
begin
  if IsDesktopRuntimeInstalled then
    Exit;
  if MsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNO) <> IDYES then
    Exit;
  WizardForm.StatusLabel.Caption := CustomMessage('RuntimeInstalling');
  if not Exec('winget.exe', 'install --id Microsoft.DotNet.DesktopRuntime.10 -e --silent --accept-source-agreements --accept-package-agreements',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or not IsDesktopRuntimeInstalled then
  begin
    MsgBox(CustomMessage('RuntimeFailed'), mbInformation, MB_OK);
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    EnsureDesktopRuntime;
end;

{ LocalBell runs near the clock: close it before its files are removed. taskkill without /f sends WM_CLOSE to the
  windows of LocalBell.exe only (the list window hides itself on it, so /f follows for what is left; the others see this
  PC offline within two minutes). }
procedure CloseRunningApp;
var
  ResultCode: Integer;
begin
  if Exec('taskkill.exe', '/im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    Sleep(1500);
  Exec('taskkill.exe', '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeUninstall: Boolean;
begin
  CloseRunningApp;
  Result := True;
end;

{ Settings (%LOCALAPPDATA%\LocalBell, of the account that uninstalls): kept unless the user says otherwise, "No" being
  the default, so that a reinstall finds them (the same id: the other PCs keep knowing this one). A silent uninstall
  keeps them. The start with Windows (written by the app, not by Setup) goes too: it would start an exe that is gone. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LocalBell');
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent and
     (MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(ExpandConstant('{localappdata}\LocalBell'), True, True, True);
end;
