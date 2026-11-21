param([string]$InterruptedRoot)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$sources = 'ReleaseVerification.cs','PortableReplacement.cs','GitHubRelease.cs','PendingUpdate.cs' | ForEach-Object {
    Join-Path $project "desktop/CamoConnector.Update/$_"
}
Add-Type -Path $sources

function Apply-TestUpdate([string]$directory, [switch]$Interrupt, [switch]$Unmanaged, [switch]$Slim) {
    $archive = [IO.MemoryStream]::new()
    $zip = [IO.Compression.ZipArchive]::new($archive, 'Create', $true)
    try {
        $paths = @('CoDStreamingTools.exe','Runtime/Bridge/CoDStreamingTools.Bridge.exe',
            'Runtime/Bridge/special-catalog.json','Runtime/Detector/CoDStreamingTools.Detector.exe','CoDStreamingTools.Updater.exe')
        if (!$Slim) { $paths += 'Runtime/Ocr/tesseract.exe','Runtime/Ocr/libstdc++-6.dll','Runtime/Ocr/tessdata/eng.traineddata','Runtime/Capture/ffmpeg.exe' }
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
$root = Join-Path ([IO.Path]::GetTempPath()) ('CoD Streaming Tools update test ' + [guid]::NewGuid())
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Data'))
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Runtime/Ocr'))
    [IO.File]::WriteAllText((Join-Path $root 'Data/settings.json'), 'settings')
    [IO.File]::WriteAllText((Join-Path $root 'notes.txt'), 'notes')
    [IO.File]::WriteAllText((Join-Path $root 'Runtime/Ocr/notes.txt'), 'runtime notes')
    [IO.File]::WriteAllText((Join-Path $root 'CoDStreamingTools.exe'), 'previous')
    pwsh -NoProfile -File $PSCommandPath -InterruptedRoot $root
    if ($LASTEXITCODE -ne 73) { throw "Expected interruption, received $LASTEXITCODE." }
    if ([IO.File]::ReadAllText((Join-Path $root 'CoDStreamingTools.exe')) -ne 'new') { throw 'Replacement never started.' }
    [CamoConnector.Update.PortableReplacement]::new($root).Recover()
    if ([IO.File]::ReadAllText((Join-Path $root 'CoDStreamingTools.exe')) -ne 'previous' -or
        (Test-Path -LiteralPath (Join-Path $root 'Runtime/Bridge/CoDStreamingTools.Bridge.exe'))) {
        throw 'Recovery failed to restore the original files.'
    }
    try {
        Apply-TestUpdate $root -Unmanaged
        throw 'Unmanaged archive was accepted.'
    } catch {
        if ($_.Exception.ToString() -notmatch 'duplicate or unmanaged path') { throw }
    }
    Apply-TestUpdate $root
    if ([IO.File]::ReadAllText((Join-Path $root 'CoDStreamingTools.exe')) -ne 'new' -or
        [IO.File]::ReadAllText((Join-Path $root 'Data/settings.json')) -ne 'settings' -or
        [IO.File]::ReadAllText((Join-Path $root 'notes.txt')) -ne 'notes' -or
        [IO.File]::ReadAllText((Join-Path $root 'Runtime/Ocr/notes.txt')) -ne 'runtime notes' -or
        [IO.File]::ReadAllText((Join-Path $root 'Runtime/Ocr/libstdc++-6.dll')) -ne 'new' -or
        [IO.File]::ReadAllText((Join-Path $root 'Runtime/Ocr/tessdata/eng.traineddata')) -ne 'new' -or
        (Test-Path -LiteralPath (Join-Path $root '.camo-update'))) { throw 'Successful update did not preserve data or clean up.' }
    [IO.File]::WriteAllText((Join-Path $root 'Runtime/Capture/ffmpeg.exe'), 'installed capture support')
    Apply-TestUpdate $root -Slim
    if ([IO.File]::ReadAllText((Join-Path $root 'Runtime/Capture/ffmpeg.exe')) -ne 'installed capture support' -or
        [IO.File]::ReadAllText((Join-Path $root 'Runtime/Ocr/tessdata/eng.traineddata')) -ne 'new') {
        throw 'The standard update removed an installed runtime.'
    }

    foreach ($component in 'Capture', 'Node') {
        foreach ($includeUnmanaged in $true, $false) {
            $archive = [IO.MemoryStream]::new()
            try {
                $zip = [IO.Compression.ZipArchive]::new($archive, 'Create', $true)
                try {
                    $paths = if ($component -eq 'Node') { @('Runtime/Node/node.exe', 'Runtime/Node/LICENSE') } else { @('Runtime/Capture/ffmpeg.exe') }
                    if ($includeUnmanaged) { $paths += 'CoDStreamingTools.exe' }
                    foreach ($path in $paths) {
                        $writer = [IO.StreamWriter]::new($zip.CreateEntry($path).Open())
                        try { $writer.Write('downloaded runtime') } finally { $writer.Dispose() }
                    }
                } finally { $zip.Dispose() }
                $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archive.ToArray()))
                $manifest = [CamoConnector.Update.ReleaseVerification+Manifest]::new('2.0.0','test.zip',$archive.Length,$sha)
                try {
                    [CamoConnector.Update.PortableReplacement]::InstallRuntime($archive, $manifest, $root, $component)
                    if ($includeUnmanaged) { throw 'A runtime pack could replace a program file.' }
                } catch {
                    if (!$includeUnmanaged -or $_.Exception.ToString() -notmatch 'duplicate or unmanaged path') { throw }
                }
                $expected = if ($includeUnmanaged) { 'installed capture support' } else { 'downloaded runtime' }
                $runtime = if ($component -eq 'Node') { 'Runtime/Node/node.exe' } else { 'Runtime/Capture/ffmpeg.exe' }
                $installed = Join-Path $root $runtime
                if ($component -eq 'Node' -and $includeUnmanaged) {
                    if (Test-Path -LiteralPath $installed) { throw 'An invalid Node pack was installed.' }
                } elseif ([IO.File]::ReadAllText($installed) -ne $expected) {
                    throw 'The runtime install did not leave the expected files in place.'
                }
            } finally { $archive.Dispose() }
        }
    }
    $pending = [IO.Directory]::CreateDirectory((Join-Path $root 'Data/PendingUpdate')).FullName
    $archivePath = Join-Path $pending 'update.zip'
    $content = [Text.Encoding]::UTF8.GetBytes('verified update bytes')
    [IO.File]::WriteAllBytes($archivePath, $content)
    $metadata = [Text.Encoding]::UTF8.GetBytes((@{
        version = '2.1.0'; asset = 'CoDStreamingTools-v2.1.0-win-x64.zip'; size = $content.Length
        sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content))
    } | ConvertTo-Json -Compress))
    $signer = [Security.Cryptography.RSA]::Create(2048)
    try {
        $key = $signer.ExportSubjectPublicKeyInfoPem()
        $signature = $signer.SignData($metadata, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
    } finally { $signer.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $pending 'manifest.json'), $metadata)
    [IO.File]::WriteAllBytes((Join-Path $pending 'manifest.sig'), $signature)
    $cached = $null
    $opened = [CamoConnector.Update.PendingUpdate]::Open($root, [version]'2.0.0', $key, [ref]$cached)
    if ($null -eq $opened -or $cached.Version -ne '2.1.0' -or $opened.Position -ne 0) {
        throw 'A verified pending update could not be opened offline.'
    }
    $opened.Dispose()
    $content[0] = $content[0] -bxor 1
    [IO.File]::WriteAllBytes($archivePath, $content)
    if ([CamoConnector.Update.PendingUpdate]::Open($root, [version]'2.0.0', $key, [ref]$cached)) {
        throw 'A modified staged archive was accepted.'
    }
    'Passed portable recovery, standard update preservation and runtime pack isolation.'
} finally {
    if (Test-Path -LiteralPath $root) { [IO.Directory]::Delete($root, $true) }
}

# GitHub Actions propagates the last native exit code. The child exit was expected.
exit 0
