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
#define PawnIOSetupSHA256 "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"

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
english.OptionalDependencies=Optional hardware support:
russian.OptionalDependencies=Дополнительная поддержка оборудования:
english.InstallPawnIO=Install or upgrade PawnIO 2.2.0 for hardware temperatures (requires Windows administrator approval)
russian.InstallPawnIO=Установить или обновить PawnIO 2.2.0 для аппаратных температур (требуется разрешение администратора Windows)
english.PawnIOCancelled=PawnIO installation was not approved. GlimHUD has been installed, but some hardware temperatures may remain unavailable. You can install PawnIO later from GlimHUD settings.
russian.PawnIOCancelled=Установка PawnIO не была разрешена. GlimHUD установлен, но некоторые аппаратные температуры могут быть недоступны. Установить PawnIO можно позже в настройках GlimHUD.
english.PawnIOFailed=PawnIO setup did not complete (code %1). GlimHUD has been installed, but some hardware temperatures may remain unavailable. Check hardware support in GlimHUD settings.
russian.PawnIOFailed=Установка PawnIO не завершена (код %1). GlimHUD установлен, но некоторые аппаратные температуры могут быть недоступны. Проверьте поддержку оборудования в настройках GlimHUD.
english.PawnIOUnknown=The existing PawnIO version could not be verified, so it was left unchanged. GlimHUD has been installed. Check hardware support in GlimHUD settings if temperatures are unavailable.
russian.PawnIOUnknown=Не удалось проверить версию установленного PawnIO, поэтому он оставлен без изменений. GlimHUD установлен. Если температуры недоступны, проверьте поддержку оборудования в настройках GlimHUD.
english.PawnIOReboot=PawnIO setup reported that Windows needs to be restarted. Windows will not restart automatically. Some sensors may remain unavailable until you restart Windows yourself.
russian.PawnIOReboot=Установщик PawnIO сообщил о необходимости перезапуска Windows. Автоматического перезапуска не будет. Некоторые датчики могут быть недоступны до самостоятельного перезапуска Windows.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "pawnio"; Description: "{cm:InstallPawnIO}"; GroupDescription: "{cm:OptionalDependencies}"; Check: NeedsPawnIOInstall

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,installed.marker"
Source: "{#ProjectDir}\packaging\installer\installed.marker"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\tools\PawnIO\PawnIO_setup.exe"; DestName: "GlimHUD-PawnIO_setup.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\GlimHUD"; Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GlimHUD"; Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchGlimHUD}"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser
Filename: "{app}\GlimHUD.exe"; WorkingDir: "{app}"; Flags: nowait skipifnotsilent runasoriginaluser; Check: ShouldRestartAfterUpdate

[Code]
const
  PawnIOUninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  PawnIOMissing = 0;
  PawnIOOlder = 1;
  PawnIOCurrent = 2;
  PawnIOUnknown = 3;

function OpenRegistryKey(RootKey: LongWord; SubKey: String; Options, Access: Cardinal; var Key: LongWord): Integer;
  external 'RegOpenKeyExW@advapi32.dll stdcall';
function CloseRegistryKey(Key: LongWord): Integer;
  external 'RegCloseKey@advapi32.dll stdcall';
