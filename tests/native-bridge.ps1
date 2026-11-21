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
    $request = [Net.Http.HttpRequestMessage]::new('GET', "$baseUrl/$Route")
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
    $process = Start-Process -FilePath $application -ArgumentList ('"' + $data + '"', '--ephemeral-port') -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $ready = Select-String -LiteralPath $stdout -Pattern '^Camo bridge ready at (http://127\.0\.0\.1:\d+)$'
        if ($ready -or $process.HasExited) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$ready -or $process.HasExited) {
        $state = if ($process.HasExited) { "exit code $($process.ExitCode)" } else { 'timed out waiting for readiness' }
        $startupOutput = Get-Content -LiteralPath $stdout -Raw
        $startupErrors = Get-Content -LiteralPath $stderr -Raw
        throw "Native bridge did not start ($state).`nExecutable: $application`nstdout:`n$startupOutput`nstderr:`n$startupErrors"
    }
    $baseUrl = $ready.Matches[0].Groups[1].Value
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
    $artwork = $client.GetByteArrayAsync("$baseUrl$($nightSnow.artwork)").GetAwaiter().GetResult()
    if ($artwork.Length -lt 8 -or [Convert]::ToHexString($artwork[0..7]) -ne '89504E470D0A1A0A') {
        throw 'Embedded Night Snow artwork did not return a PNG.'
    }
    $overlayHeaders = @{ Authorization = 'Bearer ' + ([uri]$catalog.overlayUrl).Fragment.TrimStart('#') }
    $layoutArgs = @{ Uri = "$baseUrl/local/overlay-layout"; Method = 'Post'; ContentType = 'application/json'; NoProxy = $true }
    $denied = Invoke-WebRequest @layoutArgs -Headers $overlayHeaders -Body '{"layout":"11annah"}' -SkipHttpErrorCheck
    if ($denied.StatusCode -ne 401) { throw 'OBS access must be read-only.' }
    $invalid = Invoke-WebRequest @layoutArgs -Headers $native -Body '{"layout":"unknown"}' -SkipHttpErrorCheck
    if ($invalid.StatusCode -ne 400) { throw 'Unsupported overlay layout was accepted.' }
    $tracker = Invoke-RestMethod @layoutArgs -Headers $native -Body '{"layout":"11annah"}'
    $selectedOverlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
    if ($tracker.overlayLayout -ne '11annah' -or $selectedOverlay.overlayLayout -ne '11annah') {
        throw 'The existing overlay endpoint did not pick up the selected layout.'
    }
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
    $mojave = @(
        '{"kind":"weapon","source":"live","mode":"mp","weapon":"Mojave .357"}'
        '{"kind":"military-unlock","source":"live","mode":"mp","weapon":"Mojave .357","tier":8}'
        '{"kind":"progress-snapshot","source":"live","mode":"mp","weapon":"Mojave .357","type":"camo","camo":"Garland","count":3,"goal":30}'
    )
    [IO.File]::AppendAllLines($eventLog, [string[]]$mojave)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
        $garland = $overlay.rows | Where-Object name -eq 'Garland'
        if ($garland.count -eq 3) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($garland.count -ne 3 -or $garland.goal -ne 30) {
        throw 'Garland progress must appear even when the Military tiers only confirm 39 unlocks.'
    }
    $longshotsBefore = (Check-Events 200 $native 'local/state').progress.'signal-50:sp-3'.count
    [IO.File]::AppendAllLines($eventLog, [string[]]@(
        '{"kind":"point-blank-kill","source":"live","mode":"mp","weapon":"Signal 50"}',
        '{"kind":"point-blank-kill","source":"live","mode":"mp","weapon":"Mojave .357"}'
    ))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $tracker = Check-Events 200 $native 'local/state'
        if ($tracker.progress.'mojave-357:sp-3'.count -eq 4) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($tracker.progress.'mojave-357:sp-3'.count -ne 4 -or
        $tracker.progress.'signal-50:sp-3'.count -ne $longshotsBefore) {
        throw 'Point Blank must advance Garland once and leave Signal 50 Longshots alone.'
    }
    $tracker = Check-Events 200 $native 'local/state'
    if ($tracker.focus -ne 'mojave-357' -or ($tracker.overview.rows | Where-Object id -eq 'sp-3').count -ne 4) {
        throw 'Tracker overview did not match the overlay.'
    }
    $editUrl = "$baseUrl/local/edit"
    foreach ($increment in 1, 5) {
        $body = @{ weapon = 'mojave-357'; camo = 'sp-3'; increment = $increment } | ConvertTo-Json
        $tracker = Invoke-RestMethod -NoProxy -Uri $editUrl -Method Post -Headers $native -ContentType 'application/json' -Body $body
    }
    if (($tracker.overview.rows | Where-Object id -eq 'sp-3').count -ne 10) {
        throw 'The +1 and +5 controls did not update the overview.'
    }
    foreach ($done in $true, $false) {
        $body = @{ weapon = 'mojave-357'; camo = 'sp-3'; count = 0; done = $done } | ConvertTo-Json
        $tracker = Invoke-RestMethod -NoProxy -Uri $editUrl -Method Post -Headers $native -ContentType 'application/json' -Body $body
        if ($tracker.progress.'mojave-357:sp-3'.done -ne $done) {
            throw 'Tick and untick did not update the saved progress.'
        }
    }
    foreach ($id in 'sp-1', 'sp-2', 'sp-3', 'gold') {
        $body = @{ weapon = 'mojave-357'; camo = $id; count = 0; done = $true } | ConvertTo-Json
        $tracker = Invoke-RestMethod -NoProxy -Uri $editUrl -Method Post -Headers $native -ContentType 'application/json' -Body $body
    }
    $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
    if ($tracker.overview.waiting.id -ne 'platinum' -or $overlay.waiting.id -ne 'platinum' -or
        $overlay.waiting.name -ne 'Platinum' -or !$overlay.waiting.unlock -or $overlay.rows.Count -ne 0 -or
        $overlay.waiting.count -ne 1 -or $overlay.waiting.goal -ne 4) {
        throw 'Tracker and overlay must report the same locked Platinum objective.'
    }
    foreach ($weapon in 'csg-35', 'cronen-osprey', 'mojave-357') {
        $body = @{ weapon = $weapon; camo = 'platinum'; count = 0; done = $true } | ConvertTo-Json
        $tracker = Invoke-RestMethod -NoProxy -Uri $editUrl -Method Post -Headers $native -ContentType 'application/json' -Body $body
    }
    $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
    if ($overlay.waiting.id -ne 'harmonic-break' -or $overlay.waiting.count -ne 1 -or $overlay.waiting.goal -ne 33) {
        throw 'Harmonic Break must count launch-weapon Platinum challenges only.'
    }
    $persisted = Get-Content -LiteralPath (Join-Path $data 'local-tracker.json') -Raw | ConvertFrom-Json
    if ($overlay.overlayLayout -ne '11annah' -or $persisted.overlayLayout -ne '11annah') {
        throw 'Tracker progress edits discarded the selected overlay layout.'
    }
    $tracker = Invoke-RestMethod @layoutArgs -Headers $native -Body '{"layout":"default"}'
    $selectedOverlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
    $currentCatalog = Check-Events 200 $native 'local/catalog'
    if ($tracker.overlayLayout -ne 'default' -or $selectedOverlay.overlayLayout -ne 'default' -or
        $currentCatalog.overlayUrl -ne $catalog.overlayUrl) {
        throw 'Switching back to Default did not retain the existing OBS URL.'
    }
    $pin = @{ weapon = '50-gs' } | ConvertTo-Json
    Invoke-RestMethod -NoProxy -Uri $editUrl -Method Post -Headers $native -ContentType 'application/json' -Body $pin | Out-Null
    [IO.File]::AppendAllLines($eventLog, [string[]]@(
        '{"kind":"weapon","source":"live","mode":"mp","weapon":"Signal 50"}',
        '{"kind":"completionist-unlock","source":"live","mode":"mp","context":"unlock-banner","weapon":"Signal 50","challenge":"Gold"}'
    ))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $tracker = Check-Events 200 $native 'local/state'
        if ($tracker.progress.'50-gs:gold'.done) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$tracker.progress.'50-gs:gold'.done -or $tracker.progress.'signal-50:gold'.done -or
        $tracker.focus -ne 'signal-50') {
        throw 'A generic Gold banner should follow the pinned weapon after the equipped weapon changes.'
    }
    $levelEdit = @{ weapon = 'signal-50'; camo = 'weapon-level'; count = 24 } | ConvertTo-Json
    $tracker = Invoke-RestMethod -NoProxy -Uri $editUrl -Method Post -Headers $native -ContentType 'application/json' -Body $levelEdit
    $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
    if ($tracker.progress.'signal-50:weapon-level'.count -ne 24 -or $overlay.level -ne 24) {
        throw 'The manually entered weapon level did not reach the active overlay.'
    }
    foreach ($edit in @(
        @{ weapon = 'pila' },
        @{ weapon = 'pila'; camo = 'weapon-level'; count = 30 },
        @{ weapon = 'pila'; camo = 'sp-3'; count = 30; done = $true }
    )) {
        Invoke-RestMethod -NoProxy -Uri $editUrl -Method Post -Headers $native -ContentType 'application/json' -Body ($edit | ConvertTo-Json) | Out-Null
    }
    $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
    if ($overlay.minimumLevel -ne 46 -or $null -ne $overlay.level) {
        throw 'A completed level-gated Special camo must establish a minimum above an older exact level.'
    }
    [IO.File]::AppendAllLines($eventLog, [string[]]@(
        '{"kind":"weapon","source":"live","mode":"mp","weapon":"Sang 9mm"}',
        '{"kind":"catalog-special-unlocked","source":"live","mode":"mp","weapon":"Sang 9mm","challenge":"Space Time"}',
        '{"kind":"progress-snapshot","source":"live","mode":"mp","weapon":"Sang 9mm","type":"camo","camo":"Platinum","count":2,"goal":20}'
    ))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $overlay = Check-Events 200 $overlayHeaders 'local/overlay-state'
        $platinum = $overlay.rows | Where-Object id -eq 'platinum'
        if ($overlay.weapon -eq 'Sang 9mm' -and $platinum.count -eq 2) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($overlay.weapon -ne 'Sang 9mm' -or $platinum.count -ne 2 -or $platinum.goal -ne 20) {
        throw 'Sang 9mm Platinum progress must appear as 2/20 even before every Gold is recorded locally.'
    }
    if ($null -ne $overlay.level -or $overlay.minimumLevel -ne 0 -or
        $overlay.minimumUnlocks -ne 37 -or $overlay.maximumCamoUnlocks -ne 37) {
        throw 'Space Time should establish the confirmed cap of 37 weapon unlocks for Sang 9mm.'
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
