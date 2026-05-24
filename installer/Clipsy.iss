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
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "Clipsy"; ValueData: """{app}\{#ClipsyExeName}"""; \
    Tasks: startuplogin; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#ClipsyExeName}"; Description: "{cm:LaunchProgram,{#ClipsyName}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Clipsy"





