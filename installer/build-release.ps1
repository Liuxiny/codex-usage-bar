[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$InnoCompiler = $env:INNO_SETUP_COMPILER,
    [string]$CSharpCompiler = $env:CSC_COMPILER,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

# Do not use $PSScriptRoot inside a parameter default expression. Windows
# PowerShell 5.1 can bind the parameter before $PSScriptRoot is populated.
$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $scriptRoot) {
    throw 'Could not resolve the installer script directory.'
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
if (-not $OutputDir) {
    $OutputDir = Join-Path $projectRoot 'dist'
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

$version = ([IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION'))).Trim()
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw "Invalid VERSION: $version" }

function Find-InnoSetupCompiler {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        $expanded = [Environment]::ExpandEnvironmentVariables($ExplicitPath)
        if (Test-Path -LiteralPath $expanded -PathType Leaf) {
            return [IO.Path]::GetFullPath($expanded)
        }
        throw "INNO_SETUP_COMPILER/ -InnoCompiler points to a missing file: $expanded"
    }

    # First honor a normal PATH installation.
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    # Inno Setup may be installed per-user. Ask the uninstall registry first so
    # localized/custom install directories are also supported.
    $registryRoots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($root in $registryRoots) {
        try {
            Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like 'Inno Setup 6*' } |
                ForEach-Object {
                    if ($_.InstallLocation) {
                        $candidates.Add((Join-Path $_.InstallLocation 'ISCC.exe'))
                    }
                    if ($_.UninstallString) {
                        $uninstallExe = $_.UninstallString.Trim('"')
                        if ($uninstallExe -match '^(.*?\\)unins\d*\.exe(?:"|\s|$)') {
                            $candidates.Add((Join-Path $Matches[1] 'ISCC.exe'))
                        }
                    }
                }
        } catch {
            # Registry probing is best-effort; fixed paths below remain valid.
        }
    }

    # Common machine-wide and per-user locations.
    if ($env:LOCALAPPDATA) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Inno Setup 6\ISCC.exe'))
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Find-CSharpCompiler {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        $expanded = [Environment]::ExpandEnvironmentVariables($ExplicitPath)
        if (Test-Path -LiteralPath $expanded -PathType Leaf) { return [IO.Path]::GetFullPath($expanded) }
        throw "CSC_COMPILER/-CSharpCompiler points to a missing file: $expanded"
    }

    $command = Get-Command csc.exe -ErrorAction SilentlyContinue
    if ($command -and $command.Source -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) {
        return $command.Source
    }

    $candidates = @()
    if ($env:WINDIR) {
        $candidates += (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
        $candidates += (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
    }
    return $null
}

function Find-FrameworkReference {
    param(
        [string]$CompilerPath,
        [string]$AssemblyName
    )

    $compilerDir = Split-Path -Parent $CompilerPath
    $nearCompiler = Join-Path $compilerDir $AssemblyName
    if (Test-Path -LiteralPath $nearCompiler -PathType Leaf) { return $nearCompiler }

    $roots = @()
    if (${env:ProgramFiles(x86)}) {
        $roots += (Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework')
    }
    if ($env:ProgramFiles) {
        $roots += (Join-Path $env:ProgramFiles 'Reference Assemblies\Microsoft\Framework\.NETFramework')
    }
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $found = Get-ChildItem -LiteralPath $root -Filter $AssemblyName -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    if ($env:WINDIR) {
        $assemblyBase = [IO.Path]::GetFileNameWithoutExtension($AssemblyName)
        foreach ($gacKind in @('GAC_MSIL', 'GAC_32', 'GAC_64')) {
            $gacRoot = Join-Path $env:WINDIR "Microsoft.NET\assembly\$gacKind\$assemblyBase"
            if (-not (Test-Path -LiteralPath $gacRoot -PathType Container)) { continue }
            $found = Get-ChildItem -LiteralPath $gacRoot -Filter $AssemblyName -File -Recurse -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }
    return $null
}

$CSharpCompiler = Find-CSharpCompiler -ExplicitPath $CSharpCompiler
if (-not $CSharpCompiler) {
    throw 'The .NET Framework C# compiler (csc.exe) was not found. Windows 10/11 normally provides it under %WINDIR%\Microsoft.NET\Framework64\v4.0.30319.'
}
$SystemWindowsFormsReference = Find-FrameworkReference -CompilerPath $CSharpCompiler -AssemblyName 'System.Windows.Forms.dll'
$SystemDrawingReference = Find-FrameworkReference -CompilerPath $CSharpCompiler -AssemblyName 'System.Drawing.dll'
$SystemWebExtensionsReference = Find-FrameworkReference -CompilerPath $CSharpCompiler -AssemblyName 'System.Web.Extensions.dll'
$PresentationCoreReference = Find-FrameworkReference -CompilerPath $CSharpCompiler -AssemblyName 'PresentationCore.dll'
$WindowsBaseReference = Find-FrameworkReference -CompilerPath $CSharpCompiler -AssemblyName 'WindowsBase.dll'
if (-not $SystemWindowsFormsReference) { throw 'System.Windows.Forms.dll was not found for the tray watcher host build.' }
if (-not $SystemDrawingReference) { throw 'System.Drawing.dll was not found for the tray watcher host build.' }
if (-not $SystemWebExtensionsReference) { throw 'System.Web.Extensions.dll was not found for JSON support.' }
if (-not $PresentationCoreReference) { throw 'PresentationCore.dll was not found for Windows font metadata.' }
if (-not $WindowsBaseReference) { throw 'WindowsBase.dll was not found for Windows font metadata.' }
Write-Host "Using C# compiler: $CSharpCompiler"
Write-Host "Using System.Windows.Forms: $SystemWindowsFormsReference"
Write-Host "Using System.Drawing: $SystemDrawingReference"
Write-Host "Using System.Web.Extensions: $SystemWebExtensionsReference"
Write-Host "Using PresentationCore: $PresentationCoreReference"
Write-Host "Using WindowsBase: $WindowsBaseReference"

if (-not $SkipInstaller) {
    $InnoCompiler = Find-InnoSetupCompiler -ExplicitPath $InnoCompiler
}
if (-not $SkipInstaller -and -not $InnoCompiler) {
    throw @'
Inno Setup 6 compiler (ISCC.exe) was not found.
The script checked PATH, the Windows uninstall registry, LocalAppData, and Program Files.
If Inno Setup is installed in a custom directory, run:
  .\installer\build-release.ps1 -InnoCompiler 'C:\path\to\Inno Setup 6\ISCC.exe'
or set INNO_SETUP_COMPILER to that full path.
'@
}

if (-not $SkipInstaller) { Write-Host "Using Inno Setup compiler: $InnoCompiler" }

$stageRoot = Join-Path $env:TEMP ("codex-usage-bar-stage-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
try {
    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
    $companionSources = @(
        (Join-Path $scriptRoot 'watcher-host.cs'),
        (Join-Path $scriptRoot 'companion-model.cs'),
        (Join-Path $scriptRoot 'app-server-client.cs'),
        (Join-Path $scriptRoot 'platform.cs'),
        (Join-Path $scriptRoot 'overlay-form.cs')
    )
    $companionExe = Join-Path $stageRoot 'CodexUsageBar.exe'
    $iconSource = Join-Path $projectRoot 'assets\codex-usage-bar.ico'
    $stageIcon = Join-Path $stageRoot 'codex-usage-bar.ico'
    if (-not (Test-Path -LiteralPath $iconSource -PathType Leaf)) {
        throw "Application icon is missing: $iconSource"
    }
    Copy-Item -LiteralPath $iconSource -Destination $stageIcon -Force
    & $CSharpCompiler /nologo /target:winexe /platform:anycpu /optimize+ `
        "/out:$companionExe" `
        "/win32icon:$stageIcon" `
        "/reference:$SystemWindowsFormsReference" `
        "/reference:$SystemDrawingReference" `
        "/reference:$SystemWebExtensionsReference" `
        "/reference:$PresentationCoreReference" `
        "/reference:$WindowsBaseReference" `
        $companionSources
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $companionExe -PathType Leaf)) {
        throw "Companion C# compilation failed with exit code $LASTEXITCODE"
    }
    $selfTest = Start-Process -FilePath $companionExe -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($selfTest.ExitCode -ne 0) { throw "Companion self-test failed with exit code $($selfTest.ExitCode)" }
    $standaloneExe = Join-Path $OutputDir 'CodexUsageBar.exe'
    Copy-Item -LiteralPath $companionExe -Destination $standaloneExe -Force
    Write-Host "Built and tested native companion: $standaloneExe"

    Copy-Item -LiteralPath (Join-Path $projectRoot 'setup-bootstrap.ps1') -Destination $stageRoot -Force
    foreach ($file in @('VERSION', 'README.md', 'INSTALL-WINDOWS.md', 'CODEX-THEME-SPEC.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $stageRoot -Force
    }
    if ($SkipInstaller) { return }

    $iss = Join-Path $scriptRoot 'codex-usage-bar.iss'
    & $InnoCompiler `
        "/DAppVersion=$version" `
        "/DStageRoot=$stageRoot" `
        "/DOutputDir=$OutputDir" `
        $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

    $setup = Join-Path $OutputDir "CodexUsageBar-Setup-v$version.exe"
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Expected Setup.exe was not created: $setup" }
    $hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
    Write-Host "Built $setup"
    Write-Host "SHA256 $($hash.Hash)"
} finally {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
}
