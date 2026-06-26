; Inno Setup script for Job Radar (Windows, per-user install — no admin needed).
; App state (profile, settings, plan, db) lives in %APPDATA%\JobRadar, so the
; install directory can stay read-only.

#ifndef MyVersion
  #define MyVersion "0.0.0"
#endif
#define MyName "Job Radar"
#define MyExe "JobRadar.Desktop.exe"

[Setup]
AppId={{B6F4E9A2-7C3D-4E2A-9E0B-9A1C5D2F3A10}
AppName={#MyName}
AppVersion={#MyVersion}
AppPublisher=Gabriel Teixeira
DefaultDirName={localappdata}\Programs\Job Radar
DefaultGroupName=Job Radar
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\dist
OutputBaseFilename=JobRadar-Setup-{#MyVersion}-win-x64
SetupIconFile=..\..\src\JobRadar.Desktop\Assets\radar.ico
UninstallDisplayIcon={app}\{#MyExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\..\publish\win\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Job Radar"; Filename: "{app}\{#MyExe}"
Name: "{autodesktop}\Job Radar"; Filename: "{app}\{#MyExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyExe}"; Description: "{cm:LaunchProgram,Job Radar}"; Flags: nowait postinstall skipifsilent
