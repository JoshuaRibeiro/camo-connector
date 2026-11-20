param([string]$Executable = "$PSScriptRoot/../dist/desktop-preview/Runtime/Bridge/CamoConnector.Bridge.exe")

$ErrorActionPreference = 'Stop'
$application = (Resolve-Path -LiteralPath $Executable).Path
$data = [IO.Directory]::CreateTempSubdirectory('camo bridge ').FullName
$stdout = Join-Path $data 'stdout.log'
$stderr = Join-Path $data 'stderr.log'
$secret = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$key = [Convert]::ToHexString($secret).ToLowerInvariant()
$eventLog = Join-Path $data 'sunburn-events.jsonl'
$event = '{"source":"live","mode":"mp","key":"signal-50:sp-3","delta":1}' + [Environment]::NewLine
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(2)
$process = $null

function Check-Events($Status, $Headers = @{}, $Route = 'events') {
    $request = [Net.Http.HttpRequestMessage]::new('GET', "http://127.0.0.1:47150/$Route")
    try {
        foreach ($header in $Headers.GetEnumerator()) {
            [void]$request.Headers.TryAddWithoutValidation($header.Key, $header.Value)
        }
        $response = $client.Send($request)
        try {
            if ([int]$response.StatusCode -ne $Status) {
                throw "Expected HTTP $Status, received $($response.StatusCode)."
            }
            if ($Status -eq 200) {
                $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

try {
    [IO.File]::WriteAllText((Join-Path $data 'bridge-pairing.key'), $key)
    $historical = @(
        '{"kind":"catalog-special-unlocked","source":"live","mode":"mp","weapon":"Signal 50","challenge":"Sunburn"}'
        '{"kind":"catalog-special-unlocked","source":"live","mode":"mp","weapon":".50 GS","challenge":"Frosted"}'
        '{"kind":"weapon","source":"live","mode":"mp","weapon":"Sang 9mm"}'
        '{"kind":"progress-snapshot","source":"live","mode":"mp","weapon":"Signal 50","key":"signal-50:sp-3","count":4,"goal":40}'
    )
    [IO.File]::WriteAllText($eventLog, $event + (($historical -join "`n") + "`n"))
    $process = Start-Process -FilePath $application -ArgumentList ('"' + $data + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $ready = Select-String -LiteralPath $stdout -SimpleMatch 'Camo bridge ready'
        if ($ready -or $process.HasExited) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$ready -or $process.HasExited) {
        throw "Native bridge did not start: $(Get-Content -LiteralPath $stderr -Raw)"
    }
    Check-Events 401
    Check-Events 403 @{ Origin = 'https://mw4.darkmattersolutions.uk'; Authorization = "Bearer $key" }
    $origin = 'chrome-extension://' + ('a' * 32)
    $message = [Text.Encoding]::UTF8.GetBytes("Camo Connector browser v1:$origin")
    $token = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData($secret, $message)).ToLowerInvariant()
    $headers = @{ Origin = $origin; 'X-Camo-Client' = $origin; Authorization = "Bearer $token" }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $before = Check-Events 200 $headers
        if ($before.trackedRows.Count -ge 2) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (@($before.trackedRows | Where-Object { $_.confirmed -and $_.camo -in 'Sunburn','Frosted' }).Count -ne 2) {
        throw 'The bridge lost confirmed camo unlocks on startup.'
    }
    $frosted = $before.trackedRows | Where-Object camo -eq Frosted
    if ($null -ne $frosted.goal -or $null -ne $frosted.value) { throw 'The bridge invented a goal for Frosted.' }
    if ($before.weapon -or $before.progress) { throw 'The bridge replayed stale focus or a progress snapshot.' }
    if ($before.count -ne 0) { throw 'The bridge replayed an existing progress event.' }
    [IO.File]::AppendAllText($eventLog, $event)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $after = Check-Events 200 $headers
        if ($after.count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($after.count -ne 1) { throw 'The bridge missed the new progress event.' }
    Check-Events 403 $headers 'tracker-events'
    $native = @{ Authorization = "Bearer $key" }
    $embedded = Check-Events 200 $native 'tracker-events'
    if ($embedded.count -ne 0 -or $embedded.session -eq $after.session -or $embedded.trackedRows.Count -lt 2) {
        throw 'The embedded tracker replayed browser deltas or lost confirmed unlocks.'
    }
    Check-Events 409 $headers
    [IO.File]::AppendAllText($eventLog, $event)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $embedded = Check-Events 200 $native 'tracker-events'
        if ($embedded.count -eq 1) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($embedded.count -ne 1) { throw 'The embedded tracker missed a new progress event.' }
    Start-Sleep -Milliseconds 3100
    $resumed = Check-Events 200 $headers
    if ($resumed.count -ne 0 -or $resumed.session -eq $embedded.session) {
        throw 'Browser sync failed to resume without replaying desktop deltas.'
    }
    $catalog = Check-Events 200 $native 'local/catalog'
    $anvil = $catalog.catalog.weapons | Where-Object id -eq 'pm-40-anvil'
    $nightSnow = $anvil.camos | Where-Object id -eq 'sp-1'
    if ($nightSnow.artwork -ne '/local/artwork/night-snow.png') { throw 'Night Snow is missing its catalog artwork.' }
    $artwork = $client.GetByteArrayAsync("http://127.0.0.1:47150$($nightSnow.artwork)").GetAwaiter().GetResult()
    if ($artwork.Length -lt 8 -or [Convert]::ToHexString($artwork[0..7]) -ne '89504E470D0A1A0A') {
        throw 'Embedded Night Snow artwork did not return a PNG.'
    }
    $overlayHeaders = @{ Authorization = 'Bearer ' + ([uri]$catalog.overlayUrl).Fragment.TrimStart('#') }
    $military = '{"kind":"military-unlock","source":"live","mode":"mp","weapon":"PM-40 Anvil","tier":1}'
    $focus = '{"kind":"weapon","source":"live","mode":"mp","weapon":"PM-40 Anvil"}'
    $snapshot = '{"kind":"progress-snapshot","source":"live","mode":"mp","weapon":"PM-40 Anvil","type":"weapon-level","count":9,"goal":11,"objective":"Reach Weapon Level 11"}'
    [IO.File]::AppendAllLines($eventLog, [string[]]@($military, $focus, $snapshot))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
        if ($overlay.level -eq 9) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($overlay.rows.Count -ne 1 -or $overlay.rows[0].name -ne 'Military Camo 2' -or
        $overlay.rows[0].count -ne 9 -or $overlay.rows[0].goal -ne 11) {
        throw 'The overlay missed the PM-40 weapon-level snapshot.'
    }
    $levelUp = '{"kind":"weapon-level-up","source":"live","mode":"mp","weapon":"PM-40 Anvil","level":'
    foreach ($expected in @(@{ Level = 10; Rows = 1 }, @{ Level = 16; Rows = 2 }, @{ Level = 31; Rows = 3 })) {
        [IO.File]::AppendAllLines($eventLog, [string[]]@(($levelUp + $expected.Level + '}'), $snapshot))
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
            if ($overlay.level -eq $expected.Level) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($overlay.rows.Count -ne $expected.Rows -or $overlay.rows[0].name -ne 'Military Camo 2' -or
            ($expected.Level -ge 16 -and $overlay.rows[1].artwork -ne $nightSnow.artwork) -or
            ($expected.Level -ge 31 -and $overlay.rows[2].name -ne 'Recursive')) {
            throw "The overlay listed the wrong challenges at weapon level $($expected.Level)."
        }
    }
    Write-Output 'Native bridge authentication and progress checks passed.'
} finally {
    if ($process) {
        if (!$process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    $client.Dispose()
    [IO.Directory]::Delete($data, $true)
}
