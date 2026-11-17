# Start with Windows PowerShell 5.1.
# Screenshot: powershell.exe -NoProfile -File .\sunburn.ps1 -Image .\frame.png
# Live: powershell.exe -NoProfile -File .\sunburn.ps1 -Live -Monitor 1
param(
    [string]$Image,
    [switch]$UnlockBadge,
    [switch]$ChallengeBanner,
    [switch]$CamoMenu,
    [switch]$TrackedCamos,
    [switch]$LobbyScreen,
    [switch]$Live,
    [string]$Player,
    [string]$ClanTag,
    [string]$FeedTrace,
    [string]$PinnedWeapon,
    [string]$TrackerPinFile,
    [int]$Monitor = -1,
    [string]$CaptureFrame,
    [string]$Log = (Join-Path $PSScriptRoot 'sunburn-events.jsonl'),
    [switch]$ShowOcr,
    [hashtable]$CombatWorker
)

$ErrorActionPreference = 'Stop'
if ([bool]$Image -eq [bool]$Live) { throw 'Specify either -Image or -Live.' }
if ($UnlockBadge -and !$Image) { throw '-UnlockBadge requires a screenshot with -Image.' }
if ($ChallengeBanner -and (!$Image -or $UnlockBadge)) { throw '-ChallengeBanner requires -Image and runs separately from -UnlockBadge.' }
if ($CamoMenu -and (!$Image -or $UnlockBadge -or $ChallengeBanner)) { throw '-CamoMenu requires -Image and runs separately from the banner previews.' }
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Start this script with powershell.exe (Windows PowerShell 5.1).'
}
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, System.Runtime.WindowsRuntime
if (!('FeedColourMask' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class FeedColourMask {
    public static void Apply(Bitmap image, bool badgeTitle) {
        var area = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try {
            int stride = Math.Abs(data.Stride);
            var pixels = new byte[stride];
            for (int y = 0; y < image.Height; y++) {
                var scan = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(scan, pixels, 0, stride);
                for (int x = 0; x < image.Width * 4; x += 4) {
                    int blue = pixels[x], green = pixels[x + 1], red = pixels[x + 2];
                    bool warm = red >= 145 && red - blue >= 45 && red >= green + 12;
                    bool cool = green >= 120 && blue >= 140 && blue - red >= 45 && green - red >= 28;
                    bool yellow = red >= 145 && green >= 145 && Math.Min(red, green) - blue >= 28;
                    byte value = (byte)((badgeTitle ? yellow : warm || cool) ? 255 : 0);
                    pixels[x] = pixels[x + 1] = pixels[x + 2] = value;
                    pixels[x + 3] = 255;
                }
                Marshal.Copy(pixels, 0, scan, stride);
            }
        } finally {
            image.UnlockBits(data);
        }
    }
}
'@
}
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
$ocr = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage(
    [Windows.Globalization.Language]::new('en-US')
)
if (!$ocr) { throw 'Windows English (United States) OCR is required.' }
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } |
    Select-Object -First 1
$decoderTask = $asTask.MakeGenericMethod([Windows.Graphics.Imaging.BitmapDecoder])
$bitmapTask = $asTask.MakeGenericMethod([Windows.Graphics.Imaging.SoftwareBitmap])
$resultTask = $asTask.MakeGenericMethod([Windows.Media.Ocr.OcrResult])

function Read-Hud($frame, $area, [switch]$Positioned, [switch]$Contrast, [switch]$Invert, [switch]$FeedColours, [switch]$BadgeTitle) {
    $x = [int]($area[0] * $frame.Width)
    $y = [int]($area[1] * $frame.Height)
    $w = [int]($area[2] * $frame.Width)
    $h = [int]($area[3] * $frame.Height)
    $scale = [Math]::Min(3, [Windows.Media.Ocr.OcrEngine]::MaxImageDimension / [Math]::Max($w, $h))
    $crop = [Drawing.Bitmap]::new([int]($w * $scale), [int]($h * $scale))
    $graphics = [Drawing.Graphics]::FromImage($crop)
    $stream = [IO.MemoryStream]::new()
    $attributes = $null
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $destination = [Drawing.Rectangle]::new(0, 0, $crop.Width, $crop.Height)
        if ($Contrast -or $Invert) {
            $attributes = [Drawing.Imaging.ImageAttributes]::new()
            $matrix = [Drawing.Imaging.ColorMatrix]::new()
            $gain = if ($Invert) { -1 } else { 1.6 }
            $offset = if ($Invert) { 1 } else { -0.36 }
            $matrix.Matrix00 = $gain
            $matrix.Matrix11 = $gain
            $matrix.Matrix22 = $gain
            $matrix.Matrix40 = $offset
            $matrix.Matrix41 = $offset
            $matrix.Matrix42 = $offset
            $attributes.SetColorMatrix($matrix)
            $graphics.DrawImage($frame, $destination, $x, $y, $w, $h, [Drawing.GraphicsUnit]::Pixel, $attributes)
        } else {
            $graphics.DrawImage($frame, $destination,
                [Drawing.Rectangle]::new($x, $y, $w, $h), [Drawing.GraphicsUnit]::Pixel)
        }
        if ($FeedColours -or $BadgeTitle) { [FeedColourMask]::Apply($crop, $BadgeTitle.IsPresent) }
        $crop.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $stream.Position = 0
        $randomStream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($stream)
        try {
            $task = $decoderTask.Invoke($null, @([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($randomStream)))
            $decoder = $task.GetAwaiter().GetResult()
            $task = $bitmapTask.Invoke($null, @($decoder.GetSoftwareBitmapAsync()))
            $bitmap = $task.GetAwaiter().GetResult()
            try {
                $task = $resultTask.Invoke($null, @($ocr.RecognizeAsync($bitmap)))
                $result = $task.GetAwaiter().GetResult()
                foreach ($line in $result.Lines) {
                    if ($Positioned) {
                        foreach ($word in $line.Words) {
                            [pscustomobject]@{
                                Text = $line.Text
                                X = $word.BoundingRect.X / $crop.Width
                                Y = $word.BoundingRect.Y / $crop.Height
                                Height = $word.BoundingRect.Height / $crop.Height
                            }
                            break
                        }
                    } else {
                        $line.Text
                    }
                }
            } finally { $bitmap.Dispose() }
        } finally { $randomStream.Dispose() }
    } finally {
        $stream.Dispose()
        if ($attributes) { $attributes.Dispose() }
        $graphics.Dispose()
        $crop.Dispose()
    }
}

function Write-MatchEvent([string]$Path, [string]$Json) {
    $fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Json + [Environment]::NewLine)
    $stream = $null
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (!$stream) {
        try {
            $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        } catch {
            $cause = $_.Exception.GetBaseException()
            if ($cause -isnot [IO.IOException] -or ($cause.HResult -band 0xFFFF) -notin @(32, 33) -or
                $deadline.ElapsedMilliseconds -ge 3000) { throw }
            Start-Sleep -Milliseconds 25
        }
    }
    # Retry acquisition only. A write that fails after emitting bytes is ambiguous.
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Write-Status([string]$message) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $message"
}

function Read-TrackedCamoProgress($frame) {
    if ($Image -and $TrackedCamos -and $frame.Width / $frame.Height -lt 1.4) {
        $heading = (Read-Hud $frame @(0.02, 0.01, 0.36, 0.09)) -join ' '
        if ($heading -notmatch '\bCAM[O0]S\b') { return }
        Read-TrackedCamoArea $frame @(0.24, 0.13, 0.50, 0.70) @(0.095, 0.145, 0.155, 0.005)
        return
    }
    $heading = (Read-Hud $frame @(0.740, 0.117, 0.110, 0.037)) -join ' '
    $area = @(0.792, 0.159, 0.172, 0.335)
    $thumbnail = @(0.760, 0.039, 0.069, 0.004)
    if ($heading -notmatch '\bCAM[O0]S\b') {
        $heading = (Read-Hud $frame @(0.067, 0.114, 0.110, 0.038)) -join ' '
        $area = @(0.121, 0.156, 0.169, 0.335)
        $thumbnail = @(0.084, 0.039, 0.069, 0.004)
    }
    if ($heading -notmatch '\bCAM[O0]S\b') { return }
    Read-TrackedCamoArea $frame $area $thumbnail
}

function Read-TrackedCamoArea($frame, $area, $thumbnail) {
    $lines = @(Read-Hud $frame $area -Positioned -Contrast | Sort-Object Y)
    if ($ShowOcr) { Write-Status "Tracked camo OCR: $($lines.Text -join ' | ')" }
    $rows = @(ConvertFrom-TrackedCamoLines $lines $frame $area $thumbnail)
    foreach ($row in $rows) { $row }
    if (@($rows | Where-Object weapon -eq 'Krait P68').Count -eq 0) {
        $plain = @(Read-Hud $frame $area -Positioned | Sort-Object Y)
        if ($ShowOcr) { Write-Status "Tracked camo fallback OCR: $($plain.Text -join ' | ')" }
        $fallback = @(ConvertFrom-TrackedCamoLines $plain $frame $area $thumbnail | Where-Object weapon -eq 'Krait P68')
        if (!$fallback.Count) {
            $counterArea = @(($area[0] + $area[2] * 0.66), $area[1], ($area[2] * 0.34), $area[3])
            $counters = @(Read-Hud $frame $counterArea -Positioned -Contrast | Where-Object Text -Match '^\s*\d+\s*/\s*\d+\s*$')
            if ($ShowOcr) { Write-Status "Tracked camo counter OCR: $($counters.Text -join ' | ')" }
            $fallback = @(ConvertFrom-TrackedCamoLines (@($plain + $counters) | Sort-Object Y) $frame $area $thumbnail |
                Where-Object weapon -eq 'Krait P68')
        }
        $fallback
    }
}

function Find-TrackedCamoArtwork($frame, $area, $thumbnail, $header, [int]$goal) {
    if (!$frame -or !$thumbnail -or !$specialCatalog) { return }
    $entries = @($specialCatalog.PSObject.Properties[$header.Name].Value |
        Where-Object { $_.name -and $_.artworkRGB12 })
    if ($entries.Count -lt 2) { return }
    $x, $w, $h, $offset = $thumbnail
    $top = $area[1] + $header.Y * $area[3] + $offset
    if ($top + $h -gt 1) { return }
    $sample = [Drawing.Bitmap]::new(12, 12)
    $graphics = [Drawing.Graphics]::FromImage($sample)
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $source = [Drawing.Rectangle]::new(
            [int](($x + $w * 0.17) * $frame.Width), [int](($top + $h * 0.19) * $frame.Height),
            [int]($w * 0.66 * $frame.Width), [int]($h * 0.66 * $frame.Height))
        $graphics.DrawImage($frame, [Drawing.Rectangle]::new(0,0,12,12), $source, [Drawing.GraphicsUnit]::Pixel)
        $pixels = @(for ($y = 0; $y -lt 12; $y++) {
            for ($column = 0; $column -lt 12; $column++) { $sample.GetPixel($column, $y) }
        })
        $ranked = @(foreach ($entry in $entries) {
            $reference = [Convert]::FromBase64String($entry.artworkRGB12)
            if ($reference.Length -ne 432) { continue }
            $difference = 0
            for ($i = 0; $i -lt 144; $i++) {
                $pixel = $pixels[$i]
                $difference += [Math]::Abs($pixel.R - $reference[$i*3]) +
                    [Math]::Abs($pixel.G - $reference[$i*3+1]) + [Math]::Abs($pixel.B - $reference[$i*3+2])
            }
            [pscustomobject]@{ Score = $difference / 432; Camo = $entry }
        }) | Sort-Object Score
        if ($ShowOcr) { Write-Status ("Thumbnail scores | $($header.Name) | " + (($ranked | % { "$($_.Camo.name)=$([Math]::Round($_.Score,1))" }) -join ', ')) }
        if (@($ranked).Count -ge 2 -and $ranked[0].Score -le 31 -and
            ($ranked[1].Score - $ranked[0].Score) -ge 8 -and
            (!$ranked[0].Camo.goal -or $ranked[0].Camo.goal -eq $goal)) { return $ranked[0].Camo }
    } finally {
        $graphics.Dispose()
        $sample.Dispose()
    }
}

function ConvertFrom-TrackedCamoLines($lines, $frame, $area, $thumbnail) {
    $headers = @(foreach ($line in $lines) {
        $label = $line.Text -replace '[^a-z0-9]', ''
        $name = $weaponsByLabel[$label]
        if (!$name -and $label -match '^(?:(?:KRAIT|KARIT|CRATE)P?[G6][8B]|KRAIT)(?:XP)?$') {
            $name = $weaponsByLabel['kraitp68']
        }
        if (!$name -and $label -eq 'RPGB') { $name = $weaponsByLabel['rpg8'] }
        if ($name) { [pscustomobject]@{ Y = $line.Y; Name = $name } }
    })
    for ($i = 0; $i -lt $headers.Count; $i++) {
        $header = $headers[$i]
        $limit = $header.Y + 0.40
        if ($i + 1 -lt $headers.Count) { $limit = [Math]::Min($limit, $headers[$i + 1].Y) }
        $row = ($lines | Where-Object { $_.Y -gt $header.Y -and $_.Y -lt $limit } |
            ForEach-Object Text) -join ' '
        if ($row -notmatch '^(.*?)\s*\b(\d{1,4})\s*[/|]\s*(\d{1,4})\b') { continue }
        $objective = $Matches[1].Trim() -replace '\bPlavers\b', 'Players' -replace '\bHan[gq]in[gq]\b', 'Hanging'
        $value = [int]$Matches[2]
        $goal = [int]$Matches[3]
        if (!$goal -or $value -gt $goal) { continue }
        if ($header.Name -eq 'Krait P68' -and $goal -eq 15 -and
            $objective -match '\bGet\s+15\s+Point[\s-]*Blank\s+Kills\b') {
            $objective = 'Get 15 Point Blank Kills against Players'
        }
        $camo = $null
        $identity = $null
        $type = if ($objective -match '\bReach Weapon Level\s+(\d+)$') {
            if ([int]$Matches[1] -ne $goal) { continue }
            'weapon-level'
        } else {
            $names = @($lines | Where-Object { $_.Y -gt $header.Y -and $_.Y -lt $limit } | ForEach-Object {
                $_.Text -replace '[^a-z0-9]', ''
            })
            $named = @($specialCatalog.PSObject.Properties[$header.Name].Value | Where-Object {
                $_.name -and (!$_.goal -or $_.goal -eq $goal) -and ($_.name -replace '[^a-z0-9]', '') -in $names
            })
            if ($named.Count -eq 1) {
                $camo = $named[0]
                $identity = 'name'
            } else {
                $camo = Find-TrackedCamoArtwork $frame $area $thumbnail $header $goal
                $identity = 'artwork'
            }
            if (!$camo) { continue }
            'camo'
        }
        if ($camo) { $objective = if ($camo.objective) { $camo.objective } else { $camo.name } }
        $key = if ($header.Name -eq 'Signal 50' -and $goal -eq 40 -and $camo.name -eq 'Sunburn') { 'signal-50:sp-3' }
        [ordered]@{ weapon = $header.Name; objective = $objective; count = $value; goal = $goal; type = $type; key = $key
            camo = $(if ($camo) { $camo.name }); identity = $identity }
    }
}

$streakArtwork = [Convert]::FromBase64String('Xi5KQktthkRFMGiaZUVQSMaDWD9Gr4FIPl5FX61oPT1Pu2VMQ0NGbV89Q2erakddMlSIeEdIUYGgXU1PQXa9WU9DdnNbR19Im3FDT1JRfaBQWEdno01UWkJ1gX1VTlegVEtgRW96e01IdoWDUUxLoqp2bFRXf29JXkdz7I1MUVuXfU5aRFB+kGNPV27AZU1U')
$autumnArtwork = [Convert]::FromBase64String('RZWzcEUwr5aRloq6O4eERSxClnd9n4Q7kVpCLytSVDhkmHoiin9CFkiSOzhdZ207PGkxN4x9VE9XT4+WXjtERoR2mldEVZSrhTdCRnKgn4dQUlRiUZNZR2iTo5NoXDhXT725T0lijXSAb1B+RKnJlENggZCXa3BvOW+fvIqQiGBgc25Zb1FqlKaji1RHX0dk')

function Test-ChallengeArtwork($frame, [byte[]]$reference) {
    $sample = [Drawing.Bitmap]::new(12, 12)
    $graphics = [Drawing.Graphics]::FromImage($sample)
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        foreach ($dy in @(0, -0.004, 0.004)) {
            $source = [Drawing.Rectangle]::new(
                [int]($frame.Width * 0.478), [int]($frame.Height * (0.158 + $dy)),
                [int]($frame.Width * 0.044), [int]($frame.Height * 0.081))
            $graphics.DrawImage($frame, [Drawing.Rectangle]::new(0, 0, 12, 12), $source, [Drawing.GraphicsUnit]::Pixel)
            $difference = 0
            for ($y = 0; $y -lt 12; $y++) {
                for ($x = 0; $x -lt 12; $x++) {
                    $pixel = $sample.GetPixel($x, $y)
                    $difference += [Math]::Abs(($pixel.R + $pixel.G + $pixel.B) / 3 - $reference[$y * 12 + $x])
                }
            }
            if ($difference / $reference.Length -lt 14) { return $true }
        }
        return $false
    } finally {
        $graphics.Dispose()
        $sample.Dispose()
    }
}

$spaceTimeArtwork = [Convert]::FromBase64String('DiuDgWsfGlh5f2ZYIFJ2gEQPRIiioWp4NUgtMBgdNm2umV9LKE8wG1hSJDNzgW4me3QmNn5vISh+i4AsjS8aJkczICBXeFwcg00ZHxkcJjBHXidEnW5DSHtjLlCRkWVuVFSJiI2YTEWdp8eUNmCuipCNeXJ3m7aPJl/JqGpQU2k+LUZcN0hvYDsrKy8uUX1Y')

function Test-GoldBannerTile($frame) {
    $gold = 0
    for ($y = 0; $y -lt 5; $y++) {
        for ($x = 0; $x -lt 5; $x++) {
            $p = $frame.GetPixel([int]((0.480 + $x * 0.010) * $frame.Width),
                [int]((0.198 + $y * 0.009) * $frame.Height))
            if ($p.R -gt 90 -and $p.R -ge $p.G + 15 -and $p.G -ge $p.B + 28) { $gold++ }
        }
    }
    return $gold -ge 22
}

function Read-ChallengeBanner($frame) {
    $heading = (Read-Hud $frame @(0.350, 0.250, 0.300, 0.047)) -join ' '
    if ($heading -notmatch 'CHALLENGE\s+COMPLETED') {
        $heading = (Read-Hud $frame @(0.366, 0.250, 0.269, 0.047) -Invert) -join ' '
    }
    if ($ChallengeBanner -and $ShowOcr) { Write-Status "Challenge header OCR: $heading" }
    $hasHeader = ($heading -replace '[^a-z0-9]', '') -match 'CHALLENGE(?:C[O0])?MPLETED'
    if ($hasHeader -and (Test-ChallengeArtwork $frame $spaceTimeArtwork)) { return 'menu-space-time' }
    $partialHeader = $heading -match '\bCOMPLETED\b'
    $hasGoldTile = Test-GoldBannerTile $frame
    $hasAutumnArtwork = $false
    if (!$hasHeader) {
        $hasAutumnArtwork = Test-ChallengeArtwork $frame $autumnArtwork
        if (!$hasAutumnArtwork -and !$partialHeader -and !$hasGoldTile) { return }
        if ($ShowOcr) { Write-Status 'Autumn artwork matched | checking ADS objective' }
    }
    foreach ($style in @('normal', 'tight', 'contrast', 'invert')) {
        $detailArea = if ($style -eq 'normal') { @(0.382, 0.295, 0.237, 0.034) } else { @(0.382, 0.301, 0.237, 0.022) }
        $detail = (Read-Hud $frame $detailArea -Contrast:($style -eq 'contrast') -Invert:($style -eq 'invert')) -join ' '
        if ($ShowOcr -and $detail -ne $script:lastChallengeOcr) {
            Write-Status "Challenge OCR ($style): $detail"
            $script:lastChallengeOcr = $detail
        }
        $key = $detail -replace '[^a-z0-9]', ''
        if ($key -match '25PLAYERKILLSWHILEAIMINGDOWNSIGHTS?') { return 'ads-25' }
        if ($hasAutumnArtwork -and $key -match '^GET25PLAYER') { return 'ads-25' }
        if ($hasHeader -and $key -match '3PLAYERKILLSWITHOUTDYING5TIMES') { return 'streak-3x5' }
        if ($hasHeader -and $key -match '50PLAYERKILLSWHILEUSINGAKIMBO') { return 'menu-space-time' }
        if (($hasHeader -or $partialHeader) -and $key -match '15PLAYERKILLSINASINGLEMATCH') { return 'gold-15' }
        if ($hasGoldTile -and $key -match '^GET15PLAYER(?:KILLS)?(?:IN)?ASINGLEMATCH$') { return 'gold-15' }
    }
    if ($hasHeader -and (Test-ChallengeArtwork $frame $streakArtwork)) { return 'streak-3x5' }
}

function Resolve-ChallengeWeapon($current, $recent, $pin) {
    if ($current) { return $current }
    if ($recent) { return $recent }
    return $pin
}

if ($ChallengeBanner) {
    $frame = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Image).Path)
    try { $challenge = Read-ChallengeBanner $frame } finally { $frame.Dispose() }
    if ($challenge) { Write-Output $challenge } else { Write-Host 'No supported completion banner found.' }
    return
}

function ConvertTo-FeedName([string]$text) {
    return ($text.ToLowerInvariant() -replace '[^a-z0-9]', '')
}

if ($ClanTag.Trim() -and $Player.Trim()) {
    $Player = "[$($ClanTag.Trim().Trim('[', ']'))]$($Player -replace '^\[[^\]]+\]', '')"
}
$playerText = ConvertTo-FeedName ($Player -replace '^\[[^\]]+\]', '')

$feedArea = @(0.013, 0.465, 0.24, 0.197)
function Read-CombatBanners($frame, $state, [DateTimeOffset]$now) {
    $killText = (Read-Hud $frame @(0.599, 0.427, 0.037, 0.030)) -join ' '
    $deathText = (Read-Hud $frame @(0.378, 0.798, 0.076, 0.033)) -join ' '
    $killCard = (Read-Hud $frame @(0.467, 0.934, 0.078, 0.033)) -join ' '
    $ordinal = if ($killCard -match '\b([0-9]{1,3})(?:ST|ND|RD|TH)\s+K[I1L]LL\b') { [int]$Matches[1] }
    $killBadge = $killText -match '^\s*[^a-z0-9]*K[I1L]LL\s*[^a-z0-9]*$'
    $deathBadge = ($deathText -replace '[^a-z0-9]', '') -match 'K[I1L]LLEDBY'
    if ($ShowOcr -and ($killBadge -or $deathBadge -or $ordinal)) {
        Write-Status "Combat banners OCR | kill '$killText' | card '$killCard' | death '$deathText'"
    }
    foreach ($kind in @('kill', 'death')) {
        $seen = if ($kind -eq 'kill') { $killBadge -or $ordinal } else { $deathBadge }
        $gap = if ($kind -eq 'kill') { 0.8 } else { 2.0 }
        $previous = $state[$kind]
        if (!$seen) {
            if ($previous -and ($now - $previous).TotalSeconds -ge $gap) {
                $state.Remove($kind)
                if ($kind -eq 'kill') { $state.Remove('ordinal') }
            }
            continue
        }
        $state[$kind] = $now
        $newOrdinal = $kind -eq 'kill' -and $ordinal -and $state.ordinal -and $ordinal -ne $state.ordinal
        if ($kind -eq 'kill' -and $ordinal) { $state.ordinal = $ordinal }
        if ($previous -and !$newOrdinal) { continue }
        $area = if ($kind -eq 'death') { @(0.381, 0.829, 0.151, 0.028) }
            else { @(0.424, 0.842, 0.150, 0.026) }
        $other = ((Read-Hud $frame $area) -join ' ') -replace '\s*[#*]\s*\d+.*$', ''
        if ($other.Trim().Length -lt 3) {
            $other = ((Read-Hud $frame $area -FeedColours) -join ' ') -replace '#\s*\d+.*$', ''
        }
        if ($other.Trim().Length -lt 3) { $other = 'unknown' }
        [pscustomobject]@{ Kind = $kind; Opponent = $other.Trim() }
    }
}

function Read-MilitaryUnlock($frame, $area, [switch]$Contrast, $lines = $null) {
    if ($null -eq $lines) { $lines = @(Read-Hud $frame $area -Positioned -Contrast:$Contrast | Sort-Object Y) }
    $text = $lines.Text -join ' | '
    $maxGap = if ($UnlockBadge -and $area[3] -gt 0.9) { 0.40 } else { 0.10 }
    if ($ShowOcr -and ($UnlockBadge -or
        ($text -match 'UNL|MILIT|CAM[O0]|SANG' -and $script:lastUnlockOcr -ne $text))) {
        $pass = if ($Contrast) { 'boosted contrast' } elseif ($area[3] -gt 0.9) { 'whole image' } else { 'right strip' }
        $readout = if ($text) { $text } else { '(no text)' }
        Write-Status "Unlock badge OCR ($pass): $readout"
        $script:lastUnlockOcr = $text
    }
    foreach ($line in $lines) {
        $header = $line.Text -replace '[^a-z0-9]', ''
        if ($header -notmatch 'UN[L1I][O0]CKED') { continue }
        $script:unlockHeaderSeen = $true
        # Read below the header in screen order. OCR can separate the
        # right-aligned header into a different text block from the title.
        $nearby = ($lines | Where-Object {
            $_.Y -ge $line.Y -and ($_.Y - $line.Y) * $area[3] -le $maxGap
        } | ForEach-Object Text) -join ' '
        $compact = $nearby -replace '[^a-z0-9]', ''
        if ($compact -match 'UN[L1I][O0]CKED.*?SANG9MM.*?M[I1L][L1I][I1L]TARYCAM[O0]([1-8SG])(?![0-9])') {
            switch ($Matches[1]) {
                'S' { return 5 }
                'G' { return 6 }
                default { return [int]$Matches[1] }
            }
        }
        if ($area[3] -gt 0.5) { continue }
        $top = $area[1] + $line.Y * $area[3]
        $weaponArea = @(0.829, ($top + 0.018), 0.065, 0.028)
        $titleArea = @(0.829, ($top + 0.043), 0.147, 0.041)
        $weaponRead = $false
        foreach ($style in @('normal', 'contrast', 'invert')) {
            $label = (Read-Hud $frame $weaponArea -Contrast:($style -eq 'contrast') -Invert:($style -eq 'invert')) -join ' '
            if ($ShowOcr) { Write-Status "Badge weapon ($style): '$label'" }
            if (($label -replace '[^a-z0-9]', '') -match '^SANG(?:9(?:MM|WLM))?$') {
                $weaponRead = $true
                break
            }
        }
        if (!$weaponRead) { continue }
        # Match only title variants seen in this badge's OCR, after confirming
        # both the unlock header and the Sang weapon label.
        $tierPattern = '(?:M[I1L][L1I][I1L]TARY|L?[I1]TA(?:R|I)?[FY]?|1ARY)(?:C[AV]M[O0]|CVA[M][O0]|CMQ[O0]|AM[O0])([1-8])(?![0-9])'
        if ($compact -match $tierPattern) { return [int]$Matches[1] }
        foreach ($style in @('normal', 'contrast', 'invert')) {
            $title = (Read-Hud $frame $titleArea -Contrast:($style -eq 'contrast') -Invert:($style -eq 'invert')) -join ' '
            if ($ShowOcr) { Write-Status "Badge title ($style): '$title'" }
            if (($title -replace '[^a-z0-9]', '') -match $tierPattern) { return [int]$Matches[1] }
        }
    }
}

$unlockArea = @(0.827, 0.045, 0.155, 0.405)
function Read-StackUnlock($frame, [switch]$ContrastFirst, [switch]$Named) {
    $script:unlockHeaderSeen = $false
    $lines = @(Read-Hud $frame $unlockArea -Positioned -Contrast:$ContrastFirst | Sort-Object Y)
    if ($Named) {
        $badge = Read-NamedUnlock $frame $lines
        if ($badge) { return $badge }
    }
    $tier = Read-MilitaryUnlock $frame $unlockArea -Contrast:$ContrastFirst -lines $lines
    if (!$tier -and ($script:unlockHeaderSeen -or $UnlockBadge)) {
        $lines = @(Read-Hud $frame $unlockArea -Positioned -Contrast:(!$ContrastFirst) | Sort-Object Y)
        if ($Named) {
            $badge = Read-NamedUnlock $frame $lines
            if ($badge) { return $badge }
        }
        $tier = Read-MilitaryUnlock $frame $unlockArea -Contrast:(!$ContrastFirst) -lines $lines
    }
    if ($Named -and $tier) { return @{ kind = 'military-unlock'; weapon = 'Sang 9mm'; tier = $tier } }
    return $tier
}

function Read-NamedUnlock($frame, $lines) {
    foreach ($line in $lines) {
        if (($line.Text -replace '[^a-z0-9]', '') -notmatch '^I?UN[L1I][O0]C[I1]?KED$') { continue }
        $script:unlockHeaderSeen = $true
        $top = $unlockArea[1] + $line.Y * $unlockArea[3]
        $nearby = @($lines | Where-Object { $_.Y -gt $line.Y -and ($_.Y - $line.Y) * $unlockArea[3] -le 0.095 })
        for ($i = 0; $i -lt $nearby.Count - 1; $i++) {
            $weapon = $weaponsByLabel[($nearby[$i].Text -replace '[^a-z0-9]', '')]
            if (!$weapon) { continue }
            $title = ($nearby[($i+1)..($nearby.Count-1)].Text -join ' ') -replace '[^a-z0-9]', ''
            if ($title -match '^MILITARYCAM[O0]([1-8])(?:$|[^0-9])') {
                return @{ kind = 'military-unlock'; weapon = $weapon; tier = [int]$Matches[1] }
            }
            $top = $unlockArea[1] + $line.Y * $unlockArea[3]
            $title = ((Read-Hud $frame @(0.831, ($top + 0.047), 0.145, 0.039) -BadgeTitle) -join ' ') -replace '[^a-z0-9]', ''
            if ($title -match '^MILITARYCAM[O0]([1-8])$') {
                return @{ kind = 'military-unlock'; weapon = $weapon; tier = [int]$Matches[1] }
            }
        }
        $title = ((Read-Hud $frame @(0.828, ($top + 0.044), 0.148, 0.039) -BadgeTitle) -join ' ') -replace '[^a-z0-9]', ''
        if ($title -notmatch '^MILITARYCAM[O0]([1-8])$') { continue }
        $tier = [int]$Matches[1]
        foreach ($contrast in @($true, $false)) {
            $label = ((Read-Hud $frame @(0.829, ($top + 0.021), 0.116, 0.029) -Contrast:$contrast) -join ' ') -replace '[^a-z0-9]', ''
            $weapon = $weaponsByLabel[$label]
            if (!$weapon -and $label -eq 'P68') { $weapon = $weaponsByLabel['kraitp68'] }
            if ($weapon) { return @{ kind = 'military-unlock'; weapon = $weapon; tier = $tier } }
        }
    }
}

$longshotObjectives = @{
    'Signal 50' = @{ Name = 'Sunburn'; Key = 'signal-50:sp-3' }
}

