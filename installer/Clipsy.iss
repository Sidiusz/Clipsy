; Compile with: ISCC.exe installer\Clipsy.iss
; Expects publish output at: Clipsy\bin\publish\win-x64

#define ClipsyName "Clipsy"
#ifndef ClipsyVersion
#define ClipsyVersion "1.0.7"
#endif
#define ClipsyPublisher "Sidiusz"
#define ClipsyURL "https://github.com/Sidiusz/Clipsy"
#define ClipsyExeName "Clipsy.exe"

#ifndef ClipsyPublishDir
  #define ClipsyPublishDir "..\Clipsy\bin\publish\win-x64"
#endif

[Setup]
AppId={{E5F4D9A0-9F4A-4B3D-9F5E-3B7C0E2B7F11}}
AppName={#ClipsyName}
AppVersion={#ClipsyVersion}
AppPublisher={#ClipsyPublisher}
AppPublisherURL={#ClipsyURL}
AppSupportURL={#ClipsyURL}/issues
AppUpdatesURL={#ClipsyURL}/releases
DefaultDirName={autopf}\{#ClipsyName}
DefaultGroupName={#ClipsyName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#ClipsyExeName}
UninstallDisplayName={#ClipsyName}
OutputDir=output
OutputBaseFilename=Clipsy-Setup-{#ClipsyVersion}
SetupIconFile=..\Clipsy\Assets\clipsy.ico
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
; In-app updater downloads the new setup and exits Clipsy before running it;
; CloseApplications is the last resort if something else still holds files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#ClipsyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files an older version shipped but this one doesn't (debug symbols, retired binaries).
Type: files; Name: "{app}\*.pdb"

[Icons]
Name: "{group}\{#ClipsyName}"; Filename: "{app}\{#ClipsyExeName}"
Name: "{group}\Uninstall {#ClipsyName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#ClipsyName}"; Filename: "{app}\{#ClipsyExeName}"; Tasks: desktopicon

[Registry]
; Autostart uses the current user Run key; no elevation is required.

; WER LocalDumps: a minidump even for native __fastfail (0xc0000409) crashes that bypass the
; in-app filter. Mini, not full: a full dump holds screenshots and clipboard contents.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{#ClipsyExeName}"; \
    ValueType: expandsz; ValueName: "DumpFolder"; ValueData: "%LOCALAPPDATA%\Clipsy\CrashDumps"; \
    Flags: uninsdeletekey; Check: IsAdminInstallMode
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{#ClipsyExeName}"; \
    ValueType: dword; ValueName: "DumpType"; ValueData: "$00000001"; Check: IsAdminInstallMode
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{#ClipsyExeName}"; \
    ValueType: dword; ValueName: "DumpCount"; ValueData: "$00000003"; Check: IsAdminInstallMode

[Run]
Filename: "{app}\{#ClipsyExeName}"; Parameters: "{code:AutostartInitParameters}"; Flags: runhidden runasoriginaluser
; Silent installs (updates, CLI) relaunch too unless /NOLAUNCH; never elevated.
Filename: "{app}\{#ClipsyExeName}"; Description: "{cm:LaunchProgram,{#ClipsyName}}"; \
    Flags: nowait postinstall runasoriginaluser; Check: ShouldLaunchAfterInstall

[UninstallRun]
; Elevated as the same account this is that user's HKCU. Under another admin's credentials it isn't
; (ExecAsOriginalUser is unavailable when uninstalling); Clipsy drops a Run entry left pointing at
; a missing exe the next time it starts.
Filename: "{app}\{#ClipsyExeName}"; Parameters: "autostart-init --remove"; Flags: runhidden; RunOnceId: "ClipsyAutostartCleanup"

[Code]
const
  LegacyAutostartTaskName = 'ClipsyAutostart';

function GetCurrentProcessId: Cardinal;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function ProcessIdToSessionId(ProcessId: Cardinal; var SessionId: Cardinal): BOOL;
  external 'ProcessIdToSessionId@kernel32.dll stdcall';

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
  Arg, SlashArg, DashArg: String;
begin
  Result := False;
  SlashArg := '/' + UpperCase(Name);
  DashArg := '--' + UpperCase(Name);
  for I := 1 to ParamCount do
    begin
      Arg := UpperCase(ParamStr(I));
      if (Arg = SlashArg) or (Arg = DashArg) then
        begin
          Result := True;
          Exit;
        end;
    end;
end;

function ShouldLaunchAfterInstall(): Boolean;
begin
  Result := not HasSwitch('NOLAUNCH');
end;

function AutostartInitParameters(Param: String): String;
 begin
  Result := 'autostart-init';
  if HasSwitch('NOAUTOSTART') then
    Result := Result + ' --disabled';
end;

procedure DeleteLegacyAutostartTask;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "' + LegacyAutostartTaskName + '" /F', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

// Only this session's Clipsy: another signed-in user's copy is not ours to stop.
function SessionFilter(): String;
var
  SessionId: Cardinal;
begin
  Result := '';
  if ProcessIdToSessionId(GetCurrentProcessId, SessionId) then
    Result := ' AND SessionId = ' + IntToStr(SessionId)
  else
    Log('Could not determine the session id; checking all sessions.');
end;

// True when a Clipsy from this install folder (app or watchdog) runs in this session, or in any
// session with AllSessions; Kill ends them.
function ClipsyRunning(const Exe: String; Kill, AllSessions: Boolean): Boolean;
var
  Filter: String;
  Locator, Service, Procs, Proc: Variant;
  I: Integer;
begin
  Result := False;
  Filter := '';
  if not AllSessions then Filter := SessionFilter;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''{#ClipsyExeName}''' + Filter);
    for I := 0 to Procs.Count - 1 do
    begin
      Proc := Procs.ItemIndex(I);
      if not VarIsNull(Proc.ExecutablePath) and (CompareText(Proc.ExecutablePath, Exe) = 0) then
      begin
        Result := True;
        if Kill then Proc.Terminate(0);
      end;
    end;
  except
    Log('Clipsy process check failed: ' + GetExceptionMessage);
  end;
end;

// Ask a running Clipsy to exit cleanly (it finishes saving a recording first), then make sure
// nothing from the install folder is left holding files. Setup runs elevated and an elevated
// process can't reach the user's Clipsy pipe, so 'quit' runs as the original user when possible.
// The uninstaller can't (no ExecAsOriginalUser there); its elevated 'quit' signals Clipsy's quit
// event instead and waits for it to exit.
procedure QuitRunningClipsy(AsOriginalUser: Boolean);
var
  ResultCode, I: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#ClipsyExeName}');
  if not FileExists(Exe) then exit;
  if AsOriginalUser then
    ExecAsOriginalUser(Exe, 'quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    Exec(Exe, 'quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // The watchdog follows the app out within a few seconds.
  for I := 1 to 20 do
  begin
    if not ClipsyRunning(Exe, False, False) then break;
    Sleep(500);
  end;
  // A copy in another user's session can't be asked to quit, but it holds the install
  // folder's files too; end what's left only now, after this session had its clean quit.
  if ClipsyRunning(Exe, False, True) then
  begin
    Log('Clipsy still running (this or another session); terminating it.');
    ClipsyRunning(Exe, True, True);
    Sleep(500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningClipsy(True);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    DeleteLegacyAutostartTask;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    QuitRunningClipsy(False);
    DeleteLegacyAutostartTask;
  end;
  // [UninstallDelete] Check functions run at install time, so /KEEPDATA must be read here.
  if (CurUninstallStep = usPostUninstall) and not HasSwitch('KEEPDATA') then
  begin
    DelTree(ExpandConstant('{localappdata}\Clipsy'), True, True, True);
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Clipsy');
  end;
end;
