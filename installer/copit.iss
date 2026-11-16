; Copit Installer (Inno Setup 6) – wird im Release-Workflow gebaut:
;   iscc /DAppVersion=1.2.3 /DSourceDir=..\artifacts\publish installer\copit.iss
; Installation pro Benutzer (keine Admin-Rechte), Start-Menü + optional Desktop-Verknüpfung.
; Der Auto-Updater in Copit startet diesen Installer mit /VERYSILENT – danach startet Copit neu.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{7B3E2C41-6F0A-4C1E-9B7D-C0F1A7E5D2B9}
AppName=Copit
AppVersion={#AppVersion}
AppVerName=Copit {#AppVersion}
AppPublisher=Copit
AppPublisherURL=https://github.com/Copit-Code/Copit
DefaultDirName={localappdata}\Programs\Copit
DefaultGroupName=Copit
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=Copit-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Copit.exe
UninstallDisplayName=Copit
CloseApplications=force
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; alte Programmdateien entfernen (Benutzerdaten liegen unter %LOCALAPPDATA%\Copit und bleiben erhalten)
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{group}\Copit"; Filename: "{app}\Copit.exe"
Name: "{group}\Copit deinstallieren"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Copit"; Filename: "{app}\Copit.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Copit.exe"; Description: "Copit starten"; Flags: nowait postinstall skipifsilent
; nach einem stillen Update (Auto-Updater) Copit direkt wieder starten
Filename: "{app}\Copit.exe"; Flags: nowait runasoriginaluser; Check: WizardSilent
