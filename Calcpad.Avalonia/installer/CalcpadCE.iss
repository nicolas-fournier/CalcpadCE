; Inno Setup script for the CalcpadCE (Avalonia) desktop app.
; Build with: iscc /DAppVer=<version> CalcpadCE.iss   (or run build-installer.ps1)

#ifndef AppVer
  #define AppVer "7.6.5-preview"
#endif
#define AppName "CalcpadCE"
#define AppExe "CalcpadAvalonia.exe"
#define Publisher "CalcpadCE community"
#define SrcDir "..\bin\Release\net10.0\win-x64\publish"
#define IcoFile "..\..\Calcpad.Wpf\resources\calcpad.ico"

[Setup]
AppId={{B6E2F1A4-9C3D-4E57-B0A1-CA1CDCE00001}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher={#Publisher}
AppSupportURL=https://github.com/nicolas-fournier/CalcpadCE
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=CalcpadCE-{#AppVer}-setup
SetupIconFile={#IcoFile}
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Associate .cpd worksheets and .cpdz packages with the app (per-user).
Root: HKA; Subkey: "Software\Classes\.cpd";  ValueType: string; ValueName: ""; ValueData: "CalcpadCE.Worksheet"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.cpdz"; ValueType: string; ValueName: ""; ValueData: "CalcpadCE.Package";   Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\CalcpadCE.Worksheet"; ValueType: string; ValueName: ""; ValueData: "Calcpad worksheet"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CalcpadCE.Worksheet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\CalcpadCE.Worksheet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\CalcpadCE.Package"; ValueType: string; ValueName: ""; ValueData: "Calcpad package"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CalcpadCE.Package\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\CalcpadCE.Package\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
