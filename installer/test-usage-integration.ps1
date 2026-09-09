[CmdletBinding()]
param([switch]$Live, [string]$CcSwitchDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project 'dist\usage-verification' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::SetUnhandledExceptionMode([Windows.Forms.UnhandledExceptionMode]::ThrowException)
[Windows.Forms.Application]::EnableVisualStyles()
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $project 'dist\CodexUsageBar.exe'))
$flags = [Reflection.BindingFlags]'NonPublic,Public,Static,Instance'
function Invoke-Internal($object, [string]$typeName, [string]$method, [object[]]$parameters) {
    $type = $assembly.GetType('CodexUsageBar.' + $typeName)
    $candidate = @($type.GetMethods($flags) | Where-Object { $_.Name -eq $method -and $_.GetParameters().Count -eq $parameters.Count })[0]
    $spec = $candidate.GetParameters()
    for ($i = 0; $i -lt $parameters.Count; $i++) { $parameters[$i] = [Management.Automation.LanguagePrimitives]::ConvertTo($parameters[$i], $spec[$i].ParameterType) }
    return $candidate.Invoke($object, $parameters)
}
function New-Internal([string]$name, [object[]]$parameters) {
    $ctor = @($assembly.GetType('CodexUsageBar.' + $name).GetConstructors([Reflection.BindingFlags]'NonPublic,Public,Instance') | Where-Object { $_.GetParameters().Count -eq $parameters.Count })[0]
    $spec = $ctor.GetParameters()
    for ($i = 0; $i -lt $parameters.Count; $i++) { $parameters[$i] = [Management.Automation.LanguagePrimitives]::ConvertTo($parameters[$i], $spec[$i].ParameterType) }
    return $ctor.Invoke($parameters)
}
function Set-Internal($object, [string]$name, $value) { $object.GetType().GetField($name, $flags).SetValue($object, $value) }
function Get-Internal($object, [string]$name) { return $object.GetType().GetField($name, $flags).GetValue($object) }
function Save-Form($form, [string]$name) {
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-30000, -30000)
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $form.CreateControl()
    $form.PerformLayout()
    foreach ($child in $form.Controls) { $child.CreateControl(); $child.PerformLayout(); foreach ($item in $child.Controls) { $item.CreateControl() } }
    $bitmap = New-Object Drawing.Bitmap($form.Width, $form.Height)
    try { $form.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $bitmap.Width, $bitmap.Height))); $bitmap.Save((Join-Path $OutputDirectory $name)) }
    finally { $bitmap.Dispose(); $form.Hide() }
}
try {
    Invoke-Internal $null 'CcSwitchTests' 'Run' @()
    $provider = New-Internal 'CcProvider' @()
    Set-Internal $provider 'Name' 'DespAI (fixture)'
    $stream = $assembly.GetManifestResourceStream('package-quota.js')
    $reader = New-Object IO.StreamReader($stream)
    try { $script = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $now = [DateTime]::UtcNow
    $fixture = Invoke-Internal $null 'CcSwitchTests' 'Fixture' @($now, $now.AddDays(7), 'pro', [double]80, $true)
    $snapshot = Invoke-Internal $null 'CcSwitchClient' 'Extract' @($provider, $script, $fixture, $now)
    if ($Live) {
        if (-not $CcSwitchDirectory) { throw 'Explicit CC Switch directory required for live verification' }
        $provider = Invoke-Internal $null 'CcSwitchReader' 'ReadCurrent' @($CcSwitchDirectory)
        $client = New-Internal 'CcSwitchClient' @()
        $snapshot = Invoke-Internal $client 'CcSwitchClient' 'Query' @($provider)
        $rows = Get-Internal $snapshot 'UsageRows'
        Write-Output ('Live query: provider={0}; rows={1}; valid={2}' -f (Get-Internal $provider 'Name'), $rows.Count, @($rows | Where-Object { Get-Internal $_ 'Valid' }).Count)
    }
    $form = New-Internal 'OverlayForm' @()
    try {
        Invoke-Internal $form 'OverlayForm' 'ApplyTexts' @((New-Internal 'Texts' @($true)))
        Invoke-Internal $form 'OverlayForm' 'ApplySnapshot' @($snapshot)
        Invoke-Internal $form 'OverlayForm' 'SetExpanded' @($true)
        Save-Form $form 'usage-expanded.png'
        Invoke-Internal $form 'OverlayForm' 'SetExpanded' @($false)
        Save-Form $form 'usage-collapsed.png'
        if (-not $Live) {
            foreach ($variant in @('plus', 'pro-no-estimate', 'pro-zero-estimate')) {
                $plan = if ($variant -eq 'plus') { 'plus' } else { 'pro' }
                $hasEstimate = $variant -ne 'pro-no-estimate'
                $sample = Invoke-Internal $null 'CcSwitchTests' 'Fixture' @($now, $now.AddDays(7), $plan, [double]80, $hasEstimate)
                $variantSnapshot = Invoke-Internal $null 'CcSwitchClient' 'Extract' @($provider, $script, $sample, $now)
                if ($variant -eq 'pro-zero-estimate') {
                    $estimate = $variantSnapshot.GetType().GetProperty('ThirdPartyEstimate', $flags).GetValue($variantSnapshot, $null)
                    Set-Internal $estimate 'Remaining' ([Nullable[double]]0)
                }
                Invoke-Internal $form 'OverlayForm' 'ApplySnapshot' @($variantSnapshot)
                Invoke-Internal $form 'OverlayForm' 'SetExpanded' @($true)
                Save-Form $form ('usage-' + $variant + '-expanded.png')
                Invoke-Internal $form 'OverlayForm' 'SetExpanded' @($false)
                Save-Form $form ('usage-' + $variant + '-collapsed.png')
            }
        }
        foreach ($dpi in @(96, 120, 144, 192)) {
            Invoke-Internal $form 'OverlayForm' 'ApplyDpi' @($dpi)
            foreach ($plan in @('pro', 'plus')) {
                $sample = Invoke-Internal $null 'CcSwitchTests' 'Fixture' @($now, $now.AddDays(7), $plan, [double]96, $true)
                $scaledSnapshot = Invoke-Internal $null 'CcSwitchClient' 'Extract' @($provider, $script, $sample, $now)
                Invoke-Internal $form 'OverlayForm' 'ApplySnapshot' @($scaledSnapshot)
                foreach ($expanded in @($false, $true)) {
                    Invoke-Internal $form 'OverlayForm' 'SetExpanded' @($expanded)
                    $state = if ($expanded) { 'expanded' } else { 'collapsed' }
                    Save-Form $form ("dpi-$dpi-$plan-$state.png")
                    if (-not $expanded -and $form.Height -ne [Math]::Round(33 * $dpi / 96)) { throw 'DPI collapsed height mismatch' }
                }
            }
        }
        Invoke-Internal $form 'OverlayForm' 'ApplyDpi' @(96)
        Invoke-Internal $form 'OverlayForm' 'SetExpanded' @($false)
        $failure = Invoke-Internal $null 'CcSwitchClient' 'Failure' @($provider, 'Querying...')
        Invoke-Internal $form 'OverlayForm' 'ApplySnapshot' @($failure)
        Save-Form $form 'usage-status-centered.png'
    } finally { $form.Dispose() }
    $settingsForm = New-Internal 'UsageSettingsForm' @((Join-Path $OutputDirectory 'nonexistent-settings.json'), $true)
    (Get-Internal $settingsForm '_mode').SelectedIndex = 3
    (Get-Internal $settingsForm '_name').Text = 'My quota'
    (Get-Internal $settingsForm '_url').Text = 'https://your-site.example'
    (Get-Internal $settingsForm '_code').Text = $script.Replace("`r`n", "`n").Replace("`n", "`r`n")
    try {
        Save-Form $settingsForm 'usage-settings.png'
        foreach ($dark in @($true, $false)) {
            $theme = Invoke-Internal $null 'ThemePalette' 'CreateDefault' @($dark)
            $suffix = if ($dark) { 'dark' } else { 'light' }
            Write-Output ('Rendering ' + $suffix)
            Invoke-Internal $settingsForm 'UsageSettingsForm' 'ApplyTheme' @($theme)
            (Get-Internal $settingsForm '_mode').SelectedIndex = 3
            Save-Form $settingsForm ('settings-independent-' + $suffix + '.png')
            (Get-Internal $settingsForm '_mode').SelectedIndex = 2
            (Get-Internal $settingsForm '_directory').Text = 'C:\Users\91266\.cc-switch'
            Save-Form $settingsForm ('settings-cc-switch-' + $suffix + '.png')
            $menu = New-Object Windows.Forms.ContextMenuStrip
            $font = Invoke-Internal $null 'NativeTheme' 'UiFont' @($theme, [single]0, [Drawing.FontStyle]::Regular)
            try {
                $status = $menu.Items.Add('Codex 连接：成功'); $status.Enabled = $false
                $detail = $menu.Items.Add('DespAI'); $detail.Enabled = $false
                [void]$menu.Items.Add((New-Object Windows.Forms.ToolStripSeparator))
                [void]$menu.Items.Add('立即刷新')
                [void]$menu.Items.Add('数据源')
                $visible = $menu.Items.Add('显示悬浮窗'); $visible.Checked = $true
                $modes = $menu.Items.Add('展示方式'); [void]$modes.DropDownItems.Add('独立展示'); [void]$modes.DropDownItems.Add('跟随 Codex')
                $language = $menu.Items.Add('语言'); [void]$language.DropDownItems.Add('跟随系统'); [void]$language.DropDownItems.Add('中文'); [void]$language.DropDownItems.Add('English')
                [void]$menu.Items.Add('开机启动')
                [void]$menu.Items.Add((New-Object Windows.Forms.ToolStripSeparator))
                [void]$menu.Items.Add('退出')
                Invoke-Internal $null 'TrayThemeRenderer' 'Apply' @($menu, $theme, $font)
                $menu.Show((New-Object Drawing.Point(-30000, -30000)))
                $menu.Location = New-Object Drawing.Point(-30000, -30000)
                $menu.Items[4].Select()
                $bitmap = New-Object Drawing.Bitmap($menu.Width, $menu.Height)
                try { $menu.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $bitmap.Width, $bitmap.Height))); $bitmap.Save((Join-Path $OutputDirectory ('tray-' + $suffix + '.png'))) } finally { $bitmap.Dispose() }
            } finally { $menu.Close(); $menu.Dispose(); $font.Dispose() }
        }
    } finally { $settingsForm.Dispose() }
    Write-Output 'Compatibility tests and preview rendering passed.'
} catch {
    # No raw exception messages: reflection/HTTP exceptions could contain configuration.
    $errorObject = $_.Exception
    while ($errorObject.InnerException) { $errorObject = $errorObject.InnerException }
    Write-Output ('Verification failed: ' + $errorObject.GetType().Name)
    if ($errorObject -is [InvalidOperationException] -and $errorObject.Message.StartsWith('CC Switch')) { Write-Output $errorObject.Message }
    Write-Output $_.ScriptStackTrace
    exit 1
}
