$ErrorActionPreference = 'Stop'
$detector = Join-Path (Split-Path $PSScriptRoot -Parent) 'desktop/CamoConnector.Detector'
Add-Type -Path @((Join-Path $detector 'WeaponRecognition.cs'), (Join-Path $detector 'CamoMenuRecognition.cs'),
    (Join-Path $detector 'TrackedCamoRecognition.cs'), (Join-Path $detector 'CombatRecognition.cs'),
    (Join-Path $detector 'CaptureDevices.cs'), (Join-Path $detector 'SpecialUnlocks.cs'))
$reader = [CamoConnector.Detector.WeaponRecognition]
$stability = [CamoConnector.Detector.WeaponConfirmation]::new()
$weapon = $reader::Read(@('SANG 9mm AKIMB0'), $true)
if ($weapon.Name -ne 'Sang 9mm' -or !$weapon.Akimbo) { throw 'Akimbo OCR failed' }
if ($reader::Read(@('Signal 50'), $true)) { throw 'Wide HUD accepted an ordinary name' }
if ($reader::Read(@('Signal 50'), $false).Name -ne 'Signal 50') { throw 'Narrow HUD failed' }
if ($reader::Read(@('KG-7 VU1CAN'), $false).Name -ne 'KG-7 Vulcan') { throw 'Known OCR substitution failed' }
if ($reader::Read(@('.50 6S'), $false).Name -ne '.50 GS' -or
    $reader::Read(@('021 .50 6S'), $false) -or $reader::Read(@('.50 6S'), $true)) {
    throw 'Short pistol HUD OCR matched incorrectly'
}
if ($reader::Read(@('Unknown weapon'), $false)) { throw 'Unlisted OCR text was accepted' }
if (!$reader::IsReplay('FINAL KILL') -or $reader::IsReplay('3 kills remaining')) { throw 'Replay recognition failed' }
if ($stability.Observe($weapon,$false) -or $stability.Observe($weapon,$true) -or
    $stability.Observe($weapon,$false) -or !$stability.Observe($weapon,$false) -or $stability.Observe($weapon,$false)) {
    throw 'Detector emitted duplicate or unconfirmed weapon events'
}
'Passed weapon OCR normalisation, replay rejection and two-read deduplication.'
$badge = $reader::ReadMilitaryUnlock(@('UNLOCKED','SANG 9MM','MILITARY CAMO 7'))
if ($badge.Weapon -ne 'Sang 9mm' -or $badge.Tier -ne 7) { throw 'Camo 7 badge recognition failed' }
$badge = $reader::ReadMilitaryUnlock(@('UNL0CKED','Signal 50','MILITARY CAMO','8'))
if ($badge.Weapon -ne 'Signal 50' -or $badge.Tier -ne 8) { throw 'Split title recognition failed' }
if ($reader::ReadMilitaryUnlock(@('Signal 50','Military Camo 8'))) { throw 'Badge accepted without header' }
if ($reader::ReadMilitaryUnlock(@('Unlocked','Unknown weapon','Military Camo 8'))) { throw 'Unknown weapon attributed' }
if ($reader::ReadMilitaryUnlock(@('Unlocked','Sang 9mm','Military Camo 17'))) { throw 'Invalid tier accepted' }
'Passed verified Military unlock text, split titles and invalid badge rejection.'