# Multiplayer Armoury roster. Add new tracker weapon names here as they release.
$weaponNames = @(
    'Kastov 762'
    'Finn LMG'
    'FSS Carbon Edge 1095'
    'Han 86'
    'ISO Nightshade'
    'KG-7 Vulcan'
    'Mar-9'
    'Pila'
    'Rezi 12'
    'Sang 9mm'
    'Vel 46'
    'Krait P68'
    'Type 73'
    'Oris 8.6'
    'M4'
    'WZ.55 Striga'
    'Hyeon Burst'
    'RPG-8'
    'Astaroth .57'
    'Patriot XMR'
    'PPSH-41'
    'Warlord'
    'Mosin-Nagant'
    'Oden'
    '.50 GS'
    'JOKR'
    'APOC-338'
    'X-58 Nyx'
    'Mojave .357'
    'Signal 50'
    'Axion'
    'PM-40 Anvil'
    'Cronen Squall'
    'Cronen Osprey'
    'CSG-35'
    'JAK 12'
    'ZLR Talonhawk'
)
$weaponsByLabel = @{}
foreach ($name in $weaponNames) { $weaponsByLabel[($name -replace '[^a-z0-9]', '')] = $name }
$weaponsByLabel['signal5o'] = 'Signal 50'
$weaponsByLabel['oris'] = 'Oris 8.6'
$weaponsByLabel['0ris86'] = 'Oris 8.6'
$weaponsByLabel['mosinnagan'] = 'Mosin-Nagant'
$weaponsByLabel['sang'] = 'Sang 9mm'
if ($UnlockBadge) {
    $badge = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Image).Path)
    try {
        $result = Read-StackUnlock $badge -Named
        if (!$result) {
            $tier = Read-MilitaryUnlock $badge @(0, 0, 1, 0.99)
            if ($tier) { $result = @{ kind = 'military-unlock'; weapon = 'Sang 9mm'; tier = $tier } }
        }
    } finally { $badge.Dispose() }
    if ($result) {
        $result.mode = 'mp'; $result.source = 'screenshot'
        $result | ConvertTo-Json -Compress
    } else { Write-Status 'No recognised Military unlock badge.' }
    return
}
$specialCatalog = $null
foreach ($path in @((Join-Path $PSScriptRoot 'special-catalog.json'),
    (Join-Path $PSScriptRoot 'camo-connector\special-catalog.json'))) {
    if (Test-Path -LiteralPath $path) {
        $specialCatalog = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        break
    }
}
if ($TrackedCamos) {
    if (!$Image -or $CamoMenu -or $UnlockBadge -or $ChallengeBanner) { throw '-TrackedCamos requires a screenshot and no other preview switches.' }
    $frame = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Image).Path)
    try { $progressRows = @(Read-TrackedCamoProgress $frame) } finally { $frame.Dispose() }
    if ($progressRows.Count) { $progressRows | ForEach-Object { $_ | ConvertTo-Json -Compress } }
    else { Write-Status 'No confirmed tracked progress rows.' }
    return
}

function Get-SpecialPadlockScores($frame) {
    if (!('CamoPadlockMatcher' -as [type])) {
        Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
public static class CamoPadlockMatcher {
    private static readonly byte[] Template = Convert.FromBase64String("fX2AVi+Af35+e3194fv5+frjIRd9wuOAMixf3GkWfcytTVAxILxxLX3Mz9XGyNDy5FN8zPz///////56fuz////////+ZoT+///5+P///ml8/v//x6n///5phf7//8Wn///+doT+//+0lf///oZJ/v//6+D///dsK/7+/v7///1uLSz7+/v7/PyfTRY=");
    public static double Score(Bitmap image, double tileX) {
        double best = 0;
        for (int dx = -6; dx <= 9; dx++) {
            for (int dy = 0; dy <= 12; dy++) {
                double x = tileX + dx * .00085;
                double y = .426 + dy * .0011;
                double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                for (int py = 0; py < 14; py++) {
                    for (int px = 0; px < 10; px++) {
                        var p = image.GetPixel((int)((x + (px + .5) * .0009) * image.Width),
                            (int)((y + (py + .5) * .019 / 14) * image.Height));
                        double value = Math.Min(p.R, Math.Min(p.G, p.B));
                        double target = Template[py * 10 + px];
                        sx += value; sy += target;
                        sxx += value * value; syy += target * target; sxy += value * target;
                    }
                }
                double variance = sxx - sx * sx / 140;
                if (variance < 140 * 24 * 24) continue;
                double score = (sxy - sx * sy / 140) / Math.Sqrt(variance * (syy - sy * sy / 140));
                best = Math.Max(best, score);
            }
        }
        return best;
    }
}
'@
    }
    foreach ($x in @(0.075, 0.150, 0.225)) { [CamoPadlockMatcher]::Score($frame, $x) }
}

function Test-SangSpaceTimeLock($frame) {
    $reference = [Convert]::FromBase64String('X2qdgmRzYRBCaX8zKkWAMBNBnqaqp65ZGHOupaCpr1Igi6+NTKWvVBiAtJZRpbFTFYa2raqwiVIYX314e3FdSA==')
    $sample = [Drawing.Bitmap]::new(8, 8)
    $graphics = [Drawing.Graphics]::FromImage($sample)
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $source = [Drawing.Rectangle]::new([int]($frame.Width * 0.228), [int]($frame.Height * 0.434),
            [int]($frame.Width * 0.010), [int]($frame.Height * 0.018))
        $graphics.DrawImage($frame, [Drawing.Rectangle]::new(0, 0, 8, 8), $source, [Drawing.GraphicsUnit]::Pixel)
        $difference = 0
        for ($y = 0; $y -lt 8; $y++) {
            for ($x = 0; $x -lt 8; $x++) {
                $pixel = $sample.GetPixel($x, $y)
                $difference += [Math]::Abs(($pixel.R + $pixel.G + $pixel.B) / 3 - $reference[$y * 8 + $x])
            }
        }
        return ($difference / $reference.Length -lt 11)
    } finally {
        $graphics.Dispose()
        $sample.Dispose()
    }
}

function Read-CamoMenu($frame, [switch]$HoverOnly, [switch]$SummaryOnly) {
    $heading = ((Read-Hud $frame @(0.070, 0.043, 0.182, 0.045)) -join ' ') -replace '[^a-z0-9]', ''
    if ($CamoMenu -and $ShowOcr) { Write-Status "Camo heading OCR: $heading" }
    if ($heading -notmatch 'CAMOSELECT|^SELECT$') { return }
    $tab = ((Read-Hud $frame @(0.082, 0.106, 0.177, 0.027)) -join ' ') -replace '[^a-z0-9]', ''
    if ($CamoMenu -and $ShowOcr) { Write-Status "Progression tab OCR: $tab" }
    if ($tab -notmatch '^(.+?)PROGRESSION') { return }
    $gun = $weaponsByLabel[$Matches[1]]
    if (!$gun -and $Matches[1] -match '^(?:KRAIT|KARIT|CRATE)P[G6][8B]$') { $gun = 'Krait P68' }
    if (!$gun) { return }
    $hover = $null
    $militaryHover = $null
    $title = $null
    if (!$SummaryOnly) {
        $title = ((Read-Hud $frame @(0.344, 0.614, 0.440, 0.064) -Contrast) -join ' ').Trim()
        $militaryHover = if ($title -match '^MILITARY\s+CAM[O0]\s+([1-8])$') {
            $tier = [int]$Matches[1]
            $counts = (Read-Hud $frame @(0.070, 0.153, 0.092, 0.039)) -join ' '
            if ($counts -match 'MILITARY\s*\[?\s*([0-8])\s*/\s*8') {
                [ordered]@{ tier = $tier; unlocked = ($tier -le [int]$Matches[1]) }
            }
        }
        if ($title -match '^[a-z0-9][a-z0-9 .-]{1,65}$' -and $title -notmatch '^MILITARY CAMO\b') {
            $detailArea = @(0.345, 0.709, 0.310, 0.151)
            $detail = @(Read-Hud $frame $detailArea -Contrast -Positioned | Sort-Object Y)
            if ($ShowOcr -and $CamoMenu) { Write-Status "Hover OCR: $title | $($detail.Text -join ' | ')" }
            for ($i = 0; $i -lt $detail.Count; $i++) {
                $targetText = $detail[$i].Text
                if ($targetText -notmatch '\b(\d{1,4})\s*/\s*(\d{1,4})\b') { continue }
                $value = [int]$Matches[1]; $goal = [int]$Matches[2]
                if ($value -gt $goal -or !$goal) { continue }
                $objective = ($targetText -replace '\b\d{1,4}\s*/\s*\d{1,4}\b.*$', '').Trim()
                if (!$objective -and $i -gt 0 -and ($detail[$i].Y - $detail[$i-1].Y) * $detailArea[3] -le 0.044) {
                    $objective = $detail[$i-1].Text.Trim()
                }
                if ($objective -match 'weapon unlock|weapon level|camo challenges|complete .* camos?') { continue }
                $camoName = $title
                $entry = @($specialCatalog.PSObject.Properties[$gun].Value | Where-Object {
                    $_.name -and ($_.name -replace '[^a-z0-9]', '') -eq ($title -replace '[^a-z0-9]', '')
                })
                if ($entry.Count -eq 1) {
                    if ($entry[0].goal -and $entry[0].goal -ne $goal) { continue }
                    $camoName = $entry[0].name
                }
                if (!$objective) { $objective = $camoName }
                $key = if ($gun -eq 'Signal 50' -and $title -eq 'Sunburn' -and $goal -eq 40) { 'signal-50:sp-3' }
                $hover = [ordered]@{ weapon = $gun; camo = $camoName; objective = $objective
                    count = $value; goal = $goal; type = 'camo'; key = $key; identity = 'name' }
                break
            }
        }
    }
    if ($HoverOnly) {
        if ($hover -or $militaryHover) {
            return [ordered]@{ weapon = $gun; hover = $hover; militaryHover = $militaryHover }
        }
        return
    }
    $militaryText = (Read-Hud $frame @(0.070, 0.153, 0.092, 0.039)) -join ' '
    $specialText = (Read-Hud $frame @(0.070, 0.389, 0.090, 0.039)) -join ' '
    if ($CamoMenu -and $ShowOcr) { Write-Status "Camo menu OCR: $tab | $militaryText | $specialText" }
    $military = if ($militaryText -match 'MILITARY\s*\[?\s*([0-8])\s*/\s*8') { [int]$Matches[1] }
    $special = if ($specialText -match 'SPECIAL\s*(?:\[|1)?\s*([0-3])\s*/\s*3(?:\]|1)?(?!\d)') { [int]$Matches[1] }
    if ($null -eq $military -or $null -eq $special) { return }
    $completionText = (Read-Hud $frame @(0.070, 0.550, 0.110, 0.040)) -join ' '
    $prestigeText = (Read-Hud $frame @(0.070, 0.693, 0.100, 0.037)) -join ' '
    $completionist = if ($completionText -match 'COMPLETIONIST\s*\[?\s*([0-4])\s*/\s*4') { [int]$Matches[1] }
    $prestige = if ($prestigeText -match 'PRESTIGE\s*\[?\s*([0-4])\s*/\s*4') { [int]$Matches[1] }
    $confirmed = @()
    $unlockedSlots = @()
    $slotStates = @()
    $catalogCamos = $null
    if ($specialCatalog -and $specialCatalog.PSObject.Properties[$gun]) {
        $catalogCamos = $specialCatalog.PSObject.Properties[$gun].Value
    }
    if ($special -eq 3) {
        $unlockedSlots = @(1, 2, 3)
        $slotStates = @('unlocked','unlocked','unlocked')
    } elseif ($special -eq 0) {
        $slotStates = @('locked','locked','locked')
    } else {
        $scores = @(Get-SpecialPadlockScores $frame)
        if ($ShowOcr -and $CamoMenu) {
            Write-Status ('Special padlock scores: ' + (($scores | ForEach-Object { '{0:F2}' -f $_ }) -join ', '))
        }
        $locks = @(for ($slot = 0; $slot -lt 3; $slot++) {
            if ($scores[$slot] -ge 0.70) { $slot + 1 }
        })
        $uncertain = @($scores | Where-Object { $_ -ge 0.55 -and $_ -lt 0.70 })
        if ($locks.Count -eq 3 - $special -and !$uncertain.Count) {
            $unlockedSlots = @(1..3 | Where-Object { $_ -notin $locks })
            $slotStates = @(1..3 | ForEach-Object { if ($_ -in $locks) { 'locked' } else { 'unlocked' } })
        }
    }
    if ($gun -ne 'Sang 9mm' -and $unlockedSlots.Count -and @($catalogCamos).Count -eq 3) {
        $confirmed = @($catalogCamos | Where-Object { $_.name -and $_.goal -and $_.slot -in $unlockedSlots } | ForEach-Object { $_.name })
    }
    if ($gun -eq 'Sang 9mm') {
        if (!$unlockedSlots.Count -and $special -eq 2 -and (Test-SangSpaceTimeLock $frame)) {
            $unlockedSlots = @(1, 2)
            $slotStates = @('unlocked','unlocked','locked')
        }
        $names = @('menu-autumn', 'menu-snakeskin', 'menu-space-time')
        foreach ($slot in $unlockedSlots) {
            $confirmed += $names[$slot - 1]
        }
    }
    if (!$hover -and $title -and $catalogCamos) {
        $selectedCamo = @($catalogCamos | Where-Object {
            ($_.name -replace '[^a-z0-9]', '') -eq ($title -replace '[^a-z0-9]', '') -and
            $_.slot -in $unlockedSlots
        })
        if ($selectedCamo.Count -eq 1 -and $selectedCamo[0].name -notin $confirmed) {
            $confirmed += $selectedCamo[0].name
        }
    }
    return [ordered]@{ weapon = $gun; military = $military; special = $special
        completionist = $completionist; prestige = $prestige; specialSlots = $slotStates
        confirmed = $confirmed; hover = $hover; militaryHover = $militaryHover }
}

