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
UninstallDisplayIcon={app}\CodexUsageBar.WatcherHost.exe
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
UsePreviousTasks=yes
SetupLogging=yes
MinVersion=10.0

[Tasks]
Name: "startup"; Description: "Keep Codex Usage Bar active when I sign in"; GroupDescription: "Background integration:"; Flags: checkedonce

[Files]
; Bootstrap from a temporary payload before Inno commits installed application files.
Source: "{#StageRoot}\setup-bootstrap.ps1"; DestDir: "{tmp}"; Flags: dontcopy noencryption
Source: "{#StageRoot}\payload\*"; DestDir: "{tmp}\payload"; Flags: dontcopy noencryption recursesubdirs createallsubdirs
Source: "{#StageRoot}\setup-bootstrap.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\CodexUsageBar.WatcherHost.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\codex-usage-bar.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\payload\*"; DestDir: "{app}\payload"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Remove the v0.4.13 Startup shortcut that launched hidden PowerShell.
Type: files; Name: "{userstartup}\Codex Usage Bar.lnk"

[Icons]
Name: "{group}\Codex Usage Bar"; Filename: "{app}\CodexUsageBar.WatcherHost.exe"; WorkingDir: "{app}"; IconFilename: "{app}\CodexUsageBar.WatcherHost.exe"

[Registry]
; Use the normal per-user Run key with a native host. v0.4.13 used a Startup
; shortcut whose target was hidden powershell.exe; that pattern is intentionally
; removed because AV products commonly classify it as suspicious persistence.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Codex Usage Bar"; ValueData: """{app}\CodexUsageBar.WatcherHost.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\CodexUsageBar.WatcherHost.exe"; WorkingDir: "{app}"; Description: "Start Codex Usage Bar"; Flags: nowait postinstall skipifsilent

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
  ExtractTemporaryFiles('{tmp}\payload\*');
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
