param([string]$OutputDirectory, [string]$UpdatePublicKeyFile, [string]$OcrDirectory, [string]$CaptureDirectory,
    [string]$PreviewNumber)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'dist/desktop-preview' }
$output = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
if (Get-ChildItem -LiteralPath $output -Force) { throw "Build into an empty output folder: $output" }
$version = (Get-Content -LiteralPath (Join-Path $root 'desktop/version.txt') -Raw).Trim()
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part desktop version.' }
if ($PreviewNumber -and $PreviewNumber -cnotmatch '^\d+$') { throw 'Use a numeric preview build number.' }
$information = if ($PreviewNumber) { "$version-preview.$PreviewNumber" } else { $version }
$keyOption = @()
if ($UpdatePublicKeyFile) { $keyOption += '-p:UpdatePublicKeyFile=' + (Resolve-Path -LiteralPath $UpdatePublicKeyFile).Path }
& (Join-Path $PSScriptRoot 'build-detector-development.ps1') -OutputDirectory $output -OcrDirectory $OcrDirectory -CaptureDirectory $CaptureDirectory
dotnet publish (Join-Path $root 'desktop/CamoConnector/CamoConnector.csproj') -c Release -r win-x64 --self-contained true -p:Version=$version -p:InformationalVersion=$information @keyOption -o $output
if ($LASTEXITCODE) { throw 'Desktop host build failed.' }
dotnet publish (Join-Path $root 'desktop/CamoConnector.Updater/CamoConnector.Updater.csproj') -c Release -r win-x64 --self-contained true -p:Version=$version @keyOption -o $output
if ($LASTEXITCODE) { throw 'Desktop updater build failed.' }
$bridge = Join-Path $output 'Runtime/Bridge'
dotnet publish (Join-Path $root 'desktop/CamoConnector.Bridge/CamoConnector.Bridge.csproj') -c Release -r win-x64 --self-contained true -p:Version=$version -o $bridge
if ($LASTEXITCODE) { throw 'Desktop bridge build failed.' }