if ($CamoMenu) {
    $frame = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Image).Path)
    try { $menu = Read-CamoMenu $frame } finally { $frame.Dispose() }
    if ($menu) { $menu | ConvertTo-Json -Depth 5 -Compress } else { Write-Status 'No recognised weapon progression menu.' }
    return
}
if ($PinnedWeapon) {
    $PinnedWeapon = $weaponsByLabel[($PinnedWeapon -replace '[^a-z0-9]', '')]
    if (!$PinnedWeapon) { throw '-PinnedWeapon must name a weapon in the Armoury roster.' }
}

function Read-Weapon($frame, [ref]$akimbo) {
    if ($akimbo) { $akimbo.Value = $false }
    $reads = @()
    foreach ($area in @(
        @(0.705, 0.864, 0.205, 0.053),
        @(0.727, 0.875, 0.098, 0.031),
        @(0.747, 0.869, 0.077, 0.044)
    )) {
        $lines = @(Read-Hud $frame $area)
        $reads += $lines
        foreach ($label in @($lines -join ' ') + $lines) {
            $key = $label -replace '[^a-z0-9]', ''
            if ($key -match '^sang9mmakimb[o0]$') {
                if ($akimbo) { $akimbo.Value = $true }
                return 'Sang 9mm'
            }
            if ($area[2] -lt 0.15) {
                if ($weaponsByLabel.ContainsKey($key)) { return $weaponsByLabel[$key] }
                if ($key -match '^(?:k[g6]7?)?vu[lI1]can$') { return 'KG-7 Vulcan' }
            }
        }
    }
    $rejected = $reads -join ' | '
    if ($ShowOcr -and $rejected -and $rejected -ne $script:lastRejectedWeapon) {
        Write-Status "Ignored weapon OCR: '$rejected' (unlisted)"
        $script:lastRejectedWeapon = $rejected
    }
}

function Test-AkimboKill($weapon, [bool]$akimbo, [DateTimeOffset]$lastAkimbo, [DateTimeOffset]$now) {
    if ($weapon) { return ($weapon -eq 'Sang 9mm' -and $akimbo) }
    return ($lastAkimbo -le $now -and ($now - $lastAkimbo).TotalMilliseconds -le 1500)
}

function Test-ReplayHud($frame) {
    foreach ($area in @(
        @(0.30, 0.027, 0.40, 0.12),
        @(0.29, 0.73, 0.42, 0.15)
    )) {
        $notice = (Read-Hud $frame $area) -join ' '
        if ($notice -match 'KILL\s*CAM|FINAL\s+KILL|BEST\s+PLAY|SPECTAT(?:ING|OR)|SKIP\s+(?:THE\s+)?KILL') {
            return $true
        }
    }
    return $false
}

function ConvertFrom-MatchLobbyText([string]$text) {
    if ($text -match '\bMATCH[\s.:|]+(?:IS[\s.:|]+)?ABOUT[\s.:|]+TO[\s.:|]+BEG[I1L]N\b') {
        return @{ Kind = 'match-confirmed'; Mode = $null }
    }
    if ($text -match '\bMAP\s+VOTING\b' -and $text -match '\bTIME\s+REMAINING\b') {
        return @{ Kind = 'match-voting' }
    }
}

function Read-LobbyCardMode($lines) {
    # The mode is the smaller line immediately below the larger, left-aligned map name.
    $mapLines = @($lines | Where-Object {
        $_.X -lt 0.55 -and $_.Y -gt 0.25 -and $_.Height -ge 0.018 -and $_.Text -match '\p{L}'
    } | Sort-Object Height -Descending)
    foreach ($map in $mapLines) {
        $mode = $lines | Where-Object {
            $gap = $_.Y - $map.Y
            [Math]::Abs($_.X - $map.X) -lt 0.025 -and
                $gap -ge ($map.Height * 0.8) -and $gap -le ($map.Height * 2) -and
                $_.Height -ge ($map.Height * 0.35) -and $_.Height -le ($map.Height * 0.86) -and
                $_.Text -match '\p{L}'
        } | Sort-Object Y | Select-Object -First 1
        if ($mode) { return $mode.Text.Trim() }
    }
}

function Read-MatchLobby($frame, [switch]$Retry) {
    $banner = (Read-Hud $frame @(0, 0, 0.99, 0.42)) -join ' '
    $result = ConvertFrom-MatchLobbyText $banner
    if (!$result -and $Retry) {
        $banner = (Read-Hud $frame @(0, 0, 0.99, 0.65) -Contrast) -join ' '
        $result = ConvertFrom-MatchLobbyText $banner
    }
    if ($result.Kind -eq 'match-confirmed') {
        $area = @(0, 0.18, 0.95, 0.79)
        $card = @(Read-Hud $frame $area -Positioned)
        $result.Mode = Read-LobbyCardMode $card
        if (!$result.Mode) {
            $card = @(Read-Hud $frame $area -Positioned -Contrast)
            $result.Mode = Read-LobbyCardMode $card
        }
    }
    if ($ShowOcr -and ($Retry -or $result)) { Write-Status "Lobby OCR | $banner | $($card.Text -join ' | ')" }
    return $result
}

function Read-MatchMode($frame) {
    $hud = (Read-Hud $frame @(0.015, 0.228, 0.132, 0.062)) -join ' '
    if ($hud -match 'HARD\s*POINT|DOMINATION|TEAM\s+DEATHMATCH|KILL\s+CONFIRMED|SEARCH\s+(?:AND|&)\s+DESTROY|FREE[ -]+FOR[ -]+ALL|GUN\s+GAME|GUNFIGHT|CONTROL|HEADQUARTERS') {
        return ($Matches[0].ToUpperInvariant() -replace '^HARD\s*POINT$', 'HARDPOINT' -replace '\s+', ' ')
    }
}

function Test-LongshotMedal($frame) {
    $medals = (Read-Hud $frame @(0.578, 0.395, 0.139, 0.175)) -join ' '
    if ($ShowOcr -and $medals -ne $script:lastMedalText) {
        Write-Host "Medal OCR: $medals"
        $script:lastMedalText = $medals
    }
    return $medals -match '\bL[O0]NG[\s-]*SH[O0]T\b'
}

function Copy-LiveFrame($graphics, $bounds) {
    if (!$CaptureFrame) {
        $graphics.CopyFromScreen($bounds.Location, [Drawing.Point]::Empty, $bounds.Size)
        return $true
    }
    $image = Open-CaptureFrame $CaptureFrame
    if (!$image) { return $false }
    try {
        if ($image.Size -ne $bounds.Size) { throw 'Capture resolution changed. Restart the detector.' }
        $graphics.DrawImageUnscaled($image, 0, 0)
        return $true
    } finally { $image.Dispose() }
}

if ($Live) {
    if ($CaptureFrame) {
        . (Join-Path $PSScriptRoot 'capture-frame.ps1')
        $CaptureFrame = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CaptureFrame)
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $initialFrame = Open-CaptureFrame $CaptureFrame
            if (!$initialFrame) { Start-Sleep -Milliseconds 100 }
        } until ($initialFrame -or [DateTime]::UtcNow -ge $deadline)
        if (!$initialFrame) { throw 'No recent frame from the capture device.' }
        try { $bounds = [Drawing.Rectangle]::new([Drawing.Point]::Empty, $initialFrame.Size) }
        finally { $initialFrame.Dispose() }
        $sourceLabel = 'capture device'
    } else {
        if ($Monitor -eq -1) { $screen = [Windows.Forms.Screen]::PrimaryScreen }
        elseif ($Monitor -ge 0 -and $Monitor -lt [Windows.Forms.Screen]::AllScreens.Count) {
            $screen = [Windows.Forms.Screen]::AllScreens[$Monitor]
        } else { throw 'Monitor index is out of range.' }
        $bounds = $screen.Bounds
        $sourceLabel = $screen.DeviceName
    }
    if (!$CombatWorker) {
        Write-Status "Watching $sourceLabel ($($bounds.Width)x$($bounds.Height)). Ctrl+C stops."
        Write-Status 'Waiting for multiplayer HUD. Detector logs events locally.'
        if ($PinnedWeapon) { Write-Status "Pinned weapon for completion banners: $PinnedWeapon" }
        if ($playerText) { Write-Status "Combat banners enabled | Player: $Player" }
    }
} else {
    $frame = [Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Image).Path)
}

