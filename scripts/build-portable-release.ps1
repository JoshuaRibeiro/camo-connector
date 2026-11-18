param(
    [Parameter(Mandatory)][string]$OcrDirectory,
    [Parameter(Mandatory)][string]$CaptureDirectory,
    [Parameter(Mandatory)][string]$UpdatePublicKeyFile,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CertificateThumbprint,
    [string]$TimestampServer
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $root 'desktop/version.txt') -Raw).Trim()
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part release version.' }
if (!$env:CAMO_PORTABLE_SIGNING_KEY) { throw 'Supply the manifest private key through CAMO_PORTABLE_SIGNING_KEY.' }
if ([bool]$CertificateThumbprint -ne [bool]$TimestampServer) { throw 'Supply both Authenticode thumbprint and timestamp server, or neither.' }
$output = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
if (Get-ChildItem -LiteralPath $output -Force) { throw 'Release output must be empty.' }
$assembly = Join-Path $output 'app'
Add-Type -Path @(
    (Join-Path $root 'desktop/CamoConnector.Update/ReleaseVerification.cs'),
    (Join-Path $root 'desktop/CamoConnector.Update/PortableReplacement.cs')
)
$key = [Security.Cryptography.RSA]::Create()
try {
    $key.ImportFromPem($env:CAMO_PORTABLE_SIGNING_KEY)
    $public = Get-Content -LiteralPath $UpdatePublicKeyFile -Raw
    $verification = [Security.Cryptography.RSA]::Create()
    try {
        $verification.ImportFromPem($public)
        if ($key.KeySize -lt 2048 -or $key.ExportSubjectPublicKeyInfoPem() -cne $verification.ExportSubjectPublicKeyInfoPem()) {
            throw 'The release signing key does not match the embedded public key.'
        }
    } finally { $verification.Dispose() }
    & (Join-Path $PSScriptRoot 'build-desktop-preview.ps1') -OutputDirectory $assembly -OcrDirectory $OcrDirectory -CaptureDirectory $CaptureDirectory -UpdatePublicKeyFile $UpdatePublicKeyFile
    $programs = @('CamoConnector.exe','CamoConnector.Updater.exe','Runtime/Bridge/CamoConnector.Bridge.exe','Runtime/Detector/CamoConnector.Detector.exe')
    if ($CertificateThumbprint) {
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
        foreach ($program in $programs) {
            $signature = Set-AuthenticodeSignature -LiteralPath (Join-Path $assembly $program) -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
            if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate) {
                throw "Authenticode signing or timestamp validation failed for $program."
            }
        }
    } else { Write-Warning 'Windows executables are unsigned. Only the update manifest is signed.' }
    $files = @(Get-ChildItem -LiteralPath $assembly -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($assembly, $_.FullName).Replace('\','/')
        if ([CamoConnector.Update.PortableReplacement]::IsAppFile($relative)) {
            [pscustomobject]@{ FullName = $_.FullName; Relative = $relative }
        } elseif ($_.Extension -ne '.pdb') { throw "Unexpected release file: $relative" }
    })
    $required = $programs + @('Runtime/Bridge/special-catalog.json','Runtime/Ocr/tesseract.exe',
        'Runtime/Ocr/tessdata/eng.traineddata','Runtime/Capture/ffmpeg.exe')
    foreach ($file in $required) {
        if ($file -cnotin $files.Relative) { throw "Missing release file: $file" }
    }
    $asset = "CamoConnector-v$version-win-x64.zip"
    $archive = Join-Path $output $asset
    $zip = [IO.Compression.ZipFile]::Open($archive, 'Create')
    try {
        foreach ($file in ($files | Sort-Object Relative)) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.Relative)
        }
    } finally { $zip.Dispose() }
    $manifest = @{
        version = $version
        asset = $asset
        size = (Get-Item -LiteralPath $archive).Length
        sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json -Compress
    $body = [Text.Encoding]::UTF8.GetBytes($manifest)
    $signature = $key.SignData($body, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)
    $signed = [CamoConnector.Update.ReleaseVerification]::VerifyManifest($body, $signature, $public, "v$version", [version]'0.0.0')
    $inputStream = [IO.File]::OpenRead($archive)
    try { [CamoConnector.Update.ReleaseVerification]::VerifyArchive($inputStream, $signed) }
    finally { $inputStream.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $output 'portable-update.json'), $body)
    [IO.File]::WriteAllBytes((Join-Path $output 'portable-update.sig'), $signature)
    Get-Item -LiteralPath $archive,(Join-Path $output 'portable-update.json'),(Join-Path $output 'portable-update.sig')
} finally {
    $key.Dispose()
}
