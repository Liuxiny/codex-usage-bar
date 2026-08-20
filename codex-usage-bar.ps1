[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Launch,
    [switch]$Stop,
    [switch]$SelfTest,
    [switch]$RestartExisting,
    [switch]$Watch,
    [switch]$StopWatcher,
    [switch]$ManagedAttach,
    [int]$CdpPort = 9335
)

$ErrorActionPreference = 'Stop'
$script:Version = '0.4.16'
$script:Root = $PSScriptRoot
$script:Injector = Join-Path $script:Root 'injector.mjs'
$script:Renderer = Join-Path $script:Root 'renderer-inject.js'
$script:StateRoot = Join-Path $env:LOCALAPPDATA 'CodexUsageBar'
$script:StatePath = Join-Path $script:StateRoot 'state.json'
$script:StdoutPath = Join-Path $script:StateRoot 'injector.log'
$script:StderrPath = Join-Path $script:StateRoot 'injector-error.log'
$script:LauncherLog = Join-Path $script:StateRoot 'launcher.log'
$script:WatcherStatePath = Join-Path $script:StateRoot 'watcher.json'
$script:WatcherFusePath = Join-Path $script:StateRoot 'watcher-fuse.json'

New-Item -ItemType Directory -Force -Path $script:StateRoot | Out-Null