if ($CombatWorker.MilitaryOnly) {
    $frame = [Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [Drawing.Graphics]::FromImage($frame)
    $seen = @{}
    $traceCount = 0
    $contrast = $false
    try {
        while (!$CombatWorker.Stop) {
            $lap = [Diagnostics.Stopwatch]::StartNew()
            if (!(Copy-LiveFrame $graphics $bounds)) { Start-Sleep -Milliseconds 100; continue }
            $captured = [DateTimeOffset]::UtcNow
            $unlock = Read-StackUnlock $frame -Named -ContrastFirst:$contrast
            $contrast = !$contrast
            if ($unlock) {
                $key = "$($unlock.weapon)|$($unlock.tier)"
                if (!$seen.ContainsKey($key)) {
                    $seen[$key] = $true
                    $unlock.source = 'live'
                    $unlock.mode = 'mp'
                    $unlock.at = $captured.ToString('o')
                    $CombatWorker.Events.Enqueue($unlock)
                }
            }
            if ($CombatWorker.Trace -and ($unlock -or $script:unlockHeaderSeen) -and $traceCount -lt 60) {
                $traceCount++
                $sample = [Drawing.Rectangle]::new([int]($frame.Width * 0.78), [int]($frame.Height * 0.045),
                    [int]($frame.Width * 0.208), [int]($frame.Height * 0.405))
                $crop = $frame.Clone($sample, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
                try {
                    $stamp = $captured.ToString('HHmmss-fff')
                    $crop.Save((Join-Path $CombatWorker.Trace "military-$stamp.png"), [Drawing.Imaging.ImageFormat]::Png)
                } finally { $crop.Dispose() }
            }
            $remaining = 150 - $lap.ElapsedMilliseconds
            if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
        }
    } finally {
        $graphics.Dispose()
        $frame.Dispose()
    }
    return
}

if ($CombatWorker) {
    $bannerState = @{}
    $generation = $CombatWorker.Generation
    $traceIndex = 0
    $scan = 0
    $lastTraceAt = [DateTimeOffset]::MinValue
    $frame = [Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [Drawing.Graphics]::FromImage($frame)
    try {
        while (!$CombatWorker.Stop) {
            $lap = [Diagnostics.Stopwatch]::StartNew()
            if (!(Copy-LiveFrame $graphics $bounds)) { Start-Sleep -Milliseconds 100; continue }
            $now = [DateTimeOffset]::UtcNow
            $currentGeneration = $CombatWorker.Generation
            if ($currentGeneration -ne $generation) {
                $bannerState.Clear()
                $generation = $currentGeneration
            }
            $scan++
            $feedRows = @(Read-CombatBanners $frame $bannerState $now)
            foreach ($row in $feedRows) {
                $weapon = $null
                $akimbo = $false
                $replay = $false
                if ($row.Kind -eq 'kill') {
                    $weapon = Read-Weapon $frame ([ref]$akimbo)
                    $replay = ($akimbo -or !$weapon) -and (Test-ReplayHud $frame)
                }
                $CombatWorker.Events.Enqueue([pscustomobject]@{
                    Kind = $row.Kind; Opponent = $row.Opponent; At = $now
                    DetectedAt = [DateTimeOffset]::UtcNow; Weapon = $weapon
                    Akimbo = $akimbo; Replay = $replay; Generation = $generation
                })
            }
            if ($CombatWorker.Trace -and $traceIndex -lt 240 -and
                ($feedRows.Count -or ($now - $lastTraceAt).TotalMilliseconds -ge 750)) {
                $lastTraceAt = $now
                $traceIndex++
                $sample = [Drawing.Rectangle]::new(
                    [int]($frame.Width * $feedArea[0]), [int]($frame.Height * $feedArea[1]),
                    [int]($frame.Width * $feedArea[2]), [int]($frame.Height * $feedArea[3]))
                $crop = $frame.Clone($sample, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
                try {
                    $crop.Save((Join-Path $CombatWorker.Trace ('feed-{0:D3}.png' -f $traceIndex)), [Drawing.Imaging.ImageFormat]::Png)
                } finally { $crop.Dispose() }
                foreach ($region in @(
                    @{ Name = 'kill'; Bounds = @(0.595, 0.423, 0.045, 0.038) },
                    @{ Name = 'weapon'; Bounds = @(0.705, 0.864, 0.205, 0.053) },
                    @{ Name = 'card'; Bounds = @(0.375, 0.795, 0.200, 0.174) }
                )) {
                    $a = $region.Bounds
                    $boundsForTrace = [Drawing.Rectangle]::new([int]($frame.Width * $a[0]),
                        [int]($frame.Height * $a[1]), [int]($frame.Width * $a[2]), [int]($frame.Height * $a[3]))
                    $traceBitmap = $frame.Clone($boundsForTrace, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
                    try {
                        $traceBitmap.Save((Join-Path $CombatWorker.Trace ('{0}-{1:D3}.png' -f $region.Name, $traceIndex)),
                            [Drawing.Imaging.ImageFormat]::Png)
                    } finally { $traceBitmap.Dispose() }
                }
                @{
                    sample = $traceIndex; scan = $scan; at = $now.ToString('o')
                    counted = $feedRows
                } | ConvertTo-Json -Depth 4 -Compress | Add-Content -LiteralPath (Join-Path $CombatWorker.Trace 'trace.jsonl') -Encoding UTF8
            }
            $remaining = 120 - $lap.ElapsedMilliseconds
            if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
        }
    } finally {
        $graphics.Dispose()
        $frame.Dispose()
    }
    return
}

$lastMedalSeen = $null
$medalAssigned = $false
$lastStatusRead = [DateTimeOffset]::MinValue
$lastMatchSeen = [DateTimeOffset]::MinValue
$matchMode = $null
$matchOpen = $false
$matchNumber = 0
$lobbyPhase = $null
$lobby = $null
$lastLobbyRead = [DateTimeOffset]::MinValue
$lastResultRead = [DateTimeOffset]::MinValue
$matchHudActive = $false
$lastWeaponStatus = $null
$publishedWeapon = $null
$lastChallengeRead = [DateTimeOffset]::MinValue
$lastConfirmedWeaponSeen = [DateTimeOffset]::MinValue
$lastAkimboSeen = [DateTimeOffset]::MinValue
$completedChallenges = @{}
$lastMenuRead = [DateTimeOffset]::MinValue
$lastHoverRead = [DateTimeOffset]::MinValue
$hoverCandidate = $null
$hoverCandidateAt = [DateTimeOffset]::MinValue
$lastPublishedHover = $null
$lastTrackedCamosRead = [DateTimeOffset]::MinValue
$recentTrackedCounts = @{}
$publishedTrackedCounts = @{}
$previousMenu = $null
$previousMilitaryMenu = $null
$publishedMilitaryMenu = @{}
$reportedMenus = @{}
$unassignedChallenges = @{}
$candidateWeapon = $null
$candidateLastSeen = $null
$sessionCount = 0
$kills = 0
$deaths = 0
$killStreak = 0
$bestStreak = 0
$combatBannerState = @{}
if ($FeedTrace) {
    if (!$playerText) { throw '-FeedTrace requires -Player.' }
    $traceRun = Join-Path $FeedTrace ('feed-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
    [IO.Directory]::CreateDirectory($traceRun) | Out-Null
    Write-Status "Saving up to 240 combat banner samples to $traceRun"
}
try {
if ($Live) {
    $combat = [hashtable]::Synchronized(@{
        Events = [Collections.Concurrent.ConcurrentQueue[object]]::new()
        Trace = $traceRun; Stop = $false; Generation = 0
    })
    $combatRunner = [PowerShell]::Create()
    [void]$combatRunner.AddCommand($PSCommandPath).AddParameter('Live').AddParameter('Monitor', $Monitor).AddParameter('CombatWorker', $combat)
    if ($CaptureFrame) { [void]$combatRunner.AddParameter('CaptureFrame', $CaptureFrame) }
    $combatRun = $combatRunner.BeginInvoke()
    Write-Status 'Combat capture running independently'
    $militaryCapture = [hashtable]::Synchronized(@{
        Events = [Collections.Concurrent.ConcurrentQueue[object]]::new()
        Trace = $traceRun; Stop = $false; MilitaryOnly = $true
    })
    $militaryRunner = [PowerShell]::Create()
    [void]$militaryRunner.AddCommand($PSCommandPath).AddParameter('Live').AddParameter('Monitor', $Monitor).AddParameter('CombatWorker', $militaryCapture)
    if ($CaptureFrame) { [void]$militaryRunner.AddParameter('CaptureFrame', $CaptureFrame) }
    $militaryRun = $militaryRunner.BeginInvoke()
    Write-Status 'Military badge capture running independently'
}
do {
    if ($Live) {
        $frame = [Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    }
    try {
        if ($Live) {
            $graphics = [Drawing.Graphics]::FromImage($frame)
            try { $frameReady = Copy-LiveFrame $graphics $bounds }
            finally { $graphics.Dispose() }
            if (!$frameReady) { Start-Sleep -Milliseconds 100; continue }
        }
        $now = [DateTimeOffset]::UtcNow
        if ($Image -and $LobbyScreen) {
            Read-MatchLobby $frame -Retry | ConvertTo-Json -Compress
            return
        }
        if ($Live -and ($now - $lastHoverRead).TotalMilliseconds -ge 400) {
            $lastHoverRead = $now
            $selection = Read-CamoMenu $frame -HoverOnly
            $hoverKey = if ($selection.hover) {
                $progress = $selection.hover
                $name = $progress.camo -replace '[^a-z0-9]', ''
                "$($progress.weapon)|$name|$($progress.count)/$($progress.goal)"
            } elseif ($selection.militaryHover) {
                "$($selection.weapon)|Military $($selection.militaryHover.tier)|$($selection.militaryHover.unlocked)"
            }
            if ($hoverKey) {
                if ($hoverKey -eq $hoverCandidate -and ($now - $hoverCandidateAt).TotalSeconds -le 4 -and
                    $hoverKey -ne $lastPublishedHover) {
                    if ($selection.hover) {
                        $progress = $selection.hover
                        $progress.at = $now.ToString('o'); $progress.kind = 'progress-snapshot'
                        $progress.mode = 'mp'; $progress.source = 'live'
                        Write-MatchEvent -Path $Log -Json ($progress | ConvertTo-Json -Compress)
                        Write-Status "Hovered camo | $($progress.weapon) | $($progress.camo) | $($progress.count)/$($progress.goal)"
                    } else {
                        $status = if ($selection.militaryHover.unlocked) { 'unlocked' } else { 'locked' }
                        Write-Status "Hovered camo | $($selection.weapon) | Military $($selection.militaryHover.tier) | $status"
                    }
                    $lastPublishedHover = $hoverKey
                }
                $hoverCandidate = $hoverKey
                $hoverCandidateAt = $now
            } elseif (($now - $hoverCandidateAt).TotalSeconds -gt 4) {
                $hoverCandidate = $lastPublishedHover = $null
            }
        }
        if ($Live) {
            if ($militaryRun.IsCompleted) {
                [void]$militaryRunner.EndInvoke($militaryRun)
                throw 'Military badge capture stopped.'
            }
            $unlock = $null
            while ($militaryCapture.Events.TryDequeue([ref]$unlock)) {
                Write-MatchEvent -Path $Log -Json ($unlock | ConvertTo-Json -Compress)
                Write-Status "Unlock detected | $($unlock.weapon) Military $($unlock.tier)"
            }
        }
        $lobbyInterval = if ($lobbyPhase -eq 'voting' -or ($lobbyPhase -eq 'confirmed' -and !$matchMode)) { 400 } else { 900 }
        if ($Live -and ($now - $lastLobbyRead).TotalMilliseconds -ge $lobbyInterval) {
            $lastLobbyRead = $now
            $lobby = Read-MatchLobby $frame -Retry:(!$matchOpen -or !$matchMode)
            if ($lobby.Kind -eq 'match-voting' -and $lobbyPhase -ne 'voting') {
                $lobbyPhase = 'voting'
                $matchNumber++
                $combat.Generation = $matchNumber
                $matchOpen = $matchHudActive = $false
                $matchMode = $null
                $kills = $deaths = $killStreak = $bestStreak = $sessionCount = 0
                $lastAkimboSeen = $lastConfirmedWeaponSeen = [DateTimeOffset]::MinValue
                $candidateWeapon = $publishedWeapon = $null
                $combatBannerState.Clear()
                Write-MatchEvent -Path $Log -Json (@{ kind = 'match-voting'; source = 'live'
                    at = $now.ToString('o'); match = $matchNumber; kills = 0; deaths = 0; bestStreak = 0
                } | ConvertTo-Json -Compress)
                Write-Status 'Map voting | Match counters reset'
            } elseif ($lobby.Kind -eq 'match-confirmed' -and
                ($lobbyPhase -ne 'confirmed' -or (!$matchMode -and $lobby.Mode))) {
                if ($lobbyPhase -notin @('voting', 'confirmed')) {
                    $matchNumber++
                    $combat.Generation = $matchNumber
                    $kills = $deaths = $killStreak = $bestStreak = $sessionCount = 0
                    $lastAkimboSeen = $lastConfirmedWeaponSeen = [DateTimeOffset]::MinValue
                    $candidateWeapon = $publishedWeapon = $null
                }
                $lobbyPhase = 'confirmed'
                $matchMode = $lobby.Mode
                $matchOpen = $true
                $lastMatchSeen = $now
                Write-MatchEvent -Path $Log -Json (@{ kind = 'match-confirmed'; source = 'live'
                    at = $now.ToString('o'); match = $matchNumber; matchMode = $matchMode
                } | ConvertTo-Json -Compress)
                $modeLabel = if ($matchMode) { $matchMode } else { 'Mode unreadable' }
                Write-Status "Match about to begin | $modeLabel"
            }
        }
        if ($Live -or $playerText) {
            $feedRows = @(if ($Live) {
                if ($combatRun.IsCompleted) { [void]$combatRunner.EndInvoke($combatRun); throw 'Combat capture stopped.' }
                $queued = $null
                while ($combat.Events.TryDequeue([ref]$queued)) { $queued }
            } else {
                Read-CombatBanners $frame $combatBannerState $now
            })
            foreach ($row in $feedRows) {
                $eventAt = if ($Live) { $row.At } else { $now }
                if ($Live -and ($row.Generation -ne $matchNumber -or $lobbyPhase -in @('voting', 'ended'))) { continue }
                if ($Live -and !$lobby) {
                    $lobbyPhase = $null
                    $matchOpen = $true
                    $lastMatchSeen = $eventAt
                }
                if ($row.Kind -eq 'death') {
                    $deathOnFrame = $true
                    $lastAkimboSeen = [DateTimeOffset]::MinValue
                }
                if ($Live -and $row.Kind -eq 'kill') {
                    $killFrameWeapon = $row.Weapon
                    $killFrameAkimbo = $row.Akimbo
                    $matchesAkimbo = Test-AkimboKill $killFrameWeapon $killFrameAkimbo $lastAkimboSeen $eventAt
                    $replay = $row.Replay
                    if ($matchesAkimbo -and !$replay) {
                        Write-MatchEvent -Path $Log -Json (@{
                            kind = 'akimbo-kill'; source = 'live'; mode = 'mp'
                            weapon = 'Sang 9mm'; at = $eventAt.ToString('o')
                            detectedAt = $row.DetectedAt.ToString('o'); writtenAt = [DateTimeOffset]::UtcNow.ToString('o')
                        } | ConvertTo-Json -Compress)
                        Write-Status 'Space Time +1 | Sang 9mm Akimbo'
                    } elseif ($ShowOcr -or $FeedTrace) {
                        $label = if ($killFrameWeapon) { $killFrameWeapon } else { 'unreadable' }
                        Write-Status "Space Time skipped | kill-frame weapon: $label | Akimbo: $killFrameAkimbo | replay: $replay"
                    }
                }
                $endedStreak = 0
                if ($row.Kind -eq 'kill') {
                    $kills++
                    $killStreak++
                    $bestStreak = [Math]::Max($bestStreak, $killStreak)
                } else {
                    $deaths++
                    $endedStreak = $killStreak
                    $killStreak = 0
                }
                $entry = [ordered]@{
                    at = $eventAt.ToString('o'); kind = $row.Kind; opponent = $row.Opponent
                    kills = $kills; deaths = $deaths
                    killStreak = $killStreak; bestStreak = $bestStreak; endedStreak = $endedStreak
                    source = $(if ($Live) { 'live' } else { 'screenshot' }); match = $matchNumber
                } | ConvertTo-Json -Compress
                if ($Live) {
                    Write-MatchEvent -Path $Log -Json $entry
                    $streakStatus = if ($endedStreak) { "Streak ended at $endedStreak" } else { "Streak: $killStreak" }
                    Write-Status "$($row.Kind) | $($row.Opponent) | $streakStatus | Best: $bestStreak | Session: $kills kills, $deaths deaths"
                } elseif ($ShowOcr) { Write-Output $entry }
            }
        }
        if ($Live -and $matchOpen -and ($now - $lastResultRead).TotalSeconds -ge 1) {
            $lastResultRead = $now
            $resultText = (Read-Hud $frame @(0.797, 0.042, 0.181, 0.127)) -join ' '
            if ($resultText -match '\b(VICTORY|DEFEAT|DRAW)\b') {
                $resultDetail = (Read-Hud $frame $unlockArea) -join ' '
            } else { $resultDetail = '' }
            if ($resultDetail -match 'LIMIT\s+REACHED|MATCH\s+ENDED') {
                Write-MatchEvent -Path $Log -Json (@{
                    kind = 'match-ended'; source = 'live'; at = $now.ToString('o'); match = $matchNumber
                    kills = $kills; deaths = $deaths; bestStreak = $bestStreak; matchMode = $matchMode
                } | ConvertTo-Json -Compress)
                Write-Status "Match ended | $kills kills, $deaths deaths | Best streak: $bestStreak"
                $kills = $deaths = $killStreak = $bestStreak = $sessionCount = 0
                $combatBannerState.Clear()
                $lobbyPhase = 'ended'
                $matchOpen = $false
                $matchHudActive = $false
                $matchMode = $null
                $lastAkimboSeen = $lastConfirmedWeaponSeen = [DateTimeOffset]::MinValue
                $candidateWeapon = $publishedWeapon = $null
            }
        }
            $killFrameWeapon = $null
        $killFrameAkimbo = $false
        $deathOnFrame = $false
        if ($Live -and ($now - $lastTrackedCamosRead).TotalSeconds -ge 0.75) {
            $lastTrackedCamosRead = $now
            # OCR can miss individual frames. Confirm with two matching reads within
            # five seconds, while replacing the candidate on a conflicting read.
            $confirmationCutoff = $now.AddSeconds(-5)
            foreach ($key in @($recentTrackedCounts.Keys)) {
                if ($recentTrackedCounts[$key].At -lt $confirmationCutoff) {
                    $recentTrackedCounts.Remove($key)
                }
            }
            foreach ($row in @(Read-TrackedCamoProgress $frame)) {
                $rowKey = "$($row.weapon)|$($row.camo)|$($row.goal)"
                $previous = $recentTrackedCounts[$rowKey]
                $recentTrackedCounts[$rowKey] = @{ Count = $row.count; At = $now }
                if ($previous -and $previous.At -lt $now -and $previous.Count -eq $row.count -and
                    $publishedTrackedCounts[$rowKey] -ne $row.count) {
                    $row.at = $now.ToString('o')
                    $row.kind = 'progress-snapshot'; $row.mode = 'mp'; $row.source = 'live'
                    Write-MatchEvent -Path $Log -Json ($row | ConvertTo-Json -Compress)
                    $publishedTrackedCounts[$rowKey] = $row.count
                    Write-Status "Tracked progress | $($row.weapon) | $($row.objective) | $($row.count)/$($row.goal)"
                }
            }
        }
        if ($Live -and ($now - $lastMenuRead).TotalSeconds -ge 1) {
            $lastMenuRead = $now
            $menu = Read-CamoMenu $frame -SummaryOnly
            $snapshot = if ($menu) { $menu | ConvertTo-Json -Depth 5 -Compress }
            $militaryMenu = if ($menu) { "$($menu.weapon)|$($menu.military)" }
            if ($militaryMenu -and $militaryMenu -eq $previousMilitaryMenu -and
                $menu.military -gt [int]$publishedMilitaryMenu[$menu.weapon]) {
                Write-MatchEvent -Path $Log -Json (@{
                    at = $now.ToString('o'); kind = 'military-unlock'; mode = 'mp'
                    weapon = $menu.weapon; tier = $menu.military; source = 'live'
                } | ConvertTo-Json -Compress)
                $publishedMilitaryMenu[$menu.weapon] = $menu.military
                Write-Status "Military menu sync | $($menu.weapon) | $($menu.military)/8"
            }
            $previousMilitaryMenu = $militaryMenu
            if ($snapshot -and $snapshot -eq $previousMenu -and !$reportedMenus.ContainsKey($snapshot)) {
                $reportedMenus[$snapshot] = $true
                Write-Status "Camo menu verified | $($menu.weapon) | Military $($menu.military)/8 | Special $($menu.special)/3"
                $goldKey = "gold|$($menu.weapon)"
                if ($menu.completionist -gt 0 -and !$completedChallenges.ContainsKey($goldKey)) {
                    Write-MatchEvent -Path $Log -Json (@{
                        at = $now.ToString('o'); kind = 'completionist-unlock'; mode = 'mp'
                        weapon = $menu.weapon; challenge = 'Gold'; source = 'live'
                    } | ConvertTo-Json -Compress)
                    $completedChallenges[$goldKey] = $true
                    Write-Status "Gold verified from camo menu | $($menu.weapon)"
                }
                if ($null -ne $menu.completionist -or $null -ne $menu.prestige) {
                    $sections = @()
                    if ($null -ne $menu.completionist) { $sections += "Completionist $($menu.completionist)/4" }
                    if ($null -ne $menu.prestige) { $sections += "Prestige $($menu.prestige)/4" }
                    Write-Status ($sections -join ' | ')
                }
                if ($menu.confirmed.Count) {
                    Write-Status ('Confirmed special camos | ' + (($menu.confirmed -replace '^menu-', '') -join ', '))
                }
                if ($menu.confirmed.Count) {
                    foreach ($id in $menu.confirmed) {
                        Write-MatchEvent -Path $Log -Json (@{
                            at = $now.ToString('o')
                            kind = $(if ($menu.weapon -eq 'Sang 9mm') { 'challenge-completed' } else { 'catalog-special-unlocked' })
                            mode = 'mp'
                            weapon = $menu.weapon; challenge = $id; source = 'live'
                        } | ConvertTo-Json -Compress)
                    }
                }
            }
            $previousMenu = $snapshot
        }
        $medalVisible = Test-LongshotMedal $frame
        if ($medalVisible -and (!$lastMedalSeen -or ($now - $lastMedalSeen).TotalMilliseconds -ge 650)) {
            $medalAssigned = $false
        }
        $readStatus = ($now - $lastStatusRead).TotalMilliseconds -ge 500
        $weaponAkimbo = $false
        $weapon = if ($readStatus -or ($medalVisible -and !$medalAssigned)) {
            Read-Weapon $frame ([ref]$weaponAkimbo)
        }
        if ($weapon -and !$deathOnFrame) {
            $lastAkimboSeen = if ($weaponAkimbo) { $now } else { [DateTimeOffset]::MinValue }
        }
        if ($weapon -and $weapon -eq $publishedWeapon) { $lastConfirmedWeaponSeen = $now }
        $objective = if ($weapon) { $longshotObjectives[$weapon] }
        if ($readStatus) {
            $lastStatusRead = $now
            $visibleMode = Read-MatchMode $frame
            $ammo = if ($weapon -and (!$visibleMode -or $weapon -ne $publishedWeapon)) {
                (Read-Hud $frame @(0.734, 0.824, 0.089, 0.053)) -join ' '
            }
            $hasAmmo = $ammo -match '\d{1,3}\s+\d{1,3}'
            if ($lobby -or $lobbyPhase -eq 'voting') { $visibleMode = $null; $hasAmmo = $false }
            if ($matchMode -like 'HARDCORE *' -and $visibleMode -eq ($matchMode -replace '^HARDCORE ', '')) {
                $visibleMode = $matchMode
            }
            if ($visibleMode -or ($weapon -and $hasAmmo)) {
                $lobbyPhase = $null
                if ($Live -and (!$matchOpen -or ($now - $lastMatchSeen).TotalSeconds -ge 60 -or
                    ($visibleMode -and $matchMode -and $visibleMode -ne $matchMode))) {
                    $matchNumber++
                    $combat.Generation = $matchNumber
                    $matchOpen = $true
                    $matchMode = $visibleMode
                    $kills = $deaths = $killStreak = $bestStreak = $sessionCount = 0
                    $combatBannerState.Clear()
                    $lastAkimboSeen = $lastConfirmedWeaponSeen = [DateTimeOffset]::MinValue
                    $candidateWeapon = $publishedWeapon = $null
                    Write-MatchEvent -Path $Log -Json (@{
                        kind = 'match-started'; source = 'live'; at = $now.ToString('o')
                        match = $matchNumber; matchMode = $visibleMode; kills = 0; deaths = 0; bestStreak = 0
                    } | ConvertTo-Json -Compress)
                    $modeLabel = if ($visibleMode) { $visibleMode } else { 'Mode unreadable' }
                    Write-Status "Match $matchNumber started | $modeLabel"
                }
                $lastMatchSeen = $now
                if (!$matchHudActive -and !$visibleMode) {
                    Write-Status 'Gameplay HUD detected | Match mode unreadable'
                }
                $matchHudActive = $true
            } elseif ($matchHudActive -and ($now - $lastMatchSeen).TotalSeconds -ge 12) {
                Write-Status 'Gameplay HUD not confirmed for 12 seconds'
                $matchHudActive = $false
            }
            if ($visibleMode) {
                if ($matchMode -ne $visibleMode) {
                    Write-Status "Multiplayer HUD detected | $visibleMode"
                    $matchMode = $visibleMode
                }
            }
            $weaponStatus = if ($weaponAkimbo) {
                'Weapon: Sang 9mm Akimbo | Objective: Space Time'
            } elseif ($objective) {
                "Weapon: $weapon | Objective: $($objective.Name) (longshot kills)"
            } elseif ($weapon) {
                "Weapon label: $weapon | Objective: none configured"
            } else {
                'Weapon HUD out of view or unreadable'
            }
            if (!$Live -and $lastWeaponStatus -ne $weaponStatus) {
                Write-Status $weaponStatus
                $lastWeaponStatus = $weaponStatus
            }
            if ($Live) {
                if (!$weapon) {
                    if ($candidateLastSeen -and ($now - $candidateLastSeen).TotalSeconds -gt 3) {
                        $candidateWeapon = $null
                        $candidateLastSeen = $null
                    }
                } elseif ($weapon -eq $publishedWeapon -or (Test-ReplayHud $frame)) {
                    $candidateWeapon = $null
                    $candidateLastSeen = $null
                } elseif ($candidateWeapon -ne $weapon -or
                    ($now - $candidateLastSeen).TotalSeconds -gt 3) {
                    $candidateWeapon = $weapon
                    $candidateLastSeen = $now
                    if ($ShowOcr) { Write-Status "Weapon candidate: $weapon (first matched read)" }
                } else {
                    $candidateLastSeen = $now
                    $focusEvent = @{
                        kind = 'weapon'; source = 'live'; mode = 'mp'
                        weapon = $weapon; at = $now.ToString('o')
                    } | ConvertTo-Json -Compress
                    Write-MatchEvent -Path $Log -Json $focusEvent
                    $publishedWeapon = $weapon
                    $lastConfirmedWeaponSeen = $now
                    Write-Status $weaponStatus
                }
            }
        }
        if ($Live -and ($now - $lastChallengeRead).TotalMilliseconds -ge 300) {
            $lastChallengeRead = $now
            $challenge = Read-ChallengeBanner $frame
            if ($challenge) {
                $recent = if (($now - $lastConfirmedWeaponSeen).TotalSeconds -le 5) { $publishedWeapon }
                $pin = $PinnedWeapon
                if (!$pin -and $TrackerPinFile) {
                    try {
                        if (([DateTime]::UtcNow - [IO.File]::GetLastWriteTimeUtc($TrackerPinFile)).TotalSeconds -lt 15) {
                            $pin = $weaponsByLabel[([IO.File]::ReadAllText($TrackerPinFile) -replace '[^a-z0-9]', '')]
                        }
                    } catch [IO.IOException] {}
                }
                $challengeWeapon = Resolve-ChallengeWeapon $weapon $recent $pin
                if ($challenge -eq 'gold-15') {
                    $goldWeapon = if ($weapon) { $weapon } else { $recent }
                    if ($goldWeapon -and !$completedChallenges.ContainsKey("gold|$goldWeapon")) {
                        Write-MatchEvent -Path $Log -Json (@{
                            at = $now.ToString('o'); kind = 'completionist-unlock'; mode = 'mp'
                            weapon = $goldWeapon; challenge = 'Gold'; source = 'live'
                        } | ConvertTo-Json -Compress)
                        $completedChallenges["gold|$goldWeapon"] = $true
                        Write-Status "Unlock detected | $goldWeapon Gold"
                    }
                } elseif (($challenge -eq 'menu-space-time' -or $challengeWeapon -eq 'Sang 9mm') -and
                    !$completedChallenges.ContainsKey($challenge)) {
                    if ($challenge -eq 'menu-space-time') { $challengeWeapon = 'Sang 9mm' }
                    Write-MatchEvent -Path $Log -Json (@{
                        at = $now.ToString('o'); kind = 'challenge-completed'; mode = 'mp'
                        weapon = $challengeWeapon; challenge = $challenge; source = 'live'
                    } | ConvertTo-Json -Compress)
                    $completedChallenges[$challenge] = $true
                    Write-Status "Challenge completed | $challengeWeapon | $challenge"
                } elseif (!$challengeWeapon -and $challenge -ne 'menu-space-time' -and !$unassignedChallenges.ContainsKey($challenge)) {
                    Write-MatchEvent -Path $Log -Json (@{
                        at = $now.ToString('o'); kind = 'challenge-completed'; mode = 'mp'
                        challenge = $challenge; source = 'live'
                    } | ConvertTo-Json -Compress)
                    Write-Status "Challenge banner seen | $challenge | checking tracker selection"
                    $unassignedChallenges[$challenge] = $true
                } elseif (!$unassignedChallenges.ContainsKey($challenge)) {
                    $reason = if ($challengeWeapon) { "no matching objective for $challengeWeapon" } else {
                        "weapon unconfirmed; use -PinnedWeapon for Hardcore"
                    }
                    Write-Status "Challenge banner seen | $challenge | $reason"
                    $unassignedChallenges[$challenge] = $true
                }
            }
        }
    } finally { $frame.Dispose() }

    if ($medalVisible) {
        $lastMedalSeen = $now
        if (!$medalAssigned -and $weapon) {
            $medalAssigned = $true
            if ($objective) {
                $event = [ordered]@{
                    at = $now.ToString('o')
                    mode = 'mp'
                    key = $objective.Key
                    delta = 1
                    weapon = $weapon
                    source = $(if ($Live) { 'live' } else { 'screenshot' })
                } | ConvertTo-Json -Compress
                if ($Live) {
                    Write-MatchEvent -Path $Log -Json $event
                    $sessionCount++
                    Write-Status "$($objective.Name) +1 | $weapon longshot | Detected this session: $sessionCount"
                } else {
                    Write-Output $event
                }
            } else {
                Write-Status "Longshot seen | No longshot objective configured for '$weapon'"
            }
        } elseif (!$weapon -and !$medalAssigned -and $ShowOcr) {
            Write-Host 'Longshot seen | Waiting for a readable weapon label'
        }
    }
    if ($Live) { Start-Sleep -Milliseconds 100 }
} while ($Live)
} finally {
    if ($militaryRunner) {
        $militaryCapture.Stop = $true
        try { $militaryRunner.Stop() } finally { $militaryRunner.Dispose() }
    }
    if ($combatRunner) {
        $combat.Stop = $true
        try { $combatRunner.Stop() } finally { $combatRunner.Dispose() }
    }
}
