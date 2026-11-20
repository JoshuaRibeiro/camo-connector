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
$slim = Join-Path $output 'slim'
$programs = @('CamoConnector.exe','CamoConnector.Updater.exe','Runtime/Bridge/CamoConnector.Bridge.exe','Runtime/Detector/CamoConnector.Detector.exe')
$coreFiles = $programs + 'Runtime/Bridge/special-catalog.json'

function Get-ReleaseFiles($directory) {
    Get-ChildItem -LiteralPath $directory -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($directory, $_.FullName).Replace('\','/')
        if ([CamoConnector.Update.PortableReplacement]::IsAppFile($relative)) {
            [pscustomobject]@{ FullName = $_.FullName; Relative = $relative }
        } elseif ($_.Extension -ne '.pdb') { throw "Unexpected release file: $relative" }
    }
}

function Write-Package($files, $required, $flavor, $manifestName) {
    foreach ($file in $required) {
        if ($file -cnotin $files.Relative) { throw "Missing release file: $file" }
    }
    $asset = "CamoConnector-v$version-$flavor.zip"
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
    $signed = [CamoConnector.Update.ReleaseVerification]::VerifyManifest($body, $signature, $public,
        "v$version", [version]'0.0.0', $flavor)
    $inputStream = [IO.File]::OpenRead($archive)
    try { [CamoConnector.Update.ReleaseVerification]::VerifyArchive($inputStream, $signed) }
    finally { $inputStream.Dispose() }
    $json = Join-Path $output "$manifestName.json"
    $sig = Join-Path $output "$manifestName.sig"
    [IO.File]::WriteAllBytes($json, $body)
    [IO.File]::WriteAllBytes($sig, $signature)
    Get-Item -LiteralPath $archive,$json,$sig
}

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
    & (Join-Path $PSScriptRoot 'build-desktop-preview.ps1') -OutputDirectory $slim -SlimPortable -OcrDirectory $OcrDirectory -CaptureDirectory $CaptureDirectory -UpdatePublicKeyFile $UpdatePublicKeyFile
    if ($CertificateThumbprint) {
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
        foreach ($directory in @($assembly, $slim)) {
            foreach ($program in $programs) {
                $signature = Set-AuthenticodeSignature -LiteralPath (Join-Path $directory $program) -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer $TimestampServer
                if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate) {
                    throw "Authenticode signing or timestamp validation failed for $program."
                }
            }
        }
    } else { Write-Warning 'Windows executables are unsigned. Only the update manifests are signed.' }
    $offline = @(Get-ReleaseFiles $assembly)
    $ocrRequired = @('Runtime/Ocr/tesseract.exe', 'Runtime/Ocr/tessdata/eng.traineddata')
    $captureRequired = @('Runtime/Capture/ffmpeg.exe')
    $standard = @(Get-ReleaseFiles $slim)

    Write-Package $standard ($coreFiles + $ocrRequired + $captureRequired) 'win-x64-slim' 'portable-slim-update'
    Write-Package $offline ($coreFiles + $ocrRequired + $captureRequired) 'win-x64' 'portable-update'
    Write-Package @($offline | Where-Object Relative -CLike 'Runtime/Ocr/*') $ocrRequired 'win-x64-ocr' 'runtime-ocr'
    Write-Package @($offline | Where-Object Relative -CLike 'Runtime/Capture/*') $captureRequired 'win-x64-capture' 'runtime-capture'
} finally {
    $key.Dispose()
}