function Write-UsageLog([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f [DateTime]::Now, $Message
    Add-Content -LiteralPath $script:LauncherLog -Value $line -Encoding UTF8
    if ($env:CODEX_USAGE_BAR_TRACE -eq '1' -or $Run) { Write-Host $line }
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


function Get-CodexCommandCandidates {
    $candidates = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    $configPath = Join-Path $script:StateRoot 'config.json'
    if (Test-Path -LiteralPath $configPath) {
        try {
            $configured = [string]((Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).codexPath)
            if ($configured -and (Test-Path -LiteralPath $configured) -and $seen.Add($configured)) {
                $candidates.Add([IO.Path]::GetFullPath($configured))
            }
        } catch { }
    }

    if ($env:CODEX_EXECUTABLE -and (Test-Path -LiteralPath $env:CODEX_EXECUTABLE)) {
        $configured = [IO.Path]::GetFullPath($env:CODEX_EXECUTABLE)
        if ($seen.Add($configured)) { $candidates.Add($configured) }
    }

    # Preserve the original plugin's working strategy: prefer launchable CLI
    # shims and explicitly skip Store-protected WindowsApps binaries.
    foreach ($name in @('codex.cmd', 'codex.ps1', 'codex', 'codex.exe')) {
        try {
            foreach ($command in @(Get-Command $name -All -ErrorAction Stop)) {
                $path = if ($command.Source) { $command.Source } else { $command.Path }
                if (-not $path -or $path -match '\\WindowsApps\\') { continue }
                $path = [IO.Path]::GetFullPath($path)
                if ($seen.Add($path)) { $candidates.Add($path) }
            }
        } catch { }
    }

    $desktopBin = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $desktopBin) {
        foreach ($item in @(Get-ChildItem -LiteralPath $desktopBin -Filter 'codex.exe' -File -Recurse -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTimeUtc -Descending)) {
            $path = [IO.Path]::GetFullPath($item.FullName)
            if ($seen.Add($path)) { $candidates.Add($path) }
        }
    }
    return $candidates
}

function Test-CodexCommand([string]$Command) {
    try {
        $info = [Diagnostics.ProcessStartInfo]::new()
        $extension = [IO.Path]::GetExtension($Command)
        if ($extension -ieq '.cmd' -or $extension -ieq '.bat') {
            $info.FileName = $env:ComSpec
            $info.Arguments = '/d /s /c ""' + $Command + '" --version"'
        } elseif ($extension -ieq '.ps1') {
            $info.FileName = 'powershell.exe'
            $info.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $Command + '" --version'
        } else {
            $info.FileName = $Command
            $info.Arguments = '--version'
        }
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        if (-not $process.Start()) { return $false }
        if (-not $process.WaitForExit(5000)) {
            try { $process.Kill() } catch { }
            return $false
        }
        return $process.ExitCode -eq 0
    } catch {
        return $false
    }
}

function Resolve-CodexCommand {
    foreach ($candidate in @(Get-CodexCommandCandidates)) {
        if (Test-CodexCommand $candidate) { return $candidate }
    }
    return $null
}

function Get-CodexInstall {
    $pkg = Get-AppxPackage -Name OpenAI.Codex -ErrorAction Stop |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $pkg) { throw 'OpenAI.Codex Store package is not installed.' }

    $appRoot = Join-Path $pkg.InstallLocation 'app'
    $desktopExe = Join-Path $appRoot 'ChatGPT.exe'
    $codexExe = Join-Path $appRoot 'resources\codex.exe'
    if (-not (Test-Path -LiteralPath $desktopExe -PathType Leaf)) { throw "ChatGPT.exe not found: $desktopExe" }
    if (-not (Test-Path -LiteralPath $codexExe -PathType Leaf)) { throw "Packaged codex.exe not found: $codexExe" }

    try {
        $manifest = Get-AppxPackageManifest -Package $pkg -ErrorAction Stop
        $applications = @($manifest.Package.Applications.Application | Where-Object {
            "$($_.Executable)".Replace('/', '\') -ieq 'app\ChatGPT.exe'
        })
        if ($applications.Count -ne 1) { throw 'Could not resolve the Codex application ID from the Store manifest.' }
        $applicationId = [string]$applications[0].Id
    } catch {
        throw "Could not validate the OpenAI.Codex Store application manifest: $($_.Exception.Message)"
    }

    $family = [string]$pkg.PackageFamilyName
    if (-not $family -or -not $applicationId) { throw 'Codex Store package identity is incomplete.' }

    return [pscustomobject]@{
        Version = [string]$pkg.Version
        PackageRoot = [string]$pkg.InstallLocation
        PackageFullName = [string]$pkg.PackageFullName
        PackageFamilyName = $family
        ApplicationId = $applicationId
        AppUserModelId = "$family!$applicationId"
        SignatureKind = [string]$pkg.SignatureKind
        AppRoot = $appRoot
        DesktopExe = $desktopExe
        CodexExe = $codexExe
    }
}

function Get-ProcessExecutablePath([int]$ProcessId) {
    try {
        $p = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        return [string]$p.ExecutablePath
    } catch { return $null }
}

function Get-CodexProcesses([object]$Codex) {
    return @(Get-CimInstance Win32_Process -Filter "Name = 'ChatGPT.exe'" -ErrorAction SilentlyContinue |
        Where-Object { Test-SamePath ([string]$_.ExecutablePath) $Codex.DesktopExe })
}

function Get-NodeVersion([string]$Path) {
    try {
        $psi = [Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $Path
        $psi.Arguments = '-p "process.versions.node"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $proc = [Diagnostics.Process]::new()
        $proc.StartInfo = $psi
        if (-not $proc.Start()) { return $null }
        $stdout = $proc.StandardOutput.ReadToEnd().Trim()
        $null = $proc.StandardError.ReadToEnd()
        if (-not $proc.WaitForExit(5000) -or $proc.ExitCode -ne 0) {
            try { $proc.Kill() } catch { }
            return $null
        }
        $v = [Version]$stdout
        return $v
    } catch { return $null }
}

function Get-NodeRuntime([object]$Codex) {
    $candidates = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    if ($env:CODEX_USAGE_NODE) { $candidates.Add($env:CODEX_USAGE_NODE) }
    foreach ($relative in @(
        'app\resources\cua_node\bin\node.exe',
        'app\resources\cua_node\node.exe',
        'app\resources\node.exe'
    )) {
        $candidate = Join-Path $Codex.PackageRoot $relative
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidates.Add($candidate) }
    }
    foreach ($name in @('node.exe', 'node')) {
        try {
            foreach ($command in @(Get-Command $name -All -ErrorAction Stop)) {
                $candidate = if ($command.Source) { [string]$command.Source } else { [string]$command.Path }
                if ($candidate) { $candidates.Add($candidate) }
            }
        } catch { }
    }

    foreach ($raw in $candidates) {
        try { $candidate = [IO.Path]::GetFullPath($raw) } catch { continue }
        if (-not $seen.Add($candidate) -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $version = Get-NodeVersion $candidate
        if ($null -ne $version -and $version.Major -ge 22) {
            return [pscustomobject]@{ Path = $candidate; Version = $version.ToString() }
        }
    }
    throw 'Node.js 22 or newer is required. Codex Usage Bar checks the Codex bundled Node runtime first, then PATH.'
}

function Test-CdpWebSocketUrl([string]$Value, [int]$Port, [string]$Kind) {
    try {
        $uri = [Uri]$Value
        if ($uri.Scheme -ne 'ws' -or $uri.Host -notin @('127.0.0.1', 'localhost', '::1', '[::1]') -or $uri.Port -ne $Port) { return $false }
        if ($uri.UserInfo -or $uri.Query -or $uri.Fragment) { return $false }
        $pattern = if ($Kind -eq 'browser') { '^/devtools/browser/[A-Za-z0-9._-]{1,200}$' } else { '^/devtools/page/[A-Za-z0-9._-]{1,200}$' }
        return $uri.AbsolutePath -cmatch $pattern
    } catch { return $false }
}

function Get-CdpHttpBase([string]$HostAddress, [int]$Port) {
    if ($HostAddress -eq '::1') { return "http://[::1]:$Port" }
    if ($HostAddress -eq '127.0.0.1') { return "http://127.0.0.1:$Port" }
    throw "Rejected non-loopback CDP HTTP host: $HostAddress"
}

function Invoke-NodeCdpProbe([object]$Node, [string]$HostAddress, [int]$Port, [int]$TimeoutMs = 6000) {
    if ($HostAddress -notin @('127.0.0.1', '::1')) { throw "Rejected non-loopback CDP probe host: $HostAddress" }

    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = [string]$Node.Path
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $probeArgs = @(
        $script:Injector,
        '--probe',
        '--cdp-host', $HostAddress,
        '--port', [string]$Port
    )
    $psi.Arguments = ($probeArgs | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' '

    $proc = [Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    if (-not $proc.Start()) { throw 'Could not start the Node CDP probe.' }
    try {
        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit($TimeoutMs)) {
            try { $proc.Kill($true) } catch { try { $proc.Kill() } catch { } }
            throw "Node CDP probe timed out after $TimeoutMs ms."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult().Trim()
        $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
        if ($proc.ExitCode -ne 0) {
            $detail = if ($stderr) { $stderr } elseif ($stdout) { $stdout } else { "exit code $($proc.ExitCode)" }
            throw "Node CDP probe failed: $detail"
        }
        if (-not $stdout) { throw 'Node CDP probe returned no JSON.' }
        try { $payload = $stdout | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Node CDP probe returned invalid JSON: $stdout" }
        if (-not [bool]$payload.ok) { throw 'Node CDP probe did not report success.' }
        return $payload
    } finally {
        $proc.Dispose()
    }
}

function Get-CdpIdentity([int]$Port, [object]$Codex, [object]$Node) {
    try {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop)
        if ($listeners.Count -eq 0) { return $null }
        foreach ($listener in $listeners) {
            if ($listener.LocalAddress -notin @('127.0.0.1', '::1')) { return $null }
            $ownerPath = Get-ProcessExecutablePath ([int]$listener.OwningProcess)
            if (-not (Test-SamePath $ownerPath $Codex.DesktopExe)) { return $null }
        }

        # Delegate DevTools HTTP probing to the same Node runtime that owns the
        # long-lived CDP WebSocket. This avoids PowerShell HttpClient differences
        # around IPv6 loopback while preserving the launcher-side port-owner check.
        $addresses = @($listeners.LocalAddress | Select-Object -Unique | Sort-Object {
            if ($_ -eq '127.0.0.1') { 0 } else { 1 }
        })
        foreach ($address in $addresses) {
            try {
                $probe = Invoke-NodeCdpProbe $Node $address $Port
                if ([string]$probe.cdpHost -ne [string]$address) { continue }
                if ([string]$probe.browserId -notmatch '^[A-Za-z0-9._-]{1,200}$') { continue }
                if ([int]$probe.targetCount -le 0) { continue }
                return [pscustomobject]@{
                    CdpHost = [string]$address
                    BrowserId = [string]$probe.browserId
                    Browser = [string]$probe.browser
                    TargetCount = [int]$probe.targetCount
                }
            } catch {
                if ($env:CODEX_USAGE_BAR_TRACE -eq '1') {
                    Write-UsageLog "CDP probe host=$address failed: $($_.Exception.Message)"
                }
            }
        }
        return $null
    } catch { return $null }
}

function Test-PortAvailable([int]$Port) {
    try { return @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue).Count -eq 0 }
    catch { return $false }
}

function Stop-Codex([object]$Codex) {
    foreach ($process in @(Get-CodexProcesses $Codex)) {
        try { Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop } catch { }
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-CodexProcesses $Codex).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if ((Get-CodexProcesses $Codex).Count -gt 0) { throw 'Codex did not close within 10 seconds.' }
}

function ConvertTo-CodexProcessArgument([string]$Value) {
    if ($Value.Contains('"')) { throw 'Process arguments containing a double quote are not supported.' }
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '\s') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function ConvertTo-CodexArgumentLine([string[]]$Arguments) {
    return (($Arguments | ForEach-Object { ConvertTo-CodexProcessArgument ([string]$_) }) -join ' ')
}

function Test-CodexCommandLineToken([string]$CommandLine, [string]$Token) {
    if (-not $CommandLine -or -not $Token) { return $false }
    $pattern = '(?i)(?:^|[\s"])' + [regex]::Escape($Token) + '(?=$|[\s"])'
    return [regex]::IsMatch($CommandLine, $pattern)
}

function Get-CodexDebugArgumentStatus([object[]]$Processes, [int]$Port) {
    $flag = "--remote-debugging-port=$Port"
    $encodedFlag = [Uri]::EscapeDataString($flag)
    $sawReadable = $false
    $sawProtocolRedirect = $false

    foreach ($process in @($Processes)) {
        $commandLine = [string]$process.CommandLine
        if (-not $commandLine) { continue }
        $sawReadable = $true

        $protocolPattern = '(?i)(?<!\S)"?(?<url>codex://[^\s"]*)"?'
        foreach ($match in [regex]::Matches($commandLine, $protocolPattern)) {
            $protocolArgument = $match.Groups['url'].Value
            if ($protocolArgument.IndexOf($encodedFlag, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $protocolArgument.IndexOf($flag, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $sawProtocolRedirect = $true
            }
        }

        $rawArguments = [regex]::Replace($commandLine, $protocolPattern, ' ')
        if (Test-CodexCommandLineToken $rawArguments $flag) { return 'forwarded' }
    }

    if ($sawProtocolRedirect) { return 'protocol-redirected' }
    if ($sawReadable) { return 'not-forwarded' }
    return 'uninspectable'
}

function Wait-CodexDebugArgumentStatus([object]$Codex, [int]$Port, [int]$TimeoutSeconds = 5) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = 'uninspectable'
    do {
        $last = Get-CodexDebugArgumentStatus @(Get-CodexProcesses $Codex) $Port
        if ($last -in @('forwarded', 'protocol-redirected')) { return $last }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    return $last
}

function Initialize-CodexPackageLauncher {
    if ('CodexUsageBar.PackageLauncher' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace CodexUsageBar {
  [Flags]
  internal enum ActivateOptions : uint { None = 0 }

  [ComImport]
  [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
  [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IApplicationActivationManager {
    [PreserveSig]
    int ActivateApplication(
      [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
      [MarshalAs(UnmanagedType.LPWStr)] string arguments,
      ActivateOptions options,
      out uint processId);
  }

  [ComImport]
  [Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
  internal class ApplicationActivationManager {}

  public static class PackageLauncher {
    public static uint Launch(string appUserModelId, string arguments) {
      var manager = (IApplicationActivationManager)new ApplicationActivationManager();
      try {
        uint processId;
        int result = manager.ActivateApplication(
          appUserModelId,
          arguments ?? string.Empty,
          ActivateOptions.None,
          out processId);
        Marshal.ThrowExceptionForHR(result);
        return processId;
      } finally {
        if (Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
      }
    }
  }
}
'@
}

function Start-CodexPackageActivation([object]$Codex, [string[]]$Arguments) {
    if ($Codex.AppUserModelId -notmatch '^[A-Za-z0-9._-]{1,128}![A-Za-z0-9._-]{1,64}$') {
        throw 'The registered Codex AppUserModelId is unavailable or invalid.'
    }
    Initialize-CodexPackageLauncher
    $line = ConvertTo-CodexArgumentLine $Arguments
    $pidValue = [CodexUsageBar.PackageLauncher]::Launch($Codex.AppUserModelId, $line)
    if ($pidValue -le 0) { throw 'Windows did not return a Codex process ID after package activation.' }
    return [int]$pidValue
}

function Start-CodexDirect([object]$Codex, [string[]]$Arguments) {
    $line = ConvertTo-CodexArgumentLine $Arguments
    $process = Start-Process -FilePath $Codex.DesktopExe -ArgumentList $line -PassThru -ErrorAction Stop
    try {
        if ($process.Id -le 0) { throw 'Windows did not return a Codex process ID after direct launch.' }
        return [int]$process.Id
    } finally {
        $process.Dispose()
    }
}

function Start-CodexForDebugging([object]$Codex, [int]$Port) {
    $arguments = @('--remote-debugging-address=127.0.0.1', "--remote-debugging-port=$Port")

    # Match Codex Dream Skin: launch the registered Store application first.
    $packagePid = Start-CodexPackageActivation $Codex $arguments
    $packageStatus = Wait-CodexDebugArgumentStatus $Codex $Port
    Write-UsageLog "package activation PID=$packagePid debug-arg-status=$packageStatus"

    if ($packageStatus -ne 'protocol-redirected') {
        return [pscustomobject]@{
            ProcessId = $packagePid
            Strategy = 'package-activation'
            ArgumentStatus = $packageStatus
            PackageArgumentStatus = $packageStatus
        }
    }

    # Some Codex builds convert package-activation arguments into a codex://
    # navigation URL. Dream Skin closes that session and retries the validated
    # Store executable directly.
    Write-UsageLog 'package activation redirected the CDP flag into codex://; retrying the validated Store executable directly'
    Stop-Codex $Codex
    $deadline = (Get-Date).AddSeconds(5)
    while (-not (Test-PortAvailable $Port) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-PortAvailable $Port)) { throw "Port $Port did not become available after closing the redirected Codex session." }

    $directPid = Start-CodexDirect $Codex $arguments
    $directStatus = Wait-CodexDebugArgumentStatus $Codex $Port
    Write-UsageLog "direct Store executable PID=$directPid debug-arg-status=$directStatus"
    if ($directStatus -in @('protocol-redirected', 'not-forwarded')) {
        throw "Codex $($Codex.Version) did not retain --remote-debugging-port=$Port during package activation or validated direct launch."
    }
    return [pscustomobject]@{
        ProcessId = $directPid
        Strategy = 'direct-store-executable'
        ArgumentStatus = $directStatus
        PackageArgumentStatus = $packageStatus
    }
}

function Write-CdpDiagnostics([object]$Codex, [object]$Node, [int]$Port) {
    try {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
        if ($listeners.Count -eq 0) {
            Write-UsageLog "diagnostic: no listener on port $Port"
        } else {
            foreach ($listener in $listeners) {
                $ownerPath = Get-ProcessExecutablePath ([int]$listener.OwningProcess)
                Write-UsageLog "diagnostic: listener address=$($listener.LocalAddress) pid=$($listener.OwningProcess) path=$ownerPath"
            }
        }
    } catch {
        Write-UsageLog "diagnostic: listener query failed: $($_.Exception.Message)"
    }

    try {
        $status = Get-CodexDebugArgumentStatus @(Get-CodexProcesses $Codex) $Port
        Write-UsageLog "diagnostic: debug-arg-status=$status"
    } catch { }

    $diagnosticAddresses = @(
        @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue).LocalAddress |
            Where-Object { $_ -in @('127.0.0.1', '::1') } | Select-Object -Unique
    )
    foreach ($address in $diagnosticAddresses) {
        try {
            $probe = Invoke-NodeCdpProbe $Node $address $Port
            Write-UsageLog "diagnostic: host=$address Browser=$($probe.browser) browserId=$($probe.browserId) page-count=$($probe.targetCount) urls=$((@($probe.targets.url) -join ', '))"
        } catch {
            Write-UsageLog "diagnostic: host=$address Node CDP probe unavailable: $($_.Exception.Message)"
        }
    }

}

function Ensure-CdpIdentity([object]$Codex, [object]$Node, [int]$Port, [switch]$AllowRestart, [switch]$RequireExistingAtStart) {
    $identity = Get-CdpIdentity $Port $Codex $Node
    if ($null -ne $identity) { return $identity }

    $running = @(Get-CodexProcesses $Codex)
    if ($RequireExistingAtStart -and $running.Count -eq 0) {
        throw 'Managed attach was cancelled because the Codex session already exited.'
    }
    if ($running.Count -gt 0) {
        if (-not $AllowRestart) {
            throw "Codex is already running without a verified CDP endpoint on port $Port. Re-run with -RestartExisting once."
        }
        Write-UsageLog 'closing existing Codex so it can be restarted with CDP enabled'
        Stop-Codex $Codex
        $freeDeadline = (Get-Date).AddSeconds(5)
        while (-not (Test-PortAvailable $Port) -and (Get-Date) -lt $freeDeadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-PortAvailable $Port)) { throw "Port $Port remained occupied after Codex was closed." }
    }

    $launch = Start-CodexForDebugging $Codex $Port
    Write-UsageLog "started Codex $($Codex.Version) strategy=$($launch.Strategy) argument-status=$($launch.ArgumentStatus) port=$Port"

    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 400
        $identity = Get-CdpIdentity $Port $Codex $Node
        if ($null -ne $identity) { return $identity }

        $status = Get-CodexDebugArgumentStatus @(Get-CodexProcesses $Codex) $Port
        if ($status -eq 'protocol-redirected') {
            Write-CdpDiagnostics $Codex $Node $Port
            throw "Codex converted --remote-debugging-port=$Port into a codex:// navigation argument instead of exposing CDP."
        }
    } while ((Get-Date) -lt $deadline)

    Write-CdpDiagnostics $Codex $Node $Port
    throw "Codex did not expose a verified loopback CDP endpoint on port $Port within 45 seconds."
}

function Read-State {
    if (-not (Test-Path -LiteralPath $script:StatePath -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { return $null }
}

function Stop-RecordedInjector {
    $state = Read-State
    if ($null -eq $state -or -not $state.injectorPid) { Remove-Item $script:StatePath -Force -ErrorAction SilentlyContinue; return }
    try {
        $pidValue = [int]$state.injectorPid
        $actualPath = Get-ProcessExecutablePath $pidValue
        if ($actualPath -and (Test-SamePath $actualPath ([string]$state.nodePath))) {
            Stop-Process -Id $pidValue -Force -ErrorAction Stop
            Write-UsageLog "stopped recorded injector PID $pidValue"
        }
    } catch { }
    Remove-Item $script:StatePath -Force -ErrorAction SilentlyContinue
}

function Test-ProcessIdentity([int]$ProcessId, [string]$ExpectedPath) {
    if ($ProcessId -le 0 -or -not $ExpectedPath) { return $false }
    try {
        $actual = Get-ProcessExecutablePath $ProcessId
        return $actual -and (Test-SamePath $actual $ExpectedPath)
    } catch { return $false }
}

function Test-RecordedInjectorAlive {
    $state = Read-State
    if ($null -eq $state -or -not $state.injectorPid -or -not $state.nodePath) { return $false }
    return Test-ProcessIdentity ([int]$state.injectorPid) ([string]$state.nodePath)
}

function Stop-RecordedWatcher {
    if (-not (Test-Path -LiteralPath $script:WatcherStatePath -PathType Leaf)) { return }
    try {
        $state = Get-Content -LiteralPath $script:WatcherStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $pidValue = [int]$state.watcherPid
        $expected = [string]$state.powershellPath
        if ($pidValue -gt 0 -and $pidValue -ne $PID -and (Test-ProcessIdentity $pidValue $expected)) {
            Stop-Process -Id $pidValue -Force -ErrorAction Stop
            Write-UsageLog "stopped recorded watcher PID $pidValue"
        }
    } catch { }
    Remove-Item -LiteralPath $script:WatcherStatePath -Force -ErrorAction SilentlyContinue
}

function Start-UsageBarChild([int]$Port, [switch]$AllowRestart) {
    $powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
    $args = @(
        '-NoProfile',
        '-WindowStyle', 'Hidden',
        '-ExecutionPolicy', 'RemoteSigned',
        '-File', $PSCommandPath,
        '-Launch',
        '-ManagedAttach',
        '-CdpPort', "$Port"
    )
    if ($AllowRestart) { $args += '-RestartExisting' }
    $line = ($args | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' '
    $proc = Start-Process -FilePath $powershell -ArgumentList $line -WindowStyle Hidden -PassThru -Wait
    return [int]$proc.ExitCode
}

function Set-WatcherFuse([string]$Reason) {
    $state = [ordered]@{
        schemaVersion = 1
        version = $script:Version
        reason = $Reason
        createdAt = [DateTimeOffset]::Now.ToString('o')
    }
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $script:WatcherFusePath -Encoding UTF8
}

function Clear-WatcherFuse {
    Remove-Item -LiteralPath $script:WatcherFusePath -Force -ErrorAction SilentlyContinue
}

function Watch-UsageBar([int]$Port) {
    $createdNew = $false
    $mutex = New-Object Threading.Mutex($true, 'Local\CodexUsageBarWatcher', [ref]$createdNew)
    if (-not $createdNew) {
        Write-UsageLog 'legacy watcher already running; exiting duplicate instance'
        $mutex.Dispose()
        return
    }

    $powershellPath = (Get-Command powershell.exe -ErrorAction Stop).Source
    $watcherState = [ordered]@{
        schemaVersion = 2
        version = $script:Version
        watcherPid = $PID
        powershellPath = $powershellPath
        port = $Port
        startedAt = [DateTimeOffset]::Now.ToString('o')
        mode = 'legacy-safe'
    }
    $watcherState | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $script:WatcherStatePath -Encoding UTF8
    Write-UsageLog "legacy watcher active PID=$PID port=$Port; installer v0.4.16 uses WatcherHost.exe instead"

    $handledPids = @()
    $fused = Test-Path -LiteralPath $script:WatcherFusePath -PathType Leaf
    try {
        while ($true) {
            $codex = $null
            try { $codex = Get-CodexInstall } catch { Start-Sleep -Seconds 5; continue }
            $running = @(Get-CodexProcesses $codex)
            $currentPids = @($running | ForEach-Object { [int]$_.ProcessId })
            if ($currentPids.Count -eq 0) {
                $handledPids = @()
                if ($fused) {
                    Clear-WatcherFuse
                    $fused = $false
                    Write-UsageLog 'legacy watcher fuse cleared after Codex fully exited'
                }
                Start-Sleep -Seconds 2
                continue
            }

            if ($fused) { Start-Sleep -Seconds 3; continue }
            $sameSession = @($currentPids | Where-Object { $_ -in $handledPids }).Count -gt 0
            $injectorAlive = Test-RecordedInjectorAlive
            if (-not $sameSession) {
                Write-UsageLog "legacy watcher attempting one managed restart for Codex PIDs=$($currentPids -join ',')"
                $exitCode = Start-UsageBarChild $Port -AllowRestart
                if ($exitCode -ne 0) {
                    Set-WatcherFuse "legacy-managed-attach-exit-$exitCode"
                    $fused = $true
                    Write-UsageLog "legacy watcher attach failed with code $exitCode; blocked until Codex fully exits"
                } else {
                    $running = @(Get-CodexProcesses (Get-CodexInstall))
                    $handledPids = @($running | ForEach-Object { [int]$_.ProcessId })
                }
            } elseif (-not $injectorAlive) {
                # Injector recovery for an already-handled session never restarts Codex.
                $exitCode = Start-UsageBarChild $Port
                if ($exitCode -ne 0) {
                    Write-UsageLog "legacy watcher attach-only recovery failed with code $exitCode; no restart attempted"
                }
            }
            Start-Sleep -Seconds 3
        }
    } finally {
        Remove-Item -LiteralPath $script:WatcherStatePath -Force -ErrorAction SilentlyContinue
        try { $mutex.ReleaseMutex() } catch { }
        $mutex.Dispose()
        Write-UsageLog 'legacy watcher stopped'
    }
}

function Quote-ProcessArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-NodeSelfTest([object]$Node) {
    & $Node.Path --check $script:Renderer
    if ($LASTEXITCODE -ne 0) { throw 'renderer-inject.js syntax check failed.' }
    & $Node.Path --check $script:Injector
    if ($LASTEXITCODE -ne 0) { throw 'injector.mjs syntax check failed.' }
    & $Node.Path $script:Injector --self-test --port $CdpPort
    if ($LASTEXITCODE -ne 0) { throw 'injector self-test failed.' }
}

function Remove-LiveInjection([object]$Node, [object]$Codex, [int]$Port) {
    $identity = Get-CdpIdentity $Port $Codex $Node
    if ($null -eq $identity) { return }
    try {
        & $Node.Path $script:Injector --remove --cdp-host $identity.CdpHost --port $Port --browser-id $identity.BrowserId --renderer $script:Renderer --timeout-ms 5000 | Out-Null
    } catch { }
}

if (-not $Run -and -not $Launch -and -not $Stop -and -not $SelfTest -and -not $Watch -and -not $StopWatcher) { $Launch = $true }

if ($StopWatcher) {
    Stop-RecordedWatcher
    Write-UsageLog 'Codex Usage Bar watcher stopped.'
    exit 0
}

if ($Watch) {
    Watch-UsageBar $CdpPort
    exit 0
}

$codex = Get-CodexInstall
$node = Get-NodeRuntime $codex
$codexCommand = Resolve-CodexCommand

if ($SelfTest) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($PSCommandPath, [ref]$tokens, [ref]$errors)
    if (@($errors).Count -gt 0) { throw ($errors | ForEach-Object Message | Out-String) }
    Invoke-NodeSelfTest $node
    if (-not $codexCommand) { throw 'No launchable Codex CLI command was found. Install the Codex CLI or set CODEX_EXECUTABLE/config.json codexPath.' }
    Write-Host "Codex app-server command: $codexCommand"
    Write-Host "Codex Usage Bar v$($script:Version): all self-tests passed. Node $($node.Version)."
    exit 0
}

if ($Stop) {
    Remove-LiveInjection $node $codex $CdpPort
    Stop-RecordedInjector
    Write-UsageLog 'Codex Usage Bar stopped.'
    exit 0
}

if (-not $codexCommand) { throw 'No launchable Codex CLI command was found for app-server.' }
$identity = Ensure-CdpIdentity $codex $node $CdpPort -AllowRestart:$RestartExisting -RequireExistingAtStart:$ManagedAttach
Stop-RecordedInjector

$args = @(
    $script:Injector,
    '--watch',
    '--cdp-host', $identity.CdpHost,
    '--port', "$CdpPort",
    '--browser-id', $identity.BrowserId,
    '--renderer', $script:Renderer,
    '--codex-command', $codexCommand
)
if ($env:CODEX_USAGE_BAR_TRACE -eq '1') { $args += '--trace' }

Write-UsageLog "using Node $($node.Version): $($node.Path)"
Write-UsageLog "verified Codex CDP host=$($identity.CdpHost) browser=$($identity.Browser) browserId=$($identity.BrowserId) targets=$($identity.TargetCount)"
Write-UsageLog "using launchable app-server command: $codexCommand"

if ($Run) {
    & $node.Path @args
    exit $LASTEXITCODE
}

$argumentLine = ($args | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' '
$proc = Start-Process -FilePath $node.Path -ArgumentList $argumentLine -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $script:StdoutPath -RedirectStandardError $script:StderrPath
Start-Sleep -Milliseconds 700
if ($proc.HasExited) { throw "Injector exited during startup. See $script:StderrPath" }

$state = [ordered]@{
    schemaVersion = 1
    version = $script:Version
    port = $CdpPort
    cdpHost = $identity.CdpHost
    injectorPid = $proc.Id
    nodePath = $node.Path
    nodeVersion = $node.Version
    browserId = $identity.BrowserId
    codexDesktopExe = $codex.DesktopExe
    codexCommand = $codexCommand
    codexVersion = $codex.Version
    createdAt = [DateTimeOffset]::Now.ToString('o')
}
$state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $script:StatePath -Encoding UTF8
Write-UsageLog "Codex Usage Bar v$($script:Version) launched in background; PID $($proc.Id)"
Write-Host "Codex Usage Bar is active. Log: $script:StdoutPath"
