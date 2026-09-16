; Compile with: ISCC.exe installer\Clipsy.iss
; Expects publish output at: Clipsy\bin\publish\win-x64

#define ClipsyName "Clipsy"
#ifndef ClipsyVersion
#define ClipsyVersion "1.0.5"
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
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
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
Source: "{#ClipsyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ClipsyName}"; Filename: "{app}\{#ClipsyExeName}"
Name: "{group}\Uninstall {#ClipsyName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#ClipsyName}"; Filename: "{app}\{#ClipsyExeName}"; Tasks: desktopicon

[Registry]
; Autostart uses the current user Run key; no elevation is required.

; WER LocalDumps: capture a full minidump even on native __fastfail
; (0xc0000409) crashes that bypass the in-app exception filter. Dumps land
; next to debug.log so a silent vanish always leaves post-mortem evidence.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{#ClipsyExeName}"; \
    ValueType: expandsz; ValueName: "DumpFolder"; ValueData: "%LOCALAPPDATA%\Clipsy\CrashDumps"; \
    Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{#ClipsyExeName}"; \
    ValueType: dword; ValueName: "DumpType"; ValueData: "$00000002"
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\{#ClipsyExeName}"; \
    ValueType: dword; ValueName: "DumpCount"; ValueData: "$00000005"

[Run]
Filename: "{app}\{#ClipsyExeName}"; Description: "{cm:LaunchProgram,{#ClipsyName}}"; \
    Flags: nowait postinstall skipifsilent; Check: ShouldLaunchAfterInstall

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Clipsy"; Check: ShouldDeleteUserData

[Code]
const
  LegacyAutostartTaskName = 'ClipsyAutostart';
  RunSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run';

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

function ShouldDeleteUserData(): Boolean;
begin
  Result := not HasSwitch('KEEPDATA');
end;

procedure CreateAutostart;
begin
  RegWriteStringValue(HKCU, RunSubkey, 'Clipsy', '"' + ExpandConstant('{app}\{#ClipsyExeName}') + '"');
end;

procedure DeleteAutostart;
var
  ResultCode: Integer;
begin
  RegDeleteValue(HKCU, RunSubkey, 'Clipsy');
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "' + LegacyAutostartTaskName + '" /F', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

function AutostartOptedOut: Boolean;
var
  v: Cardinal;
begin
  // App sets this when the user disables autostart; absent = enable by default.
  Result := RegQueryDWordValue(HKCU, 'Software\Clipsy', 'AutostartOptOut', v) and (v = 1);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    begin
      DeleteAutostart;
      if HasSwitch('NOAUTOSTART') then
        RegWriteDWordValue(HKCU, 'Software\Clipsy', 'AutostartOptOut', 1)
      else if not AutostartOptedOut then
        CreateAutostart;
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    DeleteAutostart;
end;