$menu = [CamoConnector.Detector.CamoMenuRecognition]
$camos = @('Frosted', 'Splatter Ops', 'Red Tiger')
if (!$menu::IsCamoMenu('CAMO SELECT') -or $menu::IsCamoMenu('CAMOS TRACKED')) { throw 'Camo menu gate failed' }
if (!$menu::IsCamoMenu('CAMO SELECT MW4') -or !$menu::IsMilitaryMenuRow('MILITARY [8/8]') -or
    $menu::IsMilitaryMenuRow('MILITARY [8/9]') -or $menu::ReadProgressionWeapon('50 6S PROGRESSION') -ne '.50 GS') {
    throw 'Camo menu fallback OCR failed'
}
if ($menu::ReadProgressionWeapon('.50 GS PROGRESSION') -ne '.50 GS' -or
    $menu::ReadProgressionWeapon('.08 GS PROGRESSION') -ne '.50 GS' -or
    $menu::ReadProgressionWeapon('.08 GS PROGRESSION MOD')) {
    throw 'The .50 GS progression tab OCR fallback failed'
}
if ($menu::ReadProgressionWeapon('UNKNOWN PROGRESSION')) { throw 'Progression weapon gate failed' }
$redTiger = $menu::ReadProgress('.50 GS','RED-TIGER', @('Complete 3 Special Camos','2/3','Point blank kills','3/30'),$camos)
if ($redTiger.Camo -ne 'Red Tiger' -or $redTiger.Count -ne 3 -or $redTiger.Goal -ne 30) { throw 'Named camo count failed' }
$differentGoal = $menu::ReadProgress('.50 GS','Frosted', @('Different wording 5/10'),$camos)
if ($differentGoal.Camo -ne 'Frosted' -or $differentGoal.Goal -ne 10) { throw 'Objective affected camo identity' }
if ($menu::ReadProgress('.50 GS','Splatter Ops', @('3/30','4/30'),$camos)) { throw 'Conflicting counts were accepted' }
if ($menu::ReadProgress('.50 GS','Military Camo 7', @('3/30'),$camos)) { throw 'Military title accepted as special progress' }
if ($menu::ReadProgress('.50 GS','Gold', @('Weapon level 15/20'),$camos)) { throw 'Prerequisite accepted as camo progress' }
$confirmed = [CamoConnector.Detector.CamoProgressConfirmation]::new()
if ($confirmed.Observe($redTiger) -or !$confirmed.Observe($redTiger) -or $confirmed.Observe($redTiger)) {
    throw 'Camo progress confirmation failed'
}
'Passed menu and weapon gating, name-first progress, conflicting-count rejection, and snapshot deduplication.'
$tracked = [CamoConnector.Detector.TrackedCamoRecognition]
$reference = (Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'special-catalog.json') -Raw | ConvertFrom-Json).'.50 GS'
$catalog = [Collections.Generic.Dictionary[string,CamoConnector.Detector.CamoReference[]]]::new()
$catalog['.50 GS'] = @($reference | ForEach-Object {
    [CamoConnector.Detector.CamoReference]::new($_.name, [Convert]::FromBase64String($_.artworkRGB12))
})
$lines = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('.50 GS', .12)
    [CamoConnector.Detector.HudLine]::new('Red Tiger: different text 3/30', .20)
)
$wrongThumbnail = $catalog['.50 GS'][0].Rgb
$rows = $tracked::ReadRows($lines, $catalog, [Func[double,byte[]]]{ param($y) return ,$wrongThumbnail })
if ($rows.Length -ne 1 -or $rows[0].Camo -ne 'Red Tiger' -or $rows[0].Identity -ne 'name') {
    throw 'A thumbnail overrode a visible camo name.'
}
$lines[1] = [CamoConnector.Detector.HudLine]::new('Unrelated objective text 3/30', .20)
$redReference = ($catalog['.50 GS'] | Where-Object Name -eq 'Red Tiger').Rgb
$rows = $tracked::ReadRows($lines, $catalog, [Func[double,byte[]]]{ param($y) return ,$redReference })
if ($rows.Length -ne 1 -or $rows[0].Camo -ne 'Red Tiger' -or $rows[0].Identity -ne 'artwork') {
    throw 'Thumbnail fallback failed.'
}
$trackedLines = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('Signal 50', .02)
    [CamoConnector.Detector.HudLine]::new('32 / 40', .22)
    [CamoConnector.Detector.HudLine]::new('.50 GS', .34)
    [CamoConnector.Detector.HudLine]::new('Get 20 Headshot Kills against Players', .42)
    [CamoConnector.Detector.HudLine]::new('8 / 20 1000', .56)
    [CamoConnector.Detector.HudLine]::new('Mojave .357', .68)
)
$counterLines = [CamoConnector.Detector.HudLine[]]@([CamoConnector.Detector.HudLine]::new('8 / 20', .56))
$rows = $tracked::ReadRows($trackedLines, $catalog, [Func[double,byte[]]]{ param($y) return ,$wrongThumbnail }, $counterLines)
if ($rows.Length -ne 1 -or $rows[0].Camo -ne 'Frosted' -or $rows[0].Count -ne 8 -or $rows[0].Goal -ne 20 -or
    $rows[0].Identity -ne 'artwork') {
    throw 'The counter-only pass did not recover the .50 GS row independently.'
}
$ambiguous = [CamoConnector.Detector.CamoReference[]]@(
    [CamoConnector.Detector.CamoReference]::new('A', $redReference)
    [CamoConnector.Detector.CamoReference]::new('B', $redReference)
)
if ($tracked::MatchArtwork($redReference, $ambiguous)) { throw 'An ambiguous thumbnail was accepted.' }
'Passed tracked-panel name precedence, thumbnail fallback and ambiguity rejection.'
$combat = [CamoConnector.Detector.CombatRecognition]
$state = [CamoConnector.Detector.CombatConfirmation]::new()
$now = [DateTimeOffset]::UtcNow
$first = $combat::Read('KILL','','1ST KILL')
if ($state.Observe($first,$now)[0].Kills -ne 1 -or $state.Observe($first,$now.AddSeconds(1)).Length) {
    throw 'Repeated kill banner was counted again.'
}
$second = $combat::Read('KILL','','2ND KILL')
if ($state.Observe($second,$now.AddSeconds(2))[0].KillStreak -ne 2) { throw 'Changed kill ordinal was missed.' }
$dead = $combat::Read('','KILLED BY','')
$entry = $state.Observe($dead,$now.AddSeconds(3))[0]
if ($entry.EndedStreak -ne 2 -or $entry.KillStreak -ne 0 -or $entry.Deaths -ne 1) { throw 'Death did not end the streak.' }
$state.Reset()
if ($state.Kills -or $state.Deaths -or $state.Best) { throw 'Match reset left counters behind.' }
if ($combat::ReadLobby('VICTORY') -or $combat::ReadLobby('VICTORY SCORE LIMIT REACHED') -ne 'match-ended') {
    throw 'Victory end detection ignored required context.'
}
if (!$combat::IsLongshot('L0NG-SHOT') -or $combat::IsLongshot('Longshotting') -or
    !$combat::HasCompletionistGold('COMPLETIONIST [1/4]') -or $combat::HasCompletionistGold('COMPLETIONIST [0/4]')) {
    throw 'Medal or completionist parsing failed.'
}
'Passed combat banner deduplication, streak resets, match end, longshots and completionist menu parsing.'
if (!$combat::IsGoldBanner('CHALLENGE COMPLETED','Get 15 player kills in a single match',$false) -or
    !$combat::IsGoldBanner('', 'moe! 15 PLAYER KILES) IN A SINGLE MATCH', $true) -or
    $combat::IsGoldBanner('', 'moe! 15 PLAYER KILES) IN A SINGLE MATCH', $false) -or
    !$combat::IsGoldBanner('', 'Get 15 player in a single match', $true) -or
    $combat::IsGoldBanner('', 'Get 15 player kills in a single match', $false) -or
    $combat::IsGoldBanner('CHALLENGE COMPLETED','Get 15 longshot kills',$true)) {
    throw 'Gold completion evidence checks failed.'
}
'Passed Gold banner detection and rejection without matching completion evidence.'
$listing = @'
[dshow @ 1] "Elgato 4K X" (video)
[dshow @ 1] Alternative name "@device_pnp_elgato"
[dshow @ 1] "Microphone" (audio)
[dshow @ 1] Alternative name "@device_pnp_mic"
[dshow @ 1] "OBS Virtual Camera" (none)
'@
$devices = [CamoConnector.Detector.CaptureDevices]::Parse($listing)
if ($devices.Length -ne 2 -or $devices[0].Input -ne '@device_pnp_elgato' -or
    $devices[1].Input -ne 'OBS Virtual Camera') { throw 'DirectShow capture device enumeration failed.' }
