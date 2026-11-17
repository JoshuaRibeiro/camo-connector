param([string]$InterruptedRoot)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$sources = 'ReleaseVerification.cs','PortableReplacement.cs' | ForEach-Object {
    Join-Path $project "desktop/CamoConnector.Update/$_"
}
Add-Type -Path $sources

function Apply-TestUpdate([string]$directory, [switch]$Interrupt, [switch]$Unmanaged) {
    $archive = [IO.MemoryStream]::new()
    $zip = [IO.Compression.ZipArchive]::new($archive, 'Create', $true)
    try {
        $paths = @('CamoConnector.exe','Runtime/Bridge/CamoConnector.Bridge.exe',
            'Runtime/Bridge/special-catalog.json','Runtime/Detector/CamoConnector.Detector.exe','CamoConnector.Updater.exe',
            'Runtime/Ocr/tesseract.exe','Runtime/Ocr/tessdata/eng.traineddata','Runtime/Capture/ffmpeg.exe')
        if ($Unmanaged) { $paths += 'Data/settings.json' }
        foreach ($path in $paths) {
            $entry = $zip.CreateEntry($path)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('new') } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archive.ToArray()))
    $manifest = [CamoConnector.Update.ReleaseVerification+Manifest]::new('2.0.0','test.zip',$archive.Length,$sha)
    $transaction = [CamoConnector.Update.PortableReplacement]::new($directory)
    try {
        if ($Interrupt) {
            $transaction.Apply($archive, $manifest, [Action[int]]{ param($count) if ($count -eq 2) { [Environment]::Exit(73) } })
        } else { $transaction.Apply($archive, $manifest) }
    } finally { $archive.Dispose() }
}

if ($InterruptedRoot) { Apply-TestUpdate $InterruptedRoot -Interrupt; exit 1 }
$root = Join-Path ([IO.Path]::GetTempPath()) ('Camo Connector update test ' + [guid]::NewGuid())
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Data'))
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Runtime/Ocr'))
    [IO.File]::WriteAllText((Join-Path $root 'Data/settings.json'), 'settings')
    [IO.File]::WriteAllText((Join-Path $root 'notes.txt'), 'notes')
    [IO.File]::WriteAllText((Join-Path $root 'Runtime/Ocr/notes.txt'), 'runtime notes')
    [IO.File]::WriteAllText((Join-Path $root 'CamoConnector.exe'), 'previous')
    pwsh -NoProfile -File $PSCommandPath -InterruptedRoot $root
    if ($LASTEXITCODE -ne 73) { throw "Expected interruption, received $LASTEXITCODE." }
    if ([IO.File]::ReadAllText((Join-Path $root 'CamoConnector.exe')) -ne 'new') { throw 'Replacement never started.' }
    [CamoConnector.Update.PortableReplacement]::new($root).Recover()
    if ([IO.File]::ReadAllText((Join-Path $root 'CamoConnector.exe')) -ne 'previous' -or
        (Test-Path -LiteralPath (Join-Path $root 'Runtime/Bridge/CamoConnector.Bridge.exe'))) {
        throw 'Recovery failed to restore the original files.'
    }
    try {
        Apply-TestUpdate $root -Unmanaged
        throw 'Unmanaged archive was accepted.'
    } catch {
        if ($_.Exception.ToString() -notmatch 'duplicate or unmanaged path') { throw }
    }
    Apply-TestUpdate $root
    if ([IO.File]::ReadAllText((Join-Path $root 'CamoConnector.exe')) -ne 'new' -or
        [IO.File]::ReadAllText((Join-Path $root 'Data/settings.json')) -ne 'settings' -or
        [IO.File]::ReadAllText((Join-Path $root 'notes.txt')) -ne 'notes' -or
        [IO.File]::ReadAllText((Join-Path $root 'Runtime/Ocr/notes.txt')) -ne 'runtime notes' -or
        [IO.File]::ReadAllText((Join-Path $root 'Runtime/Ocr/tessdata/eng.traineddata')) -ne 'new' -or
        (Test-Path -LiteralPath (Join-Path $root '.camo-update'))) { throw 'Successful update did not preserve data or clean up.' }
    'Passed interrupted replacement, recovery, unmanaged ZIP rejection and data preservation.'
} finally {
    if (Test-Path -LiteralPath $root) { [IO.Directory]::Delete($root, $true) }
}
