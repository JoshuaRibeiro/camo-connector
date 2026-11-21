param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, System.Net.Http
[Windows.Forms.Application]::EnableVisualStyles()
. (Join-Path $PSScriptRoot 'capture-frame.ps1')

function Quote-Argument([string]$Value) {
    '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

function Start-LoggedProcess($program, [string[]]$arguments, [string]$name) {
    $stdout = Join-Path $runFolder "$name.log"
    $stderr = Join-Path $runFolder "$name-errors.log"
    $process = Start-Process -FilePath $program -ArgumentList (($arguments | ForEach-Object { Quote-Argument $_ }) -join ' ') `
        -WorkingDirectory $PSScriptRoot -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    if ($name -eq 'update-check') {
        # Keep the Windows process handle open so ExitCode remains readable after exit.
        [void]$process.Handle
    }
    return $process
}

function Stop-Child($child) {
    if ($child -and !$child.HasExited) {
        try { $child.Kill(); [void]$child.WaitForExit(2000) }
        catch [InvalidOperationException] { if (!$child.HasExited) { throw } }
    }
    if ($child) { $child.Dispose() }
}

function Set-Preview($image) {
    $old = $preview.Image
    $preview.Image = $image
    if ($old) { $old.Dispose() }
}

function Get-Preview($source) {
    if ($source.Kind -eq 'device') { return Open-CaptureFrame $script:framePath }
    $bounds = $source.Screen.Bounds
    $frame = [Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [Drawing.Graphics]::FromImage($frame)
    try {
        $graphics.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size)
        $width = [Math]::Min(720, $frame.Width)
        return [Drawing.Bitmap]::new($frame, $width, [int]($width * $frame.Height / $frame.Width))
    } finally { $graphics.Dispose(); $frame.Dispose() }
}

function Add-Source($source) {
    [void]$sources.Items.Add($source)
}

function Read-CaptureDevices([string[]]$lines) {
    $device = $null
    foreach ($line in $lines) {
        if ($line -match '"(.+)"\s+\((?:video|none)\)') {
            if ($device) { $device }
            $device = [pscustomobject]@{ Kind = 'device'; Label = "$($Matches[1]) (capture device)"; Input = $Matches[1] }
        } elseif ($line -match 'Alternative name\s+"(.+)"' -and $device) {
            $device.Input = $Matches[1]
            $device
            $device = $null
        } elseif ($line -match '"(.+)"\s+\(audio\)' -and $device) {
            $device
            $device = $null
        }
    }
    if ($device) { $device }
}

function Find-Sources {
    $preferred = if ($sources.SelectedItem) { $sources.SelectedItem.Label } else { $settings.source }
    if ($script:discovery) { Stop-Child $script:discovery; $script:discovery = $null }
    $sources.Items.Clear()
    $screens = [Windows.Forms.Screen]::AllScreens
    for ($i=0; $i -lt $screens.Count; $i++) {
        $screen = $screens[$i]
        $suffix = if ($screen.Primary) { ' - primary' } else { '' }
        Add-Source ([pscustomobject]@{ Kind='monitor'; Label="$($screen.DeviceName)  $($screen.Bounds.Width)x$($screen.Bounds.Height)$suffix"; Index=$i; Screen=$screen })
    }
    if ($sources.Items.Count) { $sources.SelectedIndex = 0 }
    $script:preferredSource = $preferred
    Restore-Source
    if ($ffmpeg) {
        $script:discovery = Start-LoggedProcess $ffmpeg @('-hide_banner','-list_devices','true','-f','dshow','-i','dummy') 'device-discovery'
        $refresh.Enabled = $false
        $deviceStatus.Text = 'Scanning for capture devices...'
    } else { $deviceStatus.Text = 'Install FFmpeg on PATH to add capture devices.' }
}

function Restore-Source {
    if (!$script:preferredSource) { return }
    for ($i = 0; $i -lt $sources.Items.Count; $i++) {
        if ($sources.Items[$i].Label -eq $script:preferredSource) {
            $sources.SelectedIndex = $i
            $script:preferredSource = $null
            return
        }
    }
}

function Select-Source {
    Stop-Child $script:capture
    $script:capture = $null
    $script:framePath = $null
    Set-Preview $null
    $source = $sources.SelectedItem
    if (!$source) { return }
    $status.Text = 'Previewing ' + $source.Label
    if ($source.Kind -eq 'device') {
        $script:captureNumber++
        $script:framePath = [IO.Directory]::CreateDirectory(
            (Join-Path $runFolder "capture-$script:captureNumber")).FullName
        $script:captureLogName = "capture-$script:captureNumber"
        $arguments = @('-hide_banner','-loglevel','warning','-nostdin','-y','-f','dshow',
            '-rtbufsize','128M','-i',('video=' + $source.Input),'-an','-vf','fps=12',
            '-c:v','mjpeg','-q:v','3','-threads','1','-f','image2','-atomic_writing','1',
            (Join-Path $script:framePath 'frame-%08d.jpg'))
        $script:capture = Start-LoggedProcess $ffmpeg $arguments $script:captureLogName
        $status.Text = 'Waiting for capture device frames...'
    }
}

function Set-Running([bool]$running) {
    $script:running = $running
    foreach ($control in @($sources,$refresh,$dataPath,$browse,$player,$clan,$ocr,$trace)) {
        $control.Enabled = !$running
    }
    if ($script:discovery) { $refresh.Enabled = $false }
    $start.Enabled = !$running
    $stop.Enabled = $running
}

function Stop-Connector {
    Stop-Child $script:detector
    Stop-Child $script:tunnel
    $script:detector = $script:tunnel = $null
    $copyPairing.Enabled = $false
    $pin.Text = 'Waiting for tracker'
    Remove-Item -LiteralPath $pinFile -ErrorAction SilentlyContinue
    $script:focusRequest = $null
    Set-Running $false
}

function Start-Connector {
    $source = $sources.SelectedItem
    if (!$source) { throw 'Select a source first.' }
    if (!$node) { throw 'Install Node.js 18 or later and reopen the app.' }
    $name = $player.Text.Trim()
    if ($trace.Checked -and !$name) { throw 'Enter a player name to enable diagnostic capture.' }
    if ($source.Kind -eq 'device') {
        $test = Open-CaptureFrame $script:framePath
        if (!$test) { throw 'The capture device has not produced a preview frame.' }
        $test.Dispose()
    }
    if (![IO.Path]::IsPathRooted($dataPath.Text)) { throw 'Choose an absolute data folder.' }
    $folder = [IO.Directory]::CreateDirectory($dataPath.Text).FullName
    $eventLog = Join-Path $folder 'sunburn-events.jsonl'
    $script:bridgePairingFile = Join-Path $folder 'bridge-pairing.key'
    $detectorArgs = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'sunburn.ps1'),'-Live','-Log',$eventLog,'-TrackerPinFile',$pinFile)
    if ($source.Kind -eq 'device') { $detectorArgs += @('-CaptureFrame',$script:framePath) }
    else { $detectorArgs += @('-Monitor',[string]$source.Index) }
    if ($name) { $detectorArgs += @('-Player',$name) }
    if ($name -and $clan.Text.Trim()) { $detectorArgs += @('-ClanTag',$clan.Text.Trim()) }
    if ($ocr.Checked) { $detectorArgs += '-ShowOcr' }
    if ($trace.Checked) { $detectorArgs += @('-FeedTrace',(Join-Path $folder 'feed-trace')) }
    try {
        $detectorLog.Clear()
        $tunnelLog.Clear()
        $script:tunnel = Start-LoggedProcess $node @((Join-Path $PSScriptRoot 'bridge.cjs'),$eventLog) 'tunnel'
        Set-Running $true
        $script:detectorArguments = $detectorArgs
        $script:startupDeadline = [DateTime]::UtcNow.AddSeconds(8)
        $status.Text = 'Starting local tunnel...'
    } catch { Stop-Connector; throw }
}

function Update-Log($box, [string]$name) {
    $parts = @(
        foreach ($leaf in @("$name.log","$name-errors.log")) {
            $file = Join-Path $runFolder $leaf
            if (Test-Path -LiteralPath $file) {
                Get-Content -LiteralPath $file -Tail 200 -ErrorAction SilentlyContinue
            }
        }
    )
    $text = $parts -join [Environment]::NewLine
    if ($box.Text -ne $text) {
        $box.Text = $text
        $box.SelectionStart = $box.TextLength
        $box.ScrollToCaret()
    }
}

$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
$bundledFfmpeg = Join-Path $PSScriptRoot 'ffmpeg.exe'
$ffmpeg = if (Test-Path -LiteralPath $bundledFfmpeg) { $bundledFfmpeg }
    else { (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue).Source }
$parent = Split-Path $PSScriptRoot -Parent
$script:running = $false
$script:captureNumber = 0
$runFolder = [IO.Directory]::CreateDirectory((Join-Path $env:TEMP ('CoDStreamingTools-' + [guid]::NewGuid().ToString('N')))).FullName
$defaultData = if ((Test-Path (Join-Path $parent 'sunburn-events.jsonl')) -or
    (Test-Path (Join-Path $parent 'mw4-camos.latest.json'))) { $parent }
    else { Join-Path $env:LOCALAPPDATA 'CamoConnector' }
$settingsFolder = Join-Path $env:LOCALAPPDATA 'CamoConnector'
$settingsFile = Join-Path $settingsFolder 'settings.json'
$settings = if (Test-Path -LiteralPath $settingsFile) {
    try { Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json } catch { $null }
}
if ($settings.dataPath) { $defaultData = $settings.dataPath }
$pinFile = Join-Path $runFolder 'tracker-focus.txt'
$focusClient = [Net.Http.HttpClient]::new()
$focusClient.Timeout = [TimeSpan]::FromSeconds(1)
$script:nextFocusPoll = [DateTime]::MinValue
$updater = Join-Path $PSScriptRoot 'connector-updater.ps1'
$updateRepository = 'JoshuaRibeiro/camo-connector'
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json).version

$form = [Windows.Forms.Form]::new()
$form.Text = "CoD Streaming Tools $version"
$form.ClientSize = [Drawing.Size]::new(1050,660)
$form.MinimumSize = [Drawing.Size]::new(880,570)
$form.StartPosition = 'CenterScreen'
$form.Font = [Drawing.Font]::new('Segoe UI',10)
$tabs = [Windows.Forms.TabControl]::new()
$tabs.Dock = 'Fill'
$form.Controls.Add($tabs)
$updateBar = [Windows.Forms.FlowLayoutPanel]::new()
$updateBar.Dock = 'Top'
$updateBar.Height = 40
$updateBar.FlowDirection = 'RightToLeft'
$updateBar.WrapContents = $false
$updateBar.Padding = [Windows.Forms.Padding]::new(8,3,8,0)
$form.Controls.Add($updateBar)
$updateButton = [Windows.Forms.Button]::new()
$updateButton.Text = 'Check for updates'
$updateButton.Size = [Drawing.Size]::new(152,28)
$updateBar.Controls.Add($updateButton)
$updateStatus = [Windows.Forms.Label]::new()
$updateStatus.AutoSize = $true
$updateStatus.Margin = [Windows.Forms.Padding]::new(3,7,7,0)
$updateBar.Controls.Add($updateStatus)
$updateTooltip = [Windows.Forms.ToolTip]::new()
$updateTooltip.SetToolTip($updateStatus, '')
$setup = [Windows.Forms.TabPage]::new('Setup')
$tabs.TabPages.Add($setup)

$logsPage = [Windows.Forms.TabPage]::new('Logs')
$tabs.TabPages.Add($logsPage)
$logLayout = [Windows.Forms.TableLayoutPanel]::new()
$logLayout.Dock = 'Fill'
$logLayout.ColumnCount = 1
$logLayout.RowCount = 3
$logsPage.Controls.Add($logLayout)

function Add-LogPane([string]$name, [int]$row) {
    $group = [Windows.Forms.GroupBox]::new()
    $group.Text = $name
    $group.Dock = 'Fill'
    $box = [Windows.Forms.TextBox]::new()
    $box.Multiline = $true
    $box.ReadOnly = $true
    $box.ScrollBars = 'Both'
    $box.WordWrap = $false
    $box.Dock = 'Fill'
    $box.Font = [Drawing.Font]::new('Consolas',10)
    $group.Controls.Add($box)
    [void]$logLayout.RowStyles.Add([Windows.Forms.RowStyle]::new('Percent', [single](100 / 3)))
    $logLayout.Controls.Add($group,0,$row)
    return $box
}

$detectorLog = Add-LogPane 'Detector log' 0
$tunnelLog = Add-LogPane 'Tunnel log' 1
$captureLog = Add-LogPane 'Capture log' 2
$layout = [Windows.Forms.TableLayoutPanel]::new()
$layout.Dock = 'Fill'
$layout.Padding = [Windows.Forms.Padding]::new(14)
$layout.ColumnCount = 2
$layout.RowCount = 2
[void]$layout.ColumnStyles.Add([Windows.Forms.ColumnStyle]::new('Percent',100))
[void]$layout.ColumnStyles.Add([Windows.Forms.ColumnStyle]::new('Absolute',330))
[void]$layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Percent',100))
[void]$layout.RowStyles.Add([Windows.Forms.RowStyle]::new('Absolute',28))
$setup.Controls.Add($layout)
$preview = [Windows.Forms.PictureBox]::new()
$preview.Dock = 'Fill'
$preview.SizeMode = 'Zoom'
$preview.BackColor = [Drawing.Color]::FromArgb(24,26,29)
$layout.Controls.Add($preview,0,0)
$options = [Windows.Forms.FlowLayoutPanel]::new()
$options.Dock = 'Fill'
$options.FlowDirection = 'TopDown'
$options.WrapContents = $false
$options.AutoScroll = $true
$options.Padding = [Windows.Forms.Padding]::new(12,0,0,0)
$layout.Controls.Add($options,1,0)
$status = [Windows.Forms.Label]::new()
$status.Dock = 'Fill'
$status.AutoEllipsis = $true
$layout.Controls.Add($status,0,1)
$layout.SetColumnSpan($status,2)

function Add-OptionLabel([string]$text) {
    $control = [Windows.Forms.Label]::new()
    $control.Text = $text
    $control.AutoSize = $true
    $options.Controls.Add($control)
}
function Add-OptionButton([string]$text) {
    $control = [Windows.Forms.Button]::new()
    $control.Text = $text
    $control.Width = 282
    $control.Height = 30
    $options.Controls.Add($control)
    return $control
}
function Add-OptionField([string]$name, [string]$value) {
    Add-OptionLabel $name
    $control = [Windows.Forms.TextBox]::new()
    $control.Width = 282
    $control.Text = $value
    $options.Controls.Add($control)
    return $control
}
function Add-OptionToggle([string]$name) {
    $control = [Windows.Forms.CheckBox]::new()
    $control.AutoSize = $true
    $control.Text = $name
    $options.Controls.Add($control)
    return $control
}

Add-OptionLabel 'Capture source'
$sources = [Windows.Forms.ComboBox]::new()
$sources.DropDownStyle = 'DropDownList'
$sources.DisplayMember = 'Label'
$sources.Width = 282
$options.Controls.Add($sources)
$refresh = Add-OptionButton 'Refresh sources'
$deviceStatus = [Windows.Forms.Label]::new()
$deviceStatus.Width = 282
$deviceStatus.Height = 34
$options.Controls.Add($deviceStatus)
$dataPath = Add-OptionField 'Data folder' $defaultData
$browse = Add-OptionButton 'Choose data folder'
$clan = Add-OptionField 'Clan tag (optional)' $settings.clan
$player = Add-OptionField 'Player name (optional)' $settings.player
$pin = Add-OptionField 'Pinned weapon from Camo Tracker' 'Waiting for tracker'
$pin.ReadOnly = $true
$pin.BackColor = [Drawing.SystemColors]::Control
$pin.TabStop = $false
$ocr = Add-OptionToggle 'Show OCR diagnostics'
$trace = Add-OptionToggle 'Save diagnostic frames'
$ocr.Checked = [bool]$settings.ocr
$trace.Checked = [bool]$settings.trace
$start = Add-OptionButton 'Start connector'
$stop = Add-OptionButton 'Stop connector'
$copyPairing = Add-OptionButton 'Copy browser pairing key'
$copyPairing.Enabled = $false
$copyPairing.Add_Click({
    $key = $focusClient.DefaultRequestHeaders.Authorization.Parameter
    if (!$key) { return }
    [Windows.Forms.Clipboard]::SetText($key)
    $status.Text = 'Paste the pairing key into the CoD Streaming Tools browser extension.'
})
Set-Running $false

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ConnectorWindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void Apply(IntPtr window, bool dark)
    {
        int value = dark ? 1 : 0;
        const int UseImmersiveDarkMode = 20;
        DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref value, sizeof(int));
    }
}
'@

function Get-WindowsAppTheme {
    if ([Windows.Forms.SystemInformation]::HighContrast) { return 'Light' }
    $personalize = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -ErrorAction SilentlyContinue
    if ($personalize -and $personalize.AppsUseLightTheme -eq 0) { return 'Dark' }
    return 'Light'
}

function Set-ThemeColors($control, $palette, [bool]$dark) {
    if ($control -eq $preview) { return }
    $control.BackColor = $palette.Background
    $control.ForeColor = $palette.Text
    if ($control -is [Windows.Forms.TextBoxBase] -or $control -is [Windows.Forms.ComboBox]) {
        $control.BackColor = if ($control -eq $pin) { $palette.Button } else { $palette.Field }
        if ($control -is [Windows.Forms.ComboBox]) {
            $control.FlatStyle = if ($dark) { 'Flat' } else { 'Standard' }
        }
    } elseif ($control -is [Windows.Forms.Button]) {
        $control.FlatStyle = if ($dark) { 'Flat' } else { 'Standard' }
        $control.UseVisualStyleBackColor = !$dark
        if ($dark) {
            $control.BackColor = $palette.Button
            $control.FlatAppearance.BorderColor = $palette.Border
        }
    } elseif ($control -is [Windows.Forms.TabPage]) {
        $control.UseVisualStyleBackColor = !$dark
    }
    foreach ($child in $control.Controls) { Set-ThemeColors $child $palette $dark }
}

function Set-WindowsAppTheme([string]$theme) {
    if ($script:currentTheme -eq $theme) { return }
    $script:currentTheme = $theme
    $dark = $theme -eq 'Dark'
    $script:themePalette = if ($dark) {
        @{
            Background = [Drawing.Color]::FromArgb(30,32,36)
            Field = [Drawing.Color]::FromArgb(22,24,27)
            Button = [Drawing.Color]::FromArgb(48,51,57)
            Border = [Drawing.Color]::FromArgb(90,95,104)
            Text = [Drawing.Color]::FromArgb(234,236,240)
        }
    } else {
        @{
            Background = [Drawing.SystemColors]::Control
            Field = [Drawing.SystemColors]::Window
            Button = [Drawing.SystemColors]::Control
            Text = [Drawing.SystemColors]::ControlText
        }
    }
    Set-ThemeColors $form $script:themePalette $dark
    $tabs.DrawMode = if ($dark) { 'OwnerDrawFixed' } else { 'Normal' }
    [ConnectorWindowTheme]::Apply($form.Handle, $dark)
    $form.Invalidate($true)
}

$tabs.Add_DrawItem({
    param($sender, $eventArgs)
    $tab = $sender.TabPages[$eventArgs.Index]
    $color = if ($eventArgs.Index -eq $sender.SelectedIndex) { $script:themePalette.Button }
        else { $script:themePalette.Background }
    $brush = [Drawing.SolidBrush]::new($color)
    try {
        $eventArgs.Graphics.FillRectangle($brush, $eventArgs.Bounds)
        [Windows.Forms.TextRenderer]::DrawText($eventArgs.Graphics, $tab.Text, $sender.Font,
            $eventArgs.Bounds, $script:themePalette.Text, [Windows.Forms.TextFormatFlags]'HorizontalCenter, VerticalCenter')
    } finally { $brush.Dispose() }
})
$script:nextThemeRead = [DateTime]::MinValue

function Save-Settings {
    [void][IO.Directory]::CreateDirectory($settingsFolder)
    $selected = if ($script:preferredSource) { $script:preferredSource } else { $sources.SelectedItem.Label }
    $values = @{
        dataPath = $dataPath.Text; player = $player.Text; clan = $clan.Text
        source = $selected; ocr = $ocr.Checked; trace = $trace.Checked
    }
    $pending = Join-Path $settingsFolder ("settings-" + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($pending, ($values | ConvertTo-Json))
        if ([IO.File]::Exists($settingsFile)) { [IO.File]::Replace($pending,$settingsFile,$pending + '.previous') }
        else { [IO.File]::Move($pending,$settingsFile) }
    } finally {
        [IO.File]::Delete($pending)
        [IO.File]::Delete($pending + '.previous')
    }
}

function Update-TrackerFocus {
    if ($script:focusRequest -and $script:focusRequest.IsCompleted) {
        $request = $script:focusRequest
        $script:focusRequest = $null
        $focus = if ($request.Status -eq 'RanToCompletion') { ($request.Result | ConvertFrom-Json).weapon }
        elseif ($request.IsFaulted) { [void]$request.Exception }
        $pin.Text = if ($focus) { $focus } else { 'Waiting for tracker' }
        if ($focus) { [IO.File]::WriteAllText($pinFile, [string]$focus) }
        else { [IO.File]::Delete($pinFile) }
    }
    if (!$script:focusRequest -and [DateTime]::UtcNow -ge $script:nextFocusPoll) {
        $script:nextFocusPoll = [DateTime]::UtcNow.AddSeconds(2)
        $script:focusRequest = $focusClient.GetStringAsync('http://127.0.0.1:47150/focus')
    }
}

$updateButton.Add_Click({
    $updateTooltip.SetToolTip($updateStatus, '')
    $copy = $null
    try {
        if (!$script:availableVersion) {
            $script:updateCheck = Start-LoggedProcess 'powershell.exe' @('-NoProfile','-File',$updater,
                '-Mode','Check','-Repository',$updateRepository,'-InstallRoot',$PSScriptRoot) 'update-check'
            $updateButton.Enabled = $false
            $updateStatus.Text = 'Checking...'
            return
        }
        Save-Settings
        $copy = Join-Path $PSScriptRoot ('CoDStreamingTools-Updater-' + [guid]::NewGuid().ToString('N') + '.ps1')
        Copy-Item -LiteralPath $updater -Destination $copy
        $arguments = @('-NoProfile','-File',$copy,'-Mode','Install','-Repository',$updateRepository,
            '-InstallRoot',$PSScriptRoot,'-ExpectedVersion',$script:availableVersion,'-AppProcessId',[string]$PID,'-StagedCopy')
        Start-Process -FilePath 'powershell.exe' -ArgumentList (($arguments | ForEach-Object { Quote-Argument $_ }) -join ' ') -WindowStyle Hidden
        $script:updateClosing = $true
        $form.Close()
    } catch {
        if ($copy) { Remove-Item -LiteralPath $copy -ErrorAction SilentlyContinue }
        $updateStatus.Text = if ($script:availableVersion) { 'Could not start update' } else { 'Update check failed' }
        $updateTooltip.SetToolTip($updateStatus, $_.Exception.Message)
    }
})
$form.Add_FormClosing({
    if (!$SmokeTest -and !$script:updateClosing) {
        try { Save-Settings } catch {
            [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'Could not save CoD Streaming Tools settings')
        }
    }
})

$refresh.Add_Click({ try { Find-Sources } catch { $status.Text = $_.Exception.Message } })
$sources.Add_SelectedIndexChanged({ try { Select-Source } catch { $status.Text = $_.Exception.Message } })
$browse.Add_Click({
    $dialog = [Windows.Forms.FolderBrowserDialog]::new()
    try {
        if (Test-Path -LiteralPath $dataPath.Text) { $dialog.SelectedPath = $dataPath.Text }
        if ($dialog.ShowDialog($form) -eq 'OK') { $dataPath.Text = $dialog.SelectedPath }
    } finally { $dialog.Dispose() }
})
$start.Add_Click({ try { Start-Connector } catch { $status.Text = $_.Exception.Message } })
$stop.Add_Click({ Stop-Connector; $status.Text = 'Connector stopped. Preview is active.' })

$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 250
$timer.Add_Tick({
    try {
        if ([DateTime]::UtcNow -ge $script:nextThemeRead) {
            $script:nextThemeRead = [DateTime]::UtcNow.AddSeconds(1)
            Set-WindowsAppTheme (Get-WindowsAppTheme)
        }
        if ($script:discovery -and $script:discovery.HasExited) {
            Stop-Child $script:discovery
            $script:discovery = $null
            $devices = @(Read-CaptureDevices (Get-Content (Join-Path $runFolder 'device-discovery-errors.log')))
            foreach ($device in $devices) { Add-Source $device }
            if (!$script:running) { Restore-Source }
            $deviceStatus.Text = if ($devices.Count) { "$($devices.Count) capture device(s) available" }
                else { 'No DirectShow capture devices found.' }
            $refresh.Enabled = !$script:running
        }
        $source = $sources.SelectedItem
        if ($script:framePath) {
            [IO.Directory]::GetFiles($script:framePath, 'frame-*.jpg') | Sort-Object -Descending |
                Select-Object -Skip 12 | ForEach-Object {
                    Remove-Item -LiteralPath $_ -ErrorAction SilentlyContinue
                }
        }
        if ($source -and $tabs.SelectedTab -eq $setup) {
            $image = Get-Preview $source
            Set-Preview $image
            if (!$script:running -and $source.Kind -eq 'device') {
                $status.Text = if ($image) { 'Previewing ' + $source.Label }
                    elseif ($script:capture -and $script:capture.HasExited) { 'Capture could not start. See Capture log.' }
                    else { 'Waiting for capture device frames...' }
            }
        }
        if ($tabs.SelectedTab -eq $logsPage) {
            Update-Log $detectorLog 'detector'
            Update-Log $tunnelLog 'tunnel'
            if ($script:captureLogName) { Update-Log $captureLog $script:captureLogName }
        }
        if ($script:updateCheck -and $script:updateCheck.HasExited) {
            $exitCode = $script:updateCheck.ExitCode
            Stop-Child $script:updateCheck
            $script:updateCheck = $null
            $updateButton.Enabled = $true
            if ($exitCode -eq 0) {
                $result = Get-Content -LiteralPath (Join-Path $runFolder 'update-check.log') -Raw | ConvertFrom-Json
                if ($result.available) {
                    $script:availableVersion = $result.latest
                    $updateStatus.Text = "Version $($result.latest) is available."
                    $updateButton.Text = 'Update and restart'
                } else { $updateStatus.Text = 'Up to date!' }
            } else {
                $updateStatus.Text = 'Update check failed'
                $updateTooltip.SetToolTip($updateStatus, ((Get-Content -LiteralPath (Join-Path $runFolder 'update-check-errors.log') -TotalCount 2) -join ' '))
            }
        }
        if ($script:running) {
            if ($script:detector) {
                try { Update-TrackerFocus } catch { $pin.Text = 'Waiting for tracker' }
            }
            if ($script:tunnel.HasExited) {
                Stop-Connector
                $status.Text = 'Tunnel stopped. See Tunnel log.'
            } elseif ($script:detector -and $script:detector.HasExited) {
                Stop-Connector
                $status.Text = 'Detector stopped. See Detector log.'
            } elseif ($source.Kind -eq 'device' -and $script:capture.HasExited) {
                Stop-Connector
                $status.Text = 'Capture stopped. See Capture log.'
            } elseif (!$script:detector) {
                $ready = Get-Content (Join-Path $runFolder 'tunnel.log') -ErrorAction SilentlyContinue |
                    Select-String -SimpleMatch 'Camo bridge ready'
                if ($ready) {
                    $key = [IO.File]::ReadAllText($script:bridgePairingFile).Trim()
                    if ($key -cnotmatch '^[0-9a-f]{64}$') { throw 'The browser pairing key is invalid.' }
                    $focusClient.DefaultRequestHeaders.Authorization =
                        [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $key)
                    $copyPairing.Enabled = $true
                    $script:detector = Start-LoggedProcess 'powershell.exe' $script:detectorArguments 'detector'
                    $status.Text = 'Running | ' + $source.Label
                } elseif ([DateTime]::UtcNow -ge $script:startupDeadline) {
                    Stop-Connector
                    $status.Text = 'Tunnel did not become ready. See Tunnel log.'
                }
            }
        }
    } catch {
        if ($script:running) { Stop-Connector }
        $status.Text = $_.Exception.Message
    }
})
try {
    Set-WindowsAppTheme (Get-WindowsAppTheme)
    Find-Sources
    if ($SmokeTest) {
        if (!$sources.Items.Count) { throw 'No desktop monitors enumerated.' }
        $image = Get-Preview $sources.SelectedItem
        if (!$image) { throw 'Monitor preview could not be captured.' }
        $image.Dispose()
        $sample = @('[dshow] "Elgato HD60 X" (none)','[dshow] Alternative name "@device_pnp_test"','[dshow] "Microphone" (audio)')
        $video = @(Read-CaptureDevices $sample)
        if ($video.Count -ne 1 -or $video[0].Input -ne '@device_pnp_test') { throw 'Capture device enumeration failed.' }
        if ($logLayout.Controls.Count -ne 3 -or !$pin.ReadOnly) { throw 'Logs or tracker focus controls are missing.' }
        if ($tabs.TabPages.Count -ne 2 -or $updateButton.Text -ne 'Check for updates' -or $script:updateCheck) {
            throw 'The manual update control is missing or started an automatic check.'
        }
        Set-WindowsAppTheme 'Dark'
        if ($tabs.DrawMode -ne 'OwnerDrawFixed' -or $detectorLog.BackColor -ne $script:themePalette.Field) {
            throw 'Dark theme was not applied.'
        }
        Set-WindowsAppTheme 'Light'
        if ($tabs.DrawMode -ne 'Normal' -or $detectorLog.BackColor -ne [Drawing.SystemColors]::Window) {
            throw 'Light theme was not restored.'
        }
        'Monitor preview, capture-device parsing, log panes, tracker focus, and both app themes passed.'
    } else {
        $timer.Start()
        [void]$form.ShowDialog()
    }
} finally {
    $timer.Stop()
    Stop-Connector
    Stop-Child $script:capture
    Stop-Child $script:discovery
    Stop-Child $script:updateCheck
    $focusClient.Dispose()
    Set-Preview $null
    $timer.Dispose()
    $updateTooltip.Dispose()
    $form.Dispose()
    # This folder is unique to this run and only contains transient frames and display logs.
    [IO.Directory]::Delete($runFolder,$true)
}