'Passed DirectShow device listing, stable identifiers and audio filtering.'
$specials = [CamoConnector.Detector.SpecialUnlocks]
$camos = [CamoConnector.Detector.CamoReference[]]@(
    [CamoConnector.Detector.CamoReference]::new('A', $null, 1, 25)
    [CamoConnector.Detector.CamoReference]::new('B', $null, 2, 15)
    [CamoConnector.Detector.CamoReference]::new('C', $null, 3, 30)
)
if ($specials::ReadCount('SPECIAL [2/3]') -ne 2) { throw 'Special progress heading failed' }
$unlock = $specials::Resolve('Krait P68',2,[double[]]@(.3,.2,.8),$camos)
if (($unlock.Challenge -join ',') -ne 'A,B' -or $unlock[0].Kind -ne 'catalog-special-unlocked') { throw 'Wrong Special slots applied' }
if ($specials::Resolve('Krait P68',2,[double[]]@(.3,.60,.8),$camos).Length -or
    $specials::Resolve('Krait P68',2,[double[]]@(.3,.19,.2),$camos).Length) { throw 'Ambiguous Special locks accepted' }
$unlock = $specials::Resolve('Sang 9mm',3,[double[]]@(),$camos)
if (($unlock.Challenge -join ',') -ne 'menu-autumn,menu-snakeskin,menu-space-time') { throw 'Sang menu unlock mapping failed' }
$confirmation = [CamoConnector.Detector.SpecialUnlockConfirmation]::new()
if ($confirmation.Observe($unlock).Length -or $confirmation.Observe($unlock).Length -ne 3 -or
    $confirmation.Observe($unlock).Length) { throw 'Special confirmation did not require two reads or deduplicate' }
'Passed Special count, padlock ambiguity, menu event mapping and two-scan confirmation.'
if ($specials::ReadBanner('CHALLENGE COMPLETED','25 player kills while aiming down sights') -ne 'ads-25' -or
    $specials::ReadBanner('CHALLENGE COMPLETED','3 player kills without dying 5 times') -ne 'streak-3x5' -or
    $specials::ReadBanner('CHALLENGE COMPLETED','50 player kills while using Akimbo') -ne 'menu-space-time' -or
    $specials::ReadBanner('','25 player kills while aiming down sights')) { throw 'Special banner checks failed' }
'Passed named Special completion banner rules.'
