param([string]$OcrDirectory, [string]$OutputDirectory, [string]$CaptureDirectory, [switch]$SlimPortable)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'dist/detector-development' }
$output = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
if (Get-ChildItem -LiteralPath $output -Force) { throw "Build into an empty output folder: $output" }
if ($OcrDirectory) {
    $engine = Join-Path $OcrDirectory 'tesseract.exe'
    $model = Join-Path $OcrDirectory 'tessdata/eng.traineddata'
    if (!(Test-Path -LiteralPath $engine -PathType Leaf) -or !(Test-Path -LiteralPath $model -PathType Leaf)) {
        throw 'The OCR directory must contain tesseract.exe and tessdata/eng.traineddata.'
    }
    $runtime = [IO.Directory]::CreateDirectory((Join-Path $output 'Runtime/Ocr')).FullName
    Copy-Item -LiteralPath $engine -Destination $runtime
    Get-ChildItem -LiteralPath $OcrDirectory -File -Filter '*.dll' | Copy-Item -Destination $runtime
    Get-ChildItem -LiteralPath $OcrDirectory -File | Where-Object Name -Match '^(LICENSE|COPYING|NOTICE)' |
        Copy-Item -Destination $runtime
    $models = [IO.Directory]::CreateDirectory((Join-Path $runtime 'tessdata')).FullName
    Copy-Item -LiteralPath $model -Destination $models
}
if ($CaptureDirectory) {
    $ffmpeg = Join-Path $CaptureDirectory 'ffmpeg.exe'
    if (!(Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
        throw 'The capture directory must contain ffmpeg.exe with DirectShow support.'
    }
    $capture = [IO.Directory]::CreateDirectory((Join-Path $output 'Runtime/Capture')).FullName
    Copy-Item -LiteralPath $ffmpeg -Destination $capture
    Get-ChildItem -LiteralPath $CaptureDirectory -File -Filter '*.dll' | Copy-Item -Destination $capture
    Get-ChildItem -LiteralPath $CaptureDirectory -File | Where-Object Name -Match '^(LICENSE|COPYING|NOTICE)' |
        Copy-Item -Destination $capture
}
$project = Join-Path $root 'desktop/CamoConnector.Detector/CamoConnector.Detector.csproj'
$selfContained = if ($SlimPortable) { 'false' } else { 'true' }
dotnet publish $project -c Release --self-contained $selfContained -o (Join-Path $output 'Runtime/Detector')
if ($LASTEXITCODE) { throw 'Detector build failed.' }
