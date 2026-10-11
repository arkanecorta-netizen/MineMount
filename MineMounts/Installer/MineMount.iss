; MineMount Installer Script
; Requires Inno Setup 6.3+

#define MyAppName "MineMount"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "MineMount Team"
#define MyAppExeName "MineMount.exe"

[Setup]
AppId={{B8F4A2C1-5D6E-7F8A-9B0C-1D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Instalación POR USUARIO (sin UAC): así el auto-updater reemplaza el
; exe sin pedir permisos ni mostrar consolas. No usar {autopf}.
DefaultDirName={localappdata}\Programs\{#MyAppName}
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=
InfoBeforeFile=
InfoAfterFile=
OutputDir=..\Output
OutputBaseFilename=MineMount-Setup
SetupIconFile=..\MineMount\Assets\Icons\minemount.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=MineMount Launcher
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\Publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; NOTA: NO se borra {userappdata}\MineMount al desinstalar.
; Ahí viven los datos del usuario (series, mods, mundos, cuentas, Java,
; Minecraft/Forge descargados). Borrarlos es pérdida de datos.
; Solo se eliminan temporales del updater si quedaron huérfanos.
[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\MineMount\updates\stage"