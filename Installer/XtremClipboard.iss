; Script Inno Setup 6 - Xtrem Clipboard
; Ne pas compiler a la main : utiliser BUILD-SETUP.bat a la racine.
; App autonome de la suite Xtrem : AppId propre, aucun fichier partage.

#ifndef MyAppVersion
#define MyAppVersion "0.11.0"
#endif

#define MyAppName "Xtrem Clipboard"
#define MyAppPublisher "valloue.fr"
#define MyAppURL "https://xtrem.valloue.fr"
#define MyAppExeName "XtremClipboard.exe"
#define PublishDir "..\artifacts\publish\win-x64"

[Setup]
AppId={{19D3F6E7-2082-4346-8FB5-62AE3170B395}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.\Output
OutputBaseFilename=XtremClipboard-Setup-{#MyAppVersion}
SetupIconFile=..\src\XtremClipboard\Assets\App.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartIfNeededByRun=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstaller {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Wizard : case "Lancer Xtrem Clipboard" (ignoree en /VERYSILENT).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Auto-update silencieux : relance l'app apres /VERYSILENT (regle auto-update.md, D-UPDATE).
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifnotsilent



