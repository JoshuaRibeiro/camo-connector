param(
    [ValidateSet('Check','Install')][string]$Mode = 'Check',
    [string]$Repository,
    [string]$InstallRoot,
    [int]$AppProcessId,
    [string]$ExpectedVersion,
    [switch]$StagedCopy
)
$ErrorActionPreference = 'Stop'
$managedFiles = @(
    'background.js','backup.cjs','backup.js','bridge.cjs','capture-frame.ps1',
    'connector-app.ps1','connector-updater.ps1','Launch CoD Streaming Tools.cmd',
    'manifest.json','README.txt','special-catalog.json','sunburn.ps1','tracker.js'
)

function Get-ConnectorRelease([string]$repo) {
    if ($repo -notmatch '^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$' -or
        ($repo.Split('/')[1] -in @('.', '..'))) { throw 'Enter a GitHub repository as owner/repo.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" `
        -Headers @{ 'User-Agent'='Camo-Connector' } -UseBasicParsing -TimeoutSec 15
    if ($release.draft -or $release.prerelease -or $release.tag_name -notmatch '^v?(\d+\.\d+\.\d+)$') {
        throw 'The latest release does not have a supported version tag.'
    }
    $version = $Matches[1]
    $asset = @($release.assets | Where-Object name -eq "camo-connector-v$version.zip")
    if ($asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:([a-f0-9]{64})$') {
        throw 'The release needs one versioned Connector ZIP with a GitHub SHA-256 digest.'
    }
    $digest = $Matches[1]
    $url = [uri]$asset[0].browser_download_url
    if ($url.Scheme -ne 'https' -or $url.Host -ne 'github.com' -or
        $url.AbsolutePath -ne "/$repo/releases/download/$($release.tag_name)/$($asset[0].name)" -or
        $asset[0].size -lt 1 -or $asset[0].size -gt 25MB) { throw 'Unexpected release asset location or size.' }
    @{ Version = $version; Url = $url; Hash = $digest }
}

function Install-ConnectorArchive([string]$archivePath, [string]$target, [string]$stage, [string]$version) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $new = [IO.Directory]::CreateDirectory((Join-Path $stage 'new')).FullName
    $backup = [IO.Directory]::CreateDirectory((Join-Path $stage 'previous')).FullName
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $seen = @{}
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if ($name -eq 'camo-connector/') { continue }
            if ($name -notmatch '^camo-connector/([A-Za-z0-9][A-Za-z0-9 ._-]*\.[A-Za-z0-9]+)$' -or
                $entry.Length -gt 10MB) { throw 'The release ZIP contains an unexpected file.' }
            $leaf = $Matches[1]
            if (([IO.Path]::GetFileNameWithoutExtension($leaf) -split '\.')[0] -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                throw "The release ZIP contains a reserved file name $leaf."
            }
            if ($seen.ContainsKey($leaf)) { throw 'The release ZIP contains duplicate files.' }
            $seen[$leaf] = $true
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $new $leaf))
        }
        foreach ($leaf in $managedFiles) {
            if (!$seen.ContainsKey($leaf)) { throw "The release ZIP is missing $leaf." }
        }
    } finally { $archive.Dispose() }
    $manifest = Get-Content -LiteralPath (Join-Path $new 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.version -ne $version) { throw 'The ZIP version differs from the GitHub release.' }

    $touched = [Collections.Generic.List[string]]::new()
    $added = [Collections.Generic.List[string]]::new()
    foreach ($leaf in $seen.Keys) {
        if ($leaf -in $managedFiles) { continue }
        if (Test-Path -LiteralPath (Join-Path $target $leaf)) { throw "The update would replace an unmanaged file named $leaf." }
    }
    foreach ($leaf in $managedFiles) {
        $destination = Join-Path $target $leaf
        if (Test-Path -LiteralPath $destination) {
            if ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "The app file $leaf is a link."
            }
            Copy-Item -LiteralPath $destination -Destination (Join-Path $backup $leaf)
        }
    }
    try {
        foreach ($leaf in $managedFiles) {
            $touched.Add($leaf)
            Copy-Item -LiteralPath (Join-Path $new $leaf) -Destination (Join-Path $target $leaf) -Force
        }
        foreach ($leaf in $seen.Keys) {
            if ($leaf -in $managedFiles) { continue }
            [IO.File]::Copy((Join-Path $new $leaf), (Join-Path $target $leaf), $false)
            $added.Add($leaf)
        }
    } catch {
        $failure = $_
        try {
            foreach ($leaf in $added) { Remove-Item -LiteralPath (Join-Path $target $leaf) -Force }
            foreach ($leaf in $touched) {
                $previous = Join-Path $backup $leaf
                $destination = Join-Path $target $leaf
                if (Test-Path -LiteralPath $previous) { Copy-Item -LiteralPath $previous -Destination $destination -Force }
                elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
            }
        } catch { throw "Update and automatic rollback failed. Previous files are at $backup. $($_.Exception.Message)" }
        throw $failure
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$installed = Get-Content -LiteralPath (Join-Path $InstallRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($Mode -eq 'Check') {
    $release = Get-ConnectorRelease $Repository
    @{ installed = $installed.version; latest = $release.Version
        available = ([version]$release.Version -gt [version]$installed.version) } | ConvertTo-Json -Compress
    return
}
Add-Type -AssemblyName System.Windows.Forms
$stage = [IO.Directory]::CreateDirectory((Join-Path $env:TEMP ('CoDStreamingTools-Update-' + [guid]::NewGuid().ToString('N')))).FullName
$preserveStage = $false
$reopen = $true
try {
    $app = Get-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    if ($app -and !$app.WaitForExit(20000)) { $reopen = $false; throw 'The connector did not close. Try again after stopping it.' }
    $release = Get-ConnectorRelease $Repository
    if ($release.Version -ne $ExpectedVersion -or [version]$release.Version -le [version]$installed.version) {
        throw 'The available release changed. Check for updates again.'
    }
    $zip = Join-Path $stage 'release.zip'
    Invoke-WebRequest -Uri $release.Url -UseBasicParsing -TimeoutSec 60 -OutFile $zip
    if ((Get-Item -LiteralPath $zip).Length -gt 25MB -or
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $release.Hash) { throw 'Release ZIP checksum failed.' }
    Install-ConnectorArchive $zip $InstallRoot $stage $release.Version
    [void][Windows.Forms.MessageBox]::Show("Updated to $($release.Version). Reload the browser extension and refresh the tracker.",'CoD Streaming Tools')
} catch {
    $preserveStage = $_.Exception.Message -like '*automatic rollback failed*'
    if ($preserveStage) { $reopen = $false }
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'CoD Streaming Tools update')
} finally {
    if (!$preserveStage) { [IO.Directory]::Delete($stage,$true) }
    if ($reopen) {
        $launcher = Join-Path $InstallRoot 'Launch CoD Streaming Tools.cmd'
        Start-Process -FilePath $launcher -WorkingDirectory $InstallRoot -WindowStyle Hidden
    }
    if ($StagedCopy) { Remove-Item -LiteralPath $PSCommandPath -ErrorAction SilentlyContinue }
}
