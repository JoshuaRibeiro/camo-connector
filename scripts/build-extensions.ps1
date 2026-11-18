param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$output = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
$base = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
if ($base.manifest_version -ne 3 -or $base.version -cnotmatch '^\d+\.\d+\.\d+$') {
    throw 'Expected a Manifest V3 extension and a three-part version.'
}
$files = @('background.js','tracker.js','backup.js','pairing.html','pairing.css','pairing.js',
    'icon-16.png','icon-32.png','icon-48.png','icon-128.png')
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($browser in 'Chrome','Firefox','Edge','Opera') {
    $manifest = $base | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    if ($browser -eq 'Firefox') {
        $manifest.background = @{ scripts = @('background.js') }
        $manifest.host_permissions = @('http://127.0.0.1/*')
        $manifest | Add-Member -NotePropertyName browser_specific_settings -NotePropertyValue @{
            gecko = @{
                id = 'camo-connector@joshuaribeiro.github.com'
                strict_min_version = '140.0'
                data_collection_permissions = @{
                    required = @('websiteContent', 'authenticationInfo', 'personallyIdentifyingInfo')
                }
            }
            gecko_android = @{ strict_min_version = '142.0' }
        }
    }
    $target = [IO.Directory]::CreateDirectory((Join-Path $output $browser)).FullName
    if ((Get-ChildItem -LiteralPath $target -Force | Where-Object Name -NotIn ($files + @('manifest.json')))) {
        throw "Unexpected files in $target. Build into an empty output directory."
    }
    foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target -Force }
    [IO.File]::WriteAllText((Join-Path $target 'manifest.json'), ($manifest | ConvertTo-Json -Depth 12), $utf8)
    $entries = ($files + @('manifest.json')) | ForEach-Object { Join-Path $target $_ }
    $archive = Join-Path $output "CamoConnector-$browser-v$($manifest.version).zip"
    Compress-Archive -LiteralPath $entries -DestinationPath $archive -Force
    Write-Output $archive
}
