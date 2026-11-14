; Compile through scripts/Build-Installer.ps1. Values are supplied by the release build.
; This installer never requests elevation and never removes LocalAppData\GlimHUD settings.
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must point to a complete self-contained win-x64 publish
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be supplied by Build-Installer.ps1
#endif
#ifndef ProjectDir
  #error ProjectDir must be supplied by Build-Installer.ps1
#endif

[Setup]
AppId={{9316D4E1-267F-4DB8-B580-BAF8308BDE79}
AppName=GlimHUD
AppVersion={#AppVersion}
AppVerName=GlimHUD {#AppVersion}
AppPublisher=pyrrhuladev
AppPublisherURL=https://github.com/pyrrhuladev/GlimHUD
AppSupportURL=https://github.com/pyrrhuladev/GlimHUD/issues
AppUpdatesURL=https://github.com/pyrrhuladev/GlimHUD/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=GlimHUD Setup
VersionInfoProductName=GlimHUD
DefaultDirName={localappdata}\Programs\GlimHUD
DefaultGroupName=GlimHUD
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableWelcomePage=no
LicenseFile={#ProjectDir}\LICENSE
SetupIconFile={#ProjectDir}\src\GlimHUD\Assets\GlimHUD.ico
UninstallDisplayIcon={app}\GlimHUD.exe
UninstallDisplayName=GlimHUD
OutputDir={#InstallerOutputDir}
OutputBaseFilename=GlimHUD-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\GlimHUD.Desktop
CloseApplications=yes
CloseApplicationsFilter=GlimHUD.exe
RestartApplications=no
RestartIfNeededByRun=no
UninstallRestartComputer=no
ChangesEnvironment=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.DesktopIcon=Create a &desktop shortcut
russian.DesktopIcon=Создать ярлык на &рабочем столе
english.LaunchGlimHUD=Launch GlimHUD
russian.LaunchGlimHUD=Запустить GlimHUD

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,installed.marker"
Source: "{#ProjectDir}\packaging\installer\installed.marker"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GlimHUD"; Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GlimHUD"; Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchGlimHUD}"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser
Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent runasoriginaluser; Check: ShouldRestartAfterUpdate

[Code]
function ShouldRestartAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:GLIMHUDRESTART|0}') = '1';
end;
