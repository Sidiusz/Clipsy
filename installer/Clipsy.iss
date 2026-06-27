; Compile with: ISCC.exe installer\Clipsy.iss
; Expects publish output at: Clipsy\bin\publish\win-x64

#define ClipsyName "Clipsy"
#ifndef ClipsyVersion
#define ClipsyVersion "1.0.0"
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
Name: "startuplogin"; Description: "Run Clipsy at sign-in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#ClipsyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ClipsyName}"; Filename: "{app}\{#ClipsyExeName}"
Name: "{group}\Uninstall {#ClipsyName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#ClipsyName}"; Filename: "{app}\{#ClipsyExeName}"; Tasks: desktopicon

[Registry]
; Clipsy runs elevated (requireAdministrator). A Run-key entry can't auto-elevate
; at login (UAC blocks it), so autostart is a Scheduled Task with the highest run
; level instead — created/removed in [Code] below.

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
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Clipsy"

[Code]
const
  AutostartTaskName = 'ClipsyAutostart';

procedure CreateAutostartTask;
var
  ExePath, Params: string;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#ClipsyExeName}');
  // /TR needs the action path inner-quoted ("\"path\"") or schtasks truncates
  // it at the first space. /RL HIGHEST runs elevated; /SC ONLOGON + /RU fires
  // at this user's sign-in. The setup is elevated, so creation succeeds.
  Params := '/Create /TN "' + AutostartTaskName + '" /TR "\"' + ExePath + '\"" ' +
            '/SC ONLOGON /RU "' + ExpandConstant('{username}') + '" /RL HIGHEST /F';
  Exec(ExpandConstant('{sys}\schtasks.exe'), Params, '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

procedure DeleteAutostartTask;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'),
       '/Delete /TN "' + AutostartTaskName + '" /F', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and IsTaskSelected('startuplogin') then
    CreateAutostartTask;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    DeleteAutostartTask;
end;