function OpenPawnIOFile(FileName: String; DesiredAccess, ShareMode, SecurityAttributes,
  CreationDisposition, Flags, TemplateFile: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function ClosePawnIOFile(Handle: LongWord): Integer;
  external 'CloseHandle@kernel32.dll stdcall';

function IsStrictVersion(const Text: String; var Packed: Int64): Boolean;
var
  Value: String;
  I, Dots: Integer;
begin
  Result := False;
  Value := Trim(Text);
  if Value = '' then Exit;
  if (Value[1] = '.') or (Value[Length(Value)] = '.') or (Pos('..', Value) > 0) then Exit;
  Dots := 0;
  for I := 1 to Length(Value) do
  begin
    if Value[I] = '.' then Dots := Dots + 1
    else if (Value[I] < '0') or (Value[I] > '9') then Exit;
  end;
  if Dots > 3 then Exit;
  Result := StrToVersion(Value, Packed);
end;

function ReadPawnIOView(const Root: LongWord; const ViewFlag: Cardinal; var Packed: Int64): Integer;
var
  Key: LongWord;
  Error: Integer;
  VersionText: String;
begin
  { 0 = missing, 1 = a known version, 2 = existing/unreadable/unknown. }
  Result := 2;
  Error := OpenRegistryKey(HKLM, PawnIOUninstallKey, 0, $0001 or ViewFlag, Key);
  if (Error = 2) or (Error = 3) then
  begin
    Result := 0;
    Exit;
  end;
  if Error <> 0 then Exit;
  CloseRegistryKey(Key);
  if RegQueryStringValue(Root, PawnIOUninstallKey, 'DisplayVersion', VersionText)
     and IsStrictVersion(VersionText, Packed) then Result := 1;
end;

function ReadPawnIOState(): Integer;
var
  State64, State32: Integer;
  Version64, Version32, Required: Int64;
begin
  Version64 := 0;
  Version32 := 0;
  StrToVersion('2.2.0.0', Required);
  State64 := ReadPawnIOView(HKLM64, $0100, Version64);
  State32 := ReadPawnIOView(HKLM32, $0200, Version32);
  if ((State64 = 1) and (ComparePackedVersion(Version64, Required) >= 0))
     or ((State32 = 1) and (ComparePackedVersion(Version32, Required) >= 0)) then
    Result := PawnIOCurrent
  else if (State64 = 2) or (State32 = 2) then Result := PawnIOUnknown
  else if (State64 = 1) or (State32 = 1) then Result := PawnIOOlder
  else Result := PawnIOMissing;
end;

function NeedsPawnIOInstall(): Boolean;
var
  State: Integer;
begin
  State := ReadPawnIOState();
  Result := (State = PawnIOMissing) or (State = PawnIOOlder);
end;

procedure ReportPawnIOMessage(const Text: String);
begin
  Log(Text);
  if not WizardSilent then SuppressibleMsgBox(Text, mbInformation, MB_OK, IDOK);
end;

procedure InstallSelectedPawnIO();
var
  State, ResultCode: Integer;
  SetupPath: String;
  LockedFile: LongWord;
begin
  State := ReadPawnIOState();
  if State = PawnIOCurrent then
  begin
    Log('PawnIO 2.2.0 or newer is already installed; unchanged.');
    Exit;
  end;
  if State = PawnIOUnknown then
  begin
    ReportPawnIOMessage(CustomMessage('PawnIOUnknown'));
    Exit;
  end;
  if not WizardIsTaskSelected('pawnio') then
  begin
    Log('Optional PawnIO task was not selected; no driver changes.');
    Exit;
  end;
  try
    ExtractTemporaryFile('GlimHUD-PawnIO_setup.exe');
    SetupPath := ExpandConstant('{tmp}\GlimHUD-PawnIO_setup.exe');
    { GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL.
      The verified bytes cannot be rewritten, renamed or deleted while Windows
      displays consent or while the original vendor setup is running. }
    LockedFile := OpenPawnIOFile(SetupPath, $80000000, $00000001, 0, 3, $80, 0);
    if LockedFile = $FFFFFFFF then RaiseException('Could not lock bundled PawnIO setup.');
    try
      if CompareText(GetSHA256OfFile(SetupPath), '{#PawnIOSetupSHA256}') <> 0 then
        RaiseException('Bundled PawnIO setup verification failed.');
      { The original signed vendor installer is run unchanged. The per-user
        GlimHUD installer stays unelevated; Windows asks consent for this child. }
      if not ShellExec('runas', SetupPath, '-install -silent', ExpandConstant('{tmp}'),
         SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      begin
        if ResultCode = 1223 then ReportPawnIOMessage(CustomMessage('PawnIOCancelled'))
        else ReportPawnIOMessage(FmtMessage(CustomMessage('PawnIOFailed'), [IntToStr(ResultCode)]));
        Exit;
      end;
      if ResultCode = 3010 then ReportPawnIOMessage(CustomMessage('PawnIOReboot'))
      else if ResultCode <> 0 then ReportPawnIOMessage(FmtMessage(CustomMessage('PawnIOFailed'), [IntToStr(ResultCode)]))
      else if ReadPawnIOState() <> PawnIOCurrent then
        ReportPawnIOMessage(FmtMessage(CustomMessage('PawnIOFailed'), ['unverified']))
      else Log('PawnIO vendor setup completed; hardware availability is checked by GlimHUD.');
    finally
      ClosePawnIOFile(LockedFile);
    end;
  except
    Log('Optional PawnIO setup failed: ' + GetExceptionMessage());
    ReportPawnIOMessage(FmtMessage(CustomMessage('PawnIOFailed'), ['verification/launch']));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallSelectedPawnIO();
end;

function ShouldRestartAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:GLIMHUDRESTART|0}') = '1';
end;
