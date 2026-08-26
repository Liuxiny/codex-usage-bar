#ifndef AppVersion
  #error AppVersion must be supplied by build-release.ps1
#endif
#ifndef StageRoot
  #error StageRoot must be supplied by build-release.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-release.ps1
#endif

#define AppName "Codex Usage Bar"
#define AppPublisher "Codex Usage Bar"
#define PowerShellPath "{sysnative}\WindowsPowerShell\v1.0\powershell.exe"

[Setup]
AppId={{D15748EE-3FD8-4AC2-9585-9C4911081A6A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\CodexUsageBar
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=CodexUsageBar-Setup-v{#AppVersion}
SetupIconFile={#StageRoot}\codex-usage-bar.ico
UninstallDisplayIcon={app}\CodexUsageBar.exe
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
UsePreviousTasks=yes
SetupLogging=yes
MinVersion=10.0

[Tasks]
Name: "startup"; Description: "Keep Codex Usage Bar active when I sign in"; GroupDescription: "Background integration:"; Flags: checkedonce

[Files]
; Stop legacy CDP builds before Inno replaces their files.
Source: "{#StageRoot}\setup-bootstrap.ps1"; DestDir: "{tmp}"; Flags: dontcopy noencryption
Source: "{#StageRoot}\setup-bootstrap.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\CodexUsageBar.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\codex-usage-bar.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\VERSION"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\INSTALL-WINDOWS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\CODEX-THEME-SPEC.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Remove the v0.4.13 Startup shortcut that launched hidden PowerShell.
Type: files; Name: "{userstartup}\Codex Usage Bar.lnk"
; Remove v0.4.x CDP payload and watcher after the bootstrap has stopped them.
Type: files; Name: "{app}\CodexUsageBar.WatcherHost.exe"
Type: filesandordirs; Name: "{app}\payload"

[Icons]
Name: "{group}\Codex Usage Bar"; Filename: "{app}\CodexUsageBar.exe"; WorkingDir: "{app}"; IconFilename: "{app}\CodexUsageBar.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Codex Usage Bar"; ValueData: """{app}\CodexUsageBar.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\CodexUsageBar.exe"; WorkingDir: "{app}"; Description: "Start Codex Usage Bar"; Flags: nowait postinstall skipifsilent

[Code]
function BootstrapArguments(const ScriptPath: String; const Action: String; const Silent: Boolean): String;
begin
  Result := '-NoProfile -WindowStyle Hidden -ExecutionPolicy RemoteSigned -File ' + AddQuotes(ScriptPath) + ' ' + Action;
  if Silent then
    Result := Result + ' -Silent';
end;

function RunBootstrap(const ScriptPath: String; const Action: String; const Silent: Boolean; var ExitCode: Integer): Boolean;
begin
  Result := Exec(
    ExpandConstant('{#PowerShellPath}'),
    BootstrapArguments(ScriptPath, Action, Silent),
    ExtractFileDir(ScriptPath),
    SW_HIDE,
    ewWaitUntilTerminated,
    ExitCode
  );
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  TemporaryBootstrap: String;
begin
  if CurStep <> ssInstall then
    exit;

  ExtractTemporaryFiles('{tmp}\setup-bootstrap.ps1');
  TemporaryBootstrap := ExpandConstant('{tmp}\setup-bootstrap.ps1');
  if not RunBootstrap(TemporaryBootstrap, '-Install', WizardSilent, ExitCode) then
    RaiseException('Codex Usage Bar initialization could not be started.');
  if ExitCode <> 0 then
    RaiseException('Codex Usage Bar initialization failed. No Codex application files were modified.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;

  if not RunBootstrap(ExpandConstant('{app}\setup-bootstrap.ps1'), '-Uninstall', True, ExitCode) then
    RaiseException('Codex Usage Bar cleanup could not be started. Installed files were not removed.');
  if ExitCode <> 0 then
    RaiseException('Codex Usage Bar cleanup failed. Installed files were not removed.');
end;
