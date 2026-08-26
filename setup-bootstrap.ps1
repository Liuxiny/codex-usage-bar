[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
$script:StateRoot = Join-Path $env:LOCALAPPDATA 'CodexUsageBar'
$script:AppRoot = Join-Path $env:LOCALAPPDATA 'Programs\CodexUsageBar'
$script:SetupLog = Join-Path $script:StateRoot 'setup.log'

function Write-SetupLog([string]$Message) {
    try {
        New-Item -ItemType Directory -Force -Path $script:StateRoot | Out-Null
        Add-Content -LiteralPath $script:SetupLog -Value ('{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f [DateTime]::Now, $Message) -Encoding UTF8
    } catch { }
}

function Show-SetupError([string]$Message) {
    if ($Silent) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show(
            $Message,
            'Codex Usage Bar',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
    } catch { }
}

function Get-ProcessPath([int]$ProcessId) {
    try { return [string](Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop).ExecutablePath }
    catch { return $null }
}

function Test-SamePath([string]$Left, [string]$Right) {
    if (-not $Left -or -not $Right) { return $false }
    try {
        return [string]::Equals([IO.Path]::GetFullPath($Left), [IO.Path]::GetFullPath($Right), [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Stop-RecordedProcess([string]$StateFile, [string]$PidProperty, [string]$PathProperty) {
    if (-not (Test-Path -LiteralPath $StateFile -PathType Leaf)) { return }
    try {
        $state = Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $processId = [int]$state.$PidProperty
        $expected = [string]$state.$PathProperty
        $actual = Get-ProcessPath $processId
        if ($processId -gt 0 -and (Test-SamePath $expected $actual)) {
            Stop-Process -Id $processId -Force -ErrorAction Stop
            Write-SetupLog "Stopped PID=$processId path=$actual"
        }
    } catch { Write-SetupLog "Could not stop recorded process from $StateFile : $($_.Exception.Message)" }
}

function Stop-InstalledCompanion {
    $companion = Join-Path $script:AppRoot 'CodexUsageBar.exe'
    if (Test-Path -LiteralPath $companion -PathType Leaf) {
        try {
            $request = Start-Process -FilePath $companion -ArgumentList '--exit' -WindowStyle Hidden -PassThru
            [void]$request.WaitForExit(3000)
        } catch { Write-SetupLog "Companion exit request failed: $($_.Exception.Message)" }
    }
    Stop-RecordedProcess (Join-Path $script:StateRoot 'companion-state.json') 'pid' 'path'
}

function Stop-LegacyRuntime {
    $engine = Join-Path $script:StateRoot 'engine\codex-usage-bar.ps1'
    if (Test-Path -LiteralPath $engine -PathType Leaf) {
        try {
            & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File $engine -Stop
        } catch { Write-SetupLog "Legacy engine stop failed: $($_.Exception.Message)" }
    }
    Stop-RecordedProcess (Join-Path $script:StateRoot 'watcher-host.json') 'watcherPid' 'hostPath'
    Stop-RecordedProcess (Join-Path $script:StateRoot 'state.json') 'injectorPid' 'nodePath'
    Stop-RecordedProcess (Join-Path $script:StateRoot 'watcher.json') 'watcherPid' 'powershellPath'
}

function Remove-LegacyState {
    foreach ($path in @(
        (Join-Path $script:StateRoot 'engine'),
        (Join-Path $script:StateRoot 'state.json'),
        (Join-Path $script:StateRoot 'watcher.json'),
        (Join-Path $script:StateRoot 'watcher-host.json'),
        (Join-Path $script:StateRoot 'watcher-fuse.json'),
        (Join-Path $script:StateRoot 'refresh.request'),
        (Join-Path $script:StateRoot 'locale.current'),
        (Join-Path $script:StateRoot 'install.json')
    )) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

try {
    if ($Install.IsPresent -eq $Uninstall.IsPresent) { throw 'Choose exactly one setup action: -Install or -Uninstall.' }
    Stop-InstalledCompanion
    Stop-LegacyRuntime
    Remove-LegacyState
    if ($Uninstall -and (Test-Path -LiteralPath $script:StateRoot -PathType Container)) {
        Write-SetupLog 'Completing uninstall cleanup'
        $resolved = [IO.Path]::GetFullPath($script:StateRoot)
        $expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexUsageBar'))
        if (-not [string]::Equals($resolved, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected state path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
        exit 0
    }
    Write-SetupLog 'Prepared v0.6.0 installation'
    exit 0
} catch {
    Write-SetupLog "ERROR $($_.Exception.Message)"
    Show-SetupError $_.Exception.Message
    Write-Error $_
    exit 1
}
