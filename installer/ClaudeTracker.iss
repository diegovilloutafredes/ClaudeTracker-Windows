; ClaudeTracker for Windows: the setup (Inno Setup 6).
; Built by scripts\build-installer.ps1, which passes the version and what to pack:
;   ISCC /DAppVersion=1.2.3 /DSourceDir=<published app> /DOutputDir=<where> [/DWebView2Bootstrapper=<file>]
;
; What the app relies on here, each with its other half in the app's sources:
;   - the file is named ClaudeTracker-Setup.exe and carries the app's version (Updates.cs:
;     the updater installs the release asset of exactly that name, and only a newer version);
;   - /AppArgs=<arguments> is handed to the app this setup starts when it is done, which is
;     how an update comes back without showing anything (Updates.SilentInstallArguments);
;   - "ClaudeTracker.exe --quit" closes the running app (App.xaml.cs), and its mutex tells
;     when it has gone;
;   - "--just-installed" is added for the app this setup starts after a first install, not
;     after an upgrade: an uninstall takes the sign-in entry away, and the app must not read
;     that as the user having removed it (LoginItem.AtStart);
;   - unins000.exe beside the app is how the app knows it was installed (AppPaths.IsInstalled).

#ifndef AppVersion
  #error AppVersion is not defined: build with scripts\build-installer.ps1
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppExe "ClaudeTracker.exe"
#define AppGuid "5E0B5C2D-7B0A-4F0E-9C39-6A1D2B7E4C11"
#define AppMutexName "ClaudeTracker.SingleInstance"
#define SignInEntry "ClaudeTracker"

[Setup]
; Never change this: it is how an upgrade and the uninstaller find the copy already there.
AppId={{{#AppGuid}}
AppName=ClaudeTracker
AppVersion={#AppVersion}
AppVerName=ClaudeTracker {#AppVersion}
AppPublisher=Diego Villouta Fredes
AppPublisherURL=https://github.com/diegovilloutafredes/ClaudeTracker-Windows
AppSupportURL=https://github.com/diegovilloutafredes/ClaudeTracker-Windows/issues
AppCopyright=(c) 2026 Diego Villouta Fredes. Unofficial tool, not affiliated with or endorsed by Anthropic.
VersionInfoVersion={#AppVersion}
VersionInfoDescription=ClaudeTracker Setup
; For this user only: no administrator prompt, nothing outside the user's own folders.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ClaudeTracker
; The folder is the one question, asked on a first install and never on an upgrade.
DisableDirPage=auto
DisableProgramGroupPage=yes
; The language is Windows' own when the setup speaks it; asked only when it does not.
ShowLanguageDialog=auto
OutputDir={#OutputDir}
OutputBaseFilename=ClaudeTracker-Setup
SetupIconFile=..\src\ClaudeTracker.App\Assets\ClaudeTracker.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=ClaudeTracker
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; The running app is closed by this script, with "--quit". Windows' own way (Restart
; Manager) has nothing to ask of an app that lives in the tray without a window.
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.InstallingWebView2=Installing Microsoft Edge WebView2, which the app shows claude.ai in...
es.InstallingWebView2=Instalando Microsoft Edge WebView2, donde la app muestra claude.ai...
en.StillRunning=ClaudeTracker is still running. Quit it from its icon beside the clock, then try again.
es.StillRunning=ClaudeTracker sigue abierto. Ciérralo desde su icono junto al reloj y vuelve a intentarlo.
en.LeftBehind=ClaudeTracker was removed.%n%nYour accounts, settings and usage history were kept, in case you install it again. They are in these two folders; delete them to remove everything:%n%n%1%n%2
es.LeftBehind=ClaudeTracker se desinstaló.%n%nTus cuentas, ajustes e historial de uso se conservaron, por si vuelves a instalarlo. Están en estas dos carpetas; bórralas para eliminarlo todo:%n%n%1%n%2

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
#ifdef WebView2Bootstrapper
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: deleteafterinstall; Check: NeedsWebView2
#endif

[Icons]
Name: "{autoprograms}\ClaudeTracker"; Filename: "{app}\{#AppExe}"

[Run]
#ifdef WebView2Bootstrapper
; Windows 11 has the runtime; Windows 10 may not. Microsoft's own small installer fetches it, for this user, without a prompt.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:InstallingWebView2}"; Check: NeedsWebView2
#endif
; Always: after a first install the app shows its popover, after an update it is given --background and shows nothing.
Filename: "{app}\{#AppExe}"; Parameters: "{code:AppArguments}"; Flags: nowait

[UninstallDelete]
; A downloaded update waiting to be run: the app's own leftover, not the user's data.
Type: filesandordirs; Name: "{localappdata}\ClaudeTracker\Updates"

[Code]
const
  WebView2Runtime = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  SignInKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  SignInNotesKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';

var
  FirstInstall: Boolean;

function InitializeSetup: Boolean;
begin
  // Asked before anything is written: from then on there is always a copy installed.
  FirstInstall := not RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppGuid}' + '}_is1');
  Result := True;
end;

// What the app is started with when the setup is done: whatever /AppArgs= asked for, and
// on a first install the word that says so.
function AppArguments(Param: String): String;
begin
  Result := ExpandConstant('{param:AppArgs|}');
  if FirstInstall then
    Result := Trim(Result + ' --just-installed');
end;

function HasVersion(RootKey: Integer; const Key: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

// Microsoft's own test for the runtime: a version under either key means it is there.
// "/WebView2=force" on the command line runs the installer anyway, to see that step work.
function NeedsWebView2: Boolean;
begin
  Result := (ExpandConstant('{param:WebView2|}') = 'force')
    or not (HasVersion(HKLM32, 'SOFTWARE\' + WebView2Runtime) or HasVersion(HKCU, 'Software\' + WebView2Runtime));
end;

// Asks the running app to quit and waits until it has. True once no copy is running.
function QuitRunningApp: Boolean;
var
  Code, Waited: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if CheckForMutexes('{#AppMutexName}') and FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Waited := 0;
  while CheckForMutexes('{#AppMutexName}') and (Waited < 20000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not CheckForMutexes('{#AppMutexName}');
  // The app has gone; its browser processes let go of their files a moment later.
  if Result and (Waited > 0) then
    Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if QuitRunningApp then
    Result := ''
  else
    Result := CustomMessage('StillRunning');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Kept: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Only now, once the user has said yes. Closed when the uninstaller starts, the app
    // stayed closed for someone who then answered "No".
    QuitRunningApp;
    // The app's own entry in the user's sign-in, and Windows' note about it. The app wrote
    // them, so no [Registry] line would take them away.
    RegDeleteValue(HKCU, SignInKey, '{#SignInEntry}');
    RegDeleteValue(HKCU, SignInNotesKey, '{#SignInEntry}');
  end;
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    // (No line here may begin with a square bracket: the compiler reads that as a section.)
    Kept := FmtMessage(CustomMessage('LeftBehind'), [ExpandConstant('{userappdata}\ClaudeTracker'), ExpandConstant('{localappdata}\ClaudeTracker')]);
    MsgBox(Kept, mbInformation, MB_OK);
  end;
end;
