[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$LaunchWatcher,
    [switch]$Uninstall,
    [switch]$Silent,
    [int]$CdpPort = 9335
)

$ErrorActionPreference = 'Stop'
$script:StateRoot = Join-Path $env:LOCALAPPDATA 'CodexUsageBar'
$script:EngineRoot = Join-Path $script:StateRoot 'engine'
$script:InstallState = Join-Path $script:StateRoot 'install.json'
$script:PayloadRoot = Join-Path $PSScriptRoot 'payload'
$script:SetupLog = Join-Path $script:StateRoot 'setup.log'
$script:InstalledAppRoot = Join-Path $env:LOCALAPPDATA 'Programs\CodexUsageBar'
$script:WatcherHostPath = Join-Path $script:InstalledAppRoot 'CodexUsageBar.WatcherHost.exe'
$script:WatcherHostState = Join-Path $script:StateRoot 'watcher-host.json'

function Write-SetupLog([string]$Message) {
    try {
        New-Item -ItemType Directory -Force -Path $script:StateRoot | Out-Null
        $line = '{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f [DateTime]::Now, $Message
        Add-Content -LiteralPath $script:SetupLog -Value $line -Encoding UTF8
    } catch { }
}

function Show-SetupMessage([string]$Message, [switch]$Error) {
    if ($Silent) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $icon = if ($Error) { [System.Windows.Forms.MessageBoxIcon]::Error } else { [System.Windows.Forms.MessageBoxIcon]::Information }
        [void][System.Windows.Forms.MessageBox]::Show(
            $Message,
            'Codex Usage Bar',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            $icon)
    } catch { }
}

function Assert-Port([int]$Port) {
    if ($Port -lt 1024 -or $Port -gt 65535) { throw "CDP port must be between 1024 and 65535: $Port" }
}

function Get-InstalledPort {
    if (Test-Path -LiteralPath $script:InstallState -PathType Leaf) {
        try {
            $state = Get-Content -LiteralPath $script:InstallState -Raw -Encoding UTF8 | ConvertFrom-Json
            $value = [int]$state.cdpPort
            if ($value -ge 1024 -and $value -le 65535) { return $value }
        } catch { }
    }
    return $CdpPort
}

function Get-ProcessExecutablePath([int]$ProcessId) {
    try {
        return [string](Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop).ExecutablePath
    } catch { return $null }
}

