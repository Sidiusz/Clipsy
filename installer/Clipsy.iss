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
; CloseApplications covers the case where the app is still holding files.
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
Filename: "{app}\{#ClipsyExeName}"; Parameters: "autostart-init --remove"; Flags: runhidden; RunOnceId: "ClipsyAutostartCleanup"

[Code]
const
  LegacyAutostartTaskName = 'ClipsyAutostart';

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

// Ask a running Clipsy to exit cleanly (it finishes a recording first) before files are replaced;
// Restart Manager (CloseApplications) is the fallback.
procedure QuitRunningClipsy;
var
  ResultCode: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#ClipsyExeName}');
  if FileExists(Exe) then
    Exec(Exe, 'quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningClipsy;
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
    QuitRunningClipsy;
    DeleteLegacyAutostartTask;
  end;
  // [UninstallDelete] Check functions run at install time, so /KEEPDATA must be read here.
  if (CurUninstallStep = usPostUninstall) and not HasSwitch('KEEPDATA') then
  begin
    DelTree(ExpandConstant('{localappdata}\Clipsy'), True, True, True);
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\Clipsy');
  end;
end;