function Test-SamePath([string]$Left, [string]$Right) {
    if (-not $Left -or -not $Right) { return $false }
    try {
        return [string]::Equals(
            [IO.Path]::GetFullPath($Left).TrimEnd('\'),
            [IO.Path]::GetFullPath($Right).TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function Stop-StateProcess([string]$StateFile, [string]$PidProperty, [string]$PathProperty) {
    if (-not (Test-Path -LiteralPath $StateFile -PathType Leaf)) { return }
    try {
        $state = Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $pidValue = [int]$state.$PidProperty
        $expectedPath = [string]$state.$PathProperty
        if ($pidValue -le 0 -or -not $expectedPath) { return }
        $actualPath = Get-ProcessExecutablePath $pidValue
        if ($actualPath -and (Test-SamePath $actualPath $expectedPath)) {
            Stop-Process -Id $pidValue -Force -ErrorAction Stop
            Write-SetupLog "Stopped managed process PID=$pidValue path=$actualPath"
        }
    } catch {
        Write-SetupLog "Managed process stop fallback failed for $StateFile : $($_.Exception.Message)"
    }
}

function Invoke-Engine([string[]]$Arguments, [switch]$IgnoreFailure) {
    $scriptPath = Join-Path $script:EngineRoot 'codex-usage-bar.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { return $false }
    try {
        & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File $scriptPath @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Engine exited with code $LASTEXITCODE" }
        return $true
    } catch {
        Write-SetupLog "Engine action failed: $($_.Exception.Message)"
        if (-not $IgnoreFailure) { throw }
        return $false
    }
}

function Stop-ManagedRuntime([int]$Port) {
    # Stop the native watcher host first so it cannot race an uninstall/update by
    # launching a fresh attach child while the engine is being replaced.
    Stop-StateProcess $script:WatcherHostState 'watcherPid' 'hostPath'

    # Ask the installed engine to remove the live DOM and stop its injector.
    # Legacy v0.4.13 watcher cleanup remains for upgrade compatibility.
    [void](Invoke-Engine @('-Stop', '-CdpPort', "$Port") -IgnoreFailure)
    [void](Invoke-Engine @('-StopWatcher', '-CdpPort', "$Port") -IgnoreFailure)
    Stop-StateProcess (Join-Path $script:StateRoot 'state.json') 'injectorPid' 'nodePath'
    Stop-StateProcess (Join-Path $script:StateRoot 'watcher.json') 'watcherPid' 'powershellPath'
    Remove-Item -LiteralPath (Join-Path $script:StateRoot 'state.json') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $script:StateRoot 'watcher.json') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $script:WatcherHostState -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $script:StateRoot 'watcher-fuse.json') -Force -ErrorAction SilentlyContinue
}

function Get-PayloadVersion {
    $path = Join-Path $script:PayloadRoot 'VERSION'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Installer payload is missing VERSION.' }
    $version = ([IO.File]::ReadAllText($path)).Trim()
    if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Installer payload version is invalid: $version"
    }
    return $version
}

function Assert-PayloadComplete([string]$Root) {
    foreach ($relative in @('VERSION', 'codex-usage-bar.ps1', 'injector.mjs', 'renderer-inject.js', 'locales\en.json', 'locales\zh.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf)) {
            throw "Installer payload is incomplete: $relative"
        }
    }
}

function Install-Engine([int]$Port) {
    Assert-Port $Port
    Assert-PayloadComplete $script:PayloadRoot
    $payloadVersion = Get-PayloadVersion
    New-Item -ItemType Directory -Force -Path $script:StateRoot | Out-Null

    $installedVersion = ''
    $installedVersionPath = Join-Path $script:EngineRoot 'VERSION'
    if (Test-Path -LiteralPath $installedVersionPath -PathType Leaf) {
        try { $installedVersion = ([IO.File]::ReadAllText($installedVersionPath)).Trim() } catch { }
    }
    if ($installedVersion -cmatch '^\d+\.\d+\.\d+$' -and ([version]$installedVersion) -gt ([version]$payloadVersion)) {
        throw "Codex Usage Bar v$installedVersion is newer than this installer (v$payloadVersion)."
    }

    Stop-ManagedRuntime (Get-InstalledPort)

    $nonce = [guid]::NewGuid().ToString('N')
    $candidate = Join-Path $script:StateRoot "engine.new-$nonce"
    $backup = Join-Path $script:StateRoot "engine.old-$nonce"
    New-Item -ItemType Directory -Force -Path $candidate | Out-Null
    # Payload is installer-owned, so wildcard expansion is intentional here.
    Copy-Item -Path (Join-Path $script:PayloadRoot '*') -Destination $candidate -Recurse -Force
    Assert-PayloadComplete $candidate

    $hadOld = Test-Path -LiteralPath $script:EngineRoot -PathType Container
    try {
        if ($hadOld) { Move-Item -LiteralPath $script:EngineRoot -Destination $backup -Force }
        Move-Item -LiteralPath $candidate -Destination $script:EngineRoot -Force

        $engineScript = Join-Path $script:EngineRoot 'codex-usage-bar.ps1'
        & powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File $engineScript -SelfTest -CdpPort $Port
        if ($LASTEXITCODE -ne 0) { throw "Installed engine self-test failed with exit code $LASTEXITCODE" }

        $state = [ordered]@{
            schemaVersion = 1
            version = $payloadVersion
            cdpPort = $Port
            installedAt = [DateTimeOffset]::Now.ToString('o')
        }
        $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $script:InstallState -Encoding UTF8

        # User-maintained locale files live outside the managed engine so plugin
        # upgrades do not overwrite them. Built-in en/zh remain versioned in engine/locales.
        $userLocaleDir = Join-Path $script:StateRoot 'locales'
        New-Item -ItemType Directory -Force -Path $userLocaleDir | Out-Null
        $localeReadme = Join-Path $script:EngineRoot 'locales\README.md'
        $userLocaleReadme = Join-Path $userLocaleDir 'README.md'
        if ((Test-Path -LiteralPath $localeReadme -PathType Leaf) -and -not (Test-Path -LiteralPath $userLocaleReadme -PathType Leaf)) {
            Copy-Item -LiteralPath $localeReadme -Destination $userLocaleReadme -Force
        }

        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
        Write-SetupLog "Installed managed engine v$payloadVersion port=$Port"
    } catch {
        $failure = $_
        try {
            if (Test-Path -LiteralPath $script:EngineRoot) { Remove-Item -LiteralPath $script:EngineRoot -Recurse -Force }
            if ($hadOld -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $script:EngineRoot -Force }
            if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Recurse -Force }
            if ($hadOld -and (Test-Path -LiteralPath $script:EngineRoot -PathType Container)) {
                # Failed upgrades should restore the previous always-on behavior too,
                # not merely restore the previous files on disk.
                try { Start-Watcher } catch { Write-SetupLog "Previous watcher relaunch warning: $($_.Exception.Message)" }
            }
        } catch { Write-SetupLog "Rollback warning: $($_.Exception.Message)" }
        throw $failure
    }
}

function Start-Watcher {
    $port = Get-InstalledPort
    Assert-Port $port
    $engineScript = Join-Path $script:EngineRoot 'codex-usage-bar.ps1'
    if (-not (Test-Path -LiteralPath $engineScript -PathType Leaf)) {
        throw 'Codex Usage Bar engine is not installed. Run Setup again.'
    }
    if (-not (Test-Path -LiteralPath $script:WatcherHostPath -PathType Leaf)) {
        throw "Codex Usage Bar native watcher host is missing: $script:WatcherHostPath"
    }

    # v0.4.16 deliberately does not keep hidden PowerShell in Startup. The
    # persistent process is a small .NET Framework Windows executable; it only
    # starts a one-shot PowerShell attach worker when a verified Codex package
    # session appears.
    Start-Process -FilePath $script:WatcherHostPath -WindowStyle Hidden | Out-Null
    Write-SetupLog "Requested native watcher host launch port=$port path=$script:WatcherHostPath"
}

function Uninstall-UsageBar {
    $port = Get-InstalledPort
    Stop-ManagedRuntime $port

    # Usage Bar stores no irreplaceable user content. A single uninstall therefore
    # removes the managed engine, logs, state, and optional config in one operation.
    if (Test-Path -LiteralPath $script:StateRoot) {
        Remove-Item -LiteralPath $script:StateRoot -Recurse -Force -ErrorAction Stop
    }
}

try {
    $actions = @($Install, $LaunchWatcher, $Uninstall) | Where-Object { $_ }
    if (@($actions).Count -ne 1) { throw 'Choose exactly one setup action: -Install, -LaunchWatcher, or -Uninstall.' }

    if ($Install) {
        Install-Engine $CdpPort
        exit 0
    }
    if ($LaunchWatcher) {
        Start-Watcher
        exit 0
    }
    if ($Uninstall) {
        Uninstall-UsageBar
        exit 0
    }
} catch {
    Write-SetupLog "ERROR $($_.Exception.Message)"
    Show-SetupMessage -Message $_.Exception.Message -Error
    Write-Error $_
    exit 1
}
