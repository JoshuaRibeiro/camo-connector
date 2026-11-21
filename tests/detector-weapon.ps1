$ErrorActionPreference = 'Stop'
$detector = Join-Path (Split-Path $PSScriptRoot -Parent) 'desktop/CamoConnector.Detector'
Add-Type -Path @((Join-Path $detector 'WeaponRecognition.cs'), (Join-Path $detector 'CamoMenuRecognition.cs'),
    (Join-Path $detector 'TrackedCamoRecognition.cs'), (Join-Path $detector 'CombatRecognition.cs'),
    (Join-Path $detector 'CaptureDevices.cs'), (Join-Path $detector 'SpecialUnlocks.cs'))
$reader = [CamoConnector.Detector.WeaponRecognition]
$levelUp = $reader::ReadLevelUp('WEAPON LEVELED UP', 'PM-40 ANVIL', '10')
if ($levelUp.Weapon -ne 'PM-40 Anvil' -or $levelUp.Level -ne 10 -or
    $reader::ReadLevelUp('NEW UNLOCK', 'SIGNAL 50', '31').Level -ne 31 -or
    $reader::ReadLevelUp('NEW UNLOCK', 'SIGNAL 50', 'MILITARY CAMO 2') -or
    $reader::ReadLevelUp('WEAPON LEVEL UP', '', '10')) {
    throw 'Weapon level banner did not require both an identified weapon and an explicit level.'
}
$menuReader = [CamoConnector.Detector.CamoMenuRecognition]
$militaryLevel = $menuReader::ReadMilitaryLevel('PM-40 Anvil', 'MILITARY CAMO 2', "Reach Weapon Level 11`n9 / 11")
if ($militaryLevel.Count -ne 9 -or $militaryLevel.Goal -ne 11 -or
    $menuReader::ReadMilitaryLevel('PM-40 Anvil', 'MILITARY CAMO 2', "Reach Weapon Level 11`n9 / 18") -or
    $menuReader::ReadMilitaryLevel('PM-40 Anvil', 'MILITARY CAMO 2', "Earn 11 weapon unlocks`n9 / 11")) {
    throw 'The Military camo menu did not distinguish weapon levels from unlock totals.'
}
$stability = [CamoConnector.Detector.WeaponConfirmation]::new()
$weapon = $reader::Read(@('SANG 9mm AKIMB0'), $true)
if ($reader::ReadHud(@('.58 GS'), $false).Name -ne '.50 GS' -or
    $reader::ReadHud(@('021 .58 GS'), $false) -or $reader::ReadHud(@('.58 GS'), $true)) {
    throw 'The .50 GS HUD fallback accepted an ambiguous read or missed the short label.'
}
if ($reader::ReadHud(@('Krait P68 Commando'), $false).Name -ne 'Krait P68' -or
    $reader::ReadHud(@('Krait P68 Comm'), $false).Name -ne 'Krait P68' -or
    $reader::ReadHud(@('Krait P68 Commando'), $true) -or
    $reader::ReadHud(@('Pick up Krait P68 Commando'), $false)) {
    throw 'Krait P68 Commando must only match the active weapon HUD label.'
}
if ($weapon.Name -ne 'Sang 9mm' -or !$weapon.Akimbo) { throw 'Akimbo OCR failed' }
if ($reader::Read(@('Signal 50'), $true)) { throw 'Wide HUD accepted an ordinary name' }
if ($reader::Read(@('Signal 50'), $false).Name -ne 'Signal 50') { throw 'Narrow HUD failed' }
if ($reader::Read(@('KG-7 VU1CAN'), $false).Name -ne 'KG-7 Vulcan') { throw 'Known OCR substitution failed' }
if ($reader::ReadHud(@('KG-7 Drillcharge'), $false).Name -ne 'KG-7 Vulcan' -or
    $reader::ReadHud(@('KG-7'), $false).Name -ne 'KG-7 Vulcan' -or
    $reader::ReadHud(@('KG-7 Drillcharge'), $true) -or
    $reader::ReadHud(@('RPG-8'), $false).Name -ne 'RPG-8') {
    throw 'KG-7 Apex HUD alias did not preserve weapon identity'
}
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
$clock = [DateTimeOffset]::UtcNow
$pistol = $reader::Read(@('.SO 6S'), $false)
$astaroth = $reader::Read(@('Astaroth .57'), $false)
$stability.Reset()
[void]$stability.Observe($astaroth,$false,$clock)
[void]$stability.Observe($astaroth,$false,$clock.AddMilliseconds(250))
if ($stability.Observe($pistol,$false,$clock.AddMilliseconds(500)) -or
    $stability.Observe($null,$false,$clock.AddMilliseconds(750)) -or
    $stability.Observe($pistol,$false,$clock.AddSeconds(1)).Name -ne '.50 GS') {
    throw 'Weapon switch failed across a brief HUD dropout'
}
$stability.Reset()
[void]$stability.Observe($pistol,$false,$clock)
if ($stability.Observe($pistol,$false,$clock.AddSeconds(2))) { throw 'Stale HUD read confirmed a weapon' }
$badge = $reader::ReadMilitaryUnlock(@('UNLOCKED','SANG 9MM','MILITARY CAMO 7'))
if ($badge.Weapon -ne 'Sang 9mm' -or $badge.Tier -ne 7) { throw 'Camo 7 badge recognition failed' }
$badge = $reader::ReadMilitaryUnlock(@('UNL0CKED','Signal 50','MILITARY CAMO','8'))
if ($badge.Weapon -ne 'Signal 50' -or $badge.Tier -ne 8) { throw 'Split title recognition failed' }
$badge = $reader::ReadMilitaryUnlock(@('UNLOCKED }','RPCB','MILITARY CAMO 4'))
if ($badge.Weapon -ne 'RPG-8' -or $badge.Tier -ne 4 -or
    $reader::ReadMilitaryUnlock(@('UNLOCKED','RPCB','MILITARY CAMO 9'))) {
    throw 'RPG-8 badge OCR variant must require a valid Military tier'
}
if ($reader::ReadMilitaryUnlock(@('Signal 50','Military Camo 8'))) { throw 'Badge accepted without header' }
if ($reader::ReadMilitaryUnlock(@('Unlocked','Unknown weapon','Military Camo 8'))) { throw 'Unknown weapon attributed' }
if ($reader::ReadMilitaryUnlock(@('Unlocked','Sang 9mm','Military Camo 17'))) { throw 'Invalid tier accepted' }
$summary = $reader::ReadMilitarySummary('Top Performing Weapon', 'PILA', 'Military Camo 2')
if ($summary.Weapon -ne 'Pila' -or $summary.Tier -ne 2 -or
    $reader::ReadMilitarySummary('Top Performing Weapon', 'PILA', 'LEVEL 12') -or
    $reader::ReadMilitarySummary('Match Summary', 'PILA', 'Military Camo 2') -or
    $reader::ReadMilitarySummary('Top Performing Weapon', 'Unknown', 'Military Camo 2')) {
    throw 'Military summary accepted an unverified weapon or tier.'
}
'Passed verified Military unlock text, split titles and invalid badge rejection.'

$menu = [CamoConnector.Detector.CamoMenuRecognition]
$camos = @('Frosted', 'Splatter Ops', 'Red Tiger')
if ($menu::ReadKnownCamoTitle("ID 034`n+ FROSTED", $camos) -ne 'Frosted' -or
    $null -ne $menu::ReadKnownCamoTitle("FROSTED`nRED TIGER", $camos)) {
    throw 'Hovered camo title was missed or ambiguous'
}
if (!$menu::IsCamoMenu('CAMO SELECT') -or $menu::IsCamoMenu('CAMOS TRACKED')) { throw 'Camo menu gate failed' }
if (!$menu::IsCamoMenu('CAMO SELECT MW4') -or $menu::ReadMilitaryMenuCount('MILITARY [8/8]') -ne 8 -or
    $menu::ReadMilitaryMenuCount('MILITARY [2 / 8]') -ne 2 -or $menu::ReadMilitaryMenuCount('MILITARY [0/8]') -ne 0 -or
    $null -ne $menu::ReadMilitaryMenuCount('MILITARY [8/9]') -or
    $null -ne $menu::ReadMilitaryMenuCount('SPECIAL [2/3]') -or $menu::ReadProgressionWeapon('50 6S PROGRESSION') -ne '.50 GS') {
    throw 'Camo menu fallback OCR failed'
}
if ($menu::ConfirmsMilitaryCounter(6, '[0/8]') -or $menu::ConfirmsMilitaryCounter(8, 'L0/8]') -or
    !$menu::ConfirmsMilitaryCounter(6, '[6/8]') -or !$menu::ConfirmsMilitaryCounter(1, 'L1/8)')) {
    throw 'Military tier confirmation accepted a mismatched numerator or rejected a valid count'
}
if ($menu::ReadProgressionWeapon('.50 GS PROGRESSION') -ne '.50 GS' -or
    $menu::ReadProgressionWeapon('.08 GS PROGRESSION') -ne '.50 GS' -or
    $menu::ReadProgressionWeapon('.08 GS PROGRESSION MOD')) {
    throw 'The .50 GS progression tab OCR fallback failed'
}
$progressionReads = @{
    'SIGNAL 5@ PROGRESSION' = 'Signal 50'
    'SIGNAL 58 PROGRESSION' = 'Signal 50'
    'RPG-8 PROGRESSION | Move' = 'RPG-8'
    'RPG-8 PROGRESSION =f' = 'RPG-8'
    'PM-4@ ANVIL PROGRESSION MODE MAST' = 'PM-40 Anvil'
    'PM-48 ANVIL PROGRESSION' = 'PM-40 Anvil'
    'TSO NIGHTSHADE PROGRESSION' = 'ISO Nightshade'
    '| FSS CARBON EDGE 1695 PROGRESSION fi' = 'FSS Carbon Edge 1095'
    'FSS CARBON EDGE 1895 PROGRESSION' = 'FSS Carbon Edge 1095'
    '-58 GS PROGRESSION' = '.50 GS'
}
foreach ($read in $progressionReads.GetEnumerator()) {
    if ($menu::ReadProgressionWeapon($read.Key) -cne $read.Value) {
        throw "Incorrect camo progression weapon for $($read.Key)"
    }
}
if ($menu::ReadProgressionWeapon('UNKNOWN PROGRESSION')) { throw 'Progression weapon gate failed' }
if ($menu::ReadProgressionWeapon('SIGNAL 50 PROGRESSION MODE MASTERY') -ne 'Signal 50' -or
    $menu::ReadProgressionWeapon('UNKNOWN PROGRESSION MODE MASTERY')) {
    throw 'Neighboring menu tab affected weapon identity'
}
if ($menu::ReadBreadcrumbWeapon('CREATE A CLASS / EDIT CUSTOM CLASS / SIGNAL 5O /') -ne 'Signal 50' -or
    $menu::ReadBreadcrumbWeapon('CREATE A CLASS / EDIT CUSTOM CLASS / FSS CARBON EDGE 1095') -ne 'FSS Carbon Edge 1095' -or
    $menu::ReadBreadcrumbWeapon('CREATE A CLASS / EDIT CUSTOM CLASS / .50 GS /') -ne '.50 GS' -or
    $menu::ReadBreadcrumbWeapon('CREATE A CLASS / EDIT CUSTOM CLASS / UNKNOWN /') -or
    $menu::ReadBreadcrumbWeapon('LOADOUT / EDIT CUSTOM CLASS / SIGNAL 50 /')) {
    throw 'Camo menu breadcrumb recognition failed'
}
$optical = $menu::ReadKnownCamoTitle('6 OPTICAL-REVERB', @('Oni','Urban Smoke','Optical Reverb'))
if ($optical -ne 'Optical Reverb' -or
    $menu::ReadProgress('Cronen Squall', $optical, @('2/15'), @($optical)).Count -ne 2) {
    throw 'Lock glyph corrupted the Optical Reverb identity'
}
$redTiger = $menu::ReadProgress('.50 GS','RED-TIGER', @('Complete 3 Special Camos','2/3','Point blank kills','3/30'),$camos)
if ($redTiger.Camo -ne 'Red Tiger' -or $redTiger.Count -ne 3 -or $redTiger.Goal -ne 30) { throw 'Named camo count failed' }
$pistolPlatinum = $menu::ReadProgress('Sang 9mm', 'PLATINUM', @(
    'Complete 4 Gold Camo Challenges using Pistols', 'Get 20 Player Kills from Behind', '2/20'
), @('Gold', 'Platinum', 'Harmonic Break', 'Divergence'))
$pistolCounter = $menu::ReadProgress('Sang 9mm', 'PLATINUM', @('2/20'), @('Platinum'))
if ($pistolPlatinum.Camo -ne 'Platinum' -or $pistolPlatinum.Count -ne 2 -or
    $pistolPlatinum.Goal -ne 20 -or !$menu::ConfirmCounter($pistolPlatinum, $pistolCounter, $null)) {
    throw 'Sang 9mm Platinum must confirm the 2/20 objective counter.'
}
$differentGoal = $menu::ReadProgress('.50 GS','Frosted', @('Different wording 5/10'),$camos)
if ($differentGoal.Camo -ne 'Frosted' -or $differentGoal.Goal -ne 10) { throw 'Objective affected camo identity' }
if ($menu::ReadProgress('.50 GS','Splatter Ops', @('3/30','4/30'),$camos)) { throw 'Conflicting counts were accepted' }
if ($menu::ReadProgress('.50 GS','Military Camo 7', @('3/30'),$camos)) { throw 'Military title accepted as special progress' }
if ($menu::ReadProgress('.50 GS','Gold', @('Weapon level 15/20'),$camos)) { throw 'Prerequisite accepted as camo progress' }
$ten = $menu::ReadProgress('KG-7 Vulcan', 'Tension', @('10/20'), @('Tension'))
foreach ($misread in '16/20', '18/20', '10/26') {
    $wrong = $menu::ReadProgress('KG-7 Vulcan', 'Tension', @($misread), @('Tension'))
    if ($menu::ConfirmCounter($wrong, $ten, $ten)) { throw "Disagreement accepted: $misread" }
}
if ($menu::ConfirmCounter($ten, $ten, $null).Count -ne 10 -or
    $menu::ConfirmCounter($null, $ten, $ten).Goal -ne 20 -or
    $menu::ConfirmCounter($null, $ten, $null)) { throw 'Dedicated camo counter confirmation failed' }
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
$lines[1] = [CamoConnector.Detector.HudLine]::new('Get 75 kills while prone 19/75', .20)
$rows = $tracked::ReadRows($lines, $catalog, [Func[double,byte[]]]{ param($y) return ,$wrongThumbnail })
if ($rows.Length -ne 1 -or $rows[0].Camo -ne 'Platinum') { throw 'Platinum was attributed to a Special camo.' }
$lines[1] = [CamoConnector.Detector.HudLine]::new('19/75', .20)
if ($tracked::ReadRows($lines, $catalog, [Func[double,byte[]]]{ param($y) return ,$wrongThumbnail })) {
    throw 'Unverified 75-kill progress was attributed by Special artwork alone.'
}
$catalog['Signal 50'] = @()
$signalLobby = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('SIGNAL 58 LA5T U5ED', .04)
    [CamoConnector.Detector.HudLine]::new('Get 75 Player Kills whi le Pr0ne', .10)
    [CamoConnector.Detector.HudLine]::new('29 / 75', .21)
    [CamoConnector.Detector.HudLine]::new('PILA', .30)
    [CamoConnector.Detector.HudLine]::new('Reach Weapon Level 31', .38)
    [CamoConnector.Detector.HudLine]::new('29 / 31', .47)
)
$rows = $tracked::ReadRows($signalLobby, $catalog, [Func[double,byte[]]]{ param($y) return $null })
if ($rows.Length -ne 2 -or $rows[0].Weapon -ne 'Signal 50' -or $rows[0].Camo -ne 'Platinum' -or
    $rows[0].Count -ne 29 -or $rows[0].Goal -ne 75 -or $rows[1].Weapon -ne 'Pila') {
    throw 'The lobby failed to associate Signal 50 Platinum with its own 29/75 counter.'
}
$firstCard = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('| SIGNAL SO', 0)
    [CamoConnector.Detector.HudLine]::new('Get 75 Player Kills while Prone', .12)
)
$firstCounter = [CamoConnector.Detector.HudLine[]]@([CamoConnector.Detector.HudLine]::new('51 / 75', .22))
$rows = $tracked::ReadRows($firstCard, $catalog, [Func[double,byte[]]]{ param($y) return $null }, $firstCounter)
if ($rows.Length -ne 1 -or $rows[0].Weapon -ne 'Signal 50' -or
    $rows[0].Camo -ne 'Platinum' -or $rows[0].Count -ne 51) {
    throw 'The isolated first lobby card failed to recover Signal 50 Platinum from SIGNAL SO.'
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
    $rows[0].Identity -ne 'name') {
    throw 'The counter-only pass did not recover the .50 GS row independently.'
}
$ambiguous = [CamoConnector.Detector.CamoReference[]]@(
    [CamoConnector.Detector.CamoReference]::new('A', $redReference)
    [CamoConnector.Detector.CamoReference]::new('B', $redReference)
)
if ($tracked::MatchArtwork($redReference, $ambiguous)) { throw 'An ambiguous thumbnail was accepted.' }
$pauseLines = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('.50 GS LAST USED', .02)
    [CamoConnector.Detector.HudLine]::new('Get 20 Headshot Kills against Players', .10)
    [CamoConnector.Detector.HudLine]::new('18 / 20', .23)
    [CamoConnector.Detector.HudLine]::new('.50 GS LAST USED', .33)
    [CamoConnector.Detector.HudLine]::new('Get 30 Point Blank Kills against Players', .42)
    [CamoConnector.Detector.HudLine]::new('MOJAVE .357', .65)
)
$pauseCounters = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('18 / 20', .23)
    [CamoConnector.Detector.HudLine]::new('16 / 30', .54)
)
$pauseThumbnail = [Func[double,byte[]]]{ param($y) if ($y -lt .3) { return ,$wrongThumbnail }; return ,$redReference }
$primary = $tracked::ReadRows($pauseLines, $catalog, $pauseThumbnail)
$fallback = $tracked::ReadRows($pauseLines, $catalog, $pauseThumbnail, $pauseCounters)
$merged = $tracked::MergeRows($primary, $fallback)
if ($primary.Length -ne 1 -or $merged.Length -ne 2 -or
    ($merged | Where-Object Camo -eq Frosted).Count -ne 18 -or
    ($merged | Where-Object Camo -eq 'Red Tiger').Count -ne 16) {
    throw 'Pause menu lost a tracked row for a repeated LAST USED weapon'
}
$lobbyLines = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('.50 GS', .03)
    [CamoConnector.Detector.HudLine]::new('Get 20 Headshot Kills against', .11)
    [CamoConnector.Detector.HudLine]::new('Players', .18)
    [CamoConnector.Detector.HudLine]::new('19 / 20', .24)
    [CamoConnector.Detector.HudLine]::new('MOJAVE .357', .33)
    [CamoConnector.Detector.HudLine]::new('Earn 1 more weapon unlock', .40)
    [CamoConnector.Detector.HudLine]::new('FSS CARBON EDGE 1095', .61)
    [CamoConnector.Detector.HudLine]::new('Reach Weapon Level 16', .69)
    [CamoConnector.Detector.HudLine]::new('12 / 16', .82)
)
$lobby = $tracked::ReadRows($lobbyLines, $catalog, [Func[double,byte[]]]{ param($y) return ,$redReference })
$platinumReference = [CamoConnector.Detector.CamoReference]::new('Platinum', $null, 0, 20, $null)
$platinumReference.Objective = 'Get 20 Player Kills from Behind'
$pinnedCatalog = [System.Collections.Generic.Dictionary[string,CamoConnector.Detector.CamoReference[]]]::new()
$pinnedCatalog.Add('Sang 9mm', [CamoConnector.Detector.CamoReference[]]@($platinumReference))
$rightPanel = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('CRONEN OSPREY', .06)
    [CamoConnector.Detector.HudLine]::new('Earn 4 more weapon unlocks', .13)
    [CamoConnector.Detector.HudLine]::new('SANG 9MM', .33)
    [CamoConnector.Detector.HudLine]::new('Get 20 Player Kills from Behind', .39)
    [CamoConnector.Detector.HudLine]::new('5 / 20', .50)
    [CamoConnector.Detector.HudLine]::new('JOKR', .60)
    [CamoConnector.Detector.HudLine]::new('Reach Weapon Level 6', .66)
    [CamoConnector.Detector.HudLine]::new('1 / 6', .78)
)
$pinned = $tracked::ReadRows($rightPanel, $pinnedCatalog, [Func[double,byte[]]]{ param($y) return $null })
if ($pinned.Length -ne 2 -or $pinned[0].Camo -ne 'Platinum' -or
    $pinned[0].Count -ne 5 -or $pinned[0].Goal -ne 20 -or $pinned[1].Count -ne 1) {
    throw 'Right CAMOS panel must match Sang Platinum from its objective and keep JOKR separate.'
}
$leftPanel = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('yp SANG SMM', .30)
    [CamoConnector.Detector.HudLine]::new('Bat 20 Player Kills rom Benird', .36)
    [CamoConnector.Detector.HudLine]::new('14 / 26', .46)
    [CamoConnector.Detector.HudLine]::new('CRONEN OSPREY', .56)
)
$pinned = $tracked::ReadRows($leftPanel, $pinnedCatalog, [Func[double,byte[]]]{ param($y) return $null })
if ($pinned.Length -ne 1 -or $pinned[0].Weapon -ne 'Sang 9mm' -or
    $pinned[0].Camo -ne 'Platinum' -or $pinned[0].Count -ne 14 -or $pinned[0].Goal -ne 20) {
    throw 'Left CAMOS panel must recover Sang 9mm Platinum 14/20 from the corroborating objective.'
}
$pinnedCatalog.Add('Mojave .357', [CamoConnector.Detector.CamoReference[]]@($platinumReference))
$mojaveLobby = [CamoConnector.Detector.HudLine[]]@(
    [CamoConnector.Detector.HudLine]::new('} MOJAVE 35?', .31)
    [CamoConnector.Detector.HudLine]::new("Get 20 Player Kills 'rom Beaird", .39)
    [CamoConnector.Detector.HudLine]::new('7/28', .49)
)
$pinned = $tracked::ReadRows($mojaveLobby, $pinnedCatalog, [Func[double,byte[]]]{ param($y) return $null })
if ($pinned.Length -ne 1 -or $pinned[0].Weapon -ne 'Mojave .357' -or
    $pinned[0].Camo -ne 'Platinum' -or $pinned[0].Count -ne 7 -or $pinned[0].Goal -ne 20) {
    throw 'Mojave lobby OCR must recover Platinum 7/20.'
}
if ($lobby.Length -ne 2 -or $lobby[0].Camo -ne 'Frosted' -or $lobby[0].Count -ne 19 -or
    $lobby[1].WeaponLevelObjective -ne 'Reach Weapon Level 16' -or $lobby[1].Count -ne 12) {
    throw 'Lobby counters did not distinguish Special camo progress from weapon levels'
}
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
if ($combat::ReadLobby('SEARCHING FOR A MATCH 1-21MS PING') -ne 'match-ended' -or
    $combat::ReadLobby('MATCH IN PROGRESS')) {
    throw 'Matchmaking lobby must reset combat counters without mistaking the in-match pause screen for a lobby.'
}
if (!$combat::IsLongshot('L0NG-SHOT') -or $combat::IsLongshot('Longshotting') -or
    !($combat::ReadCompletionistCount('COMPLETIONIST [1/4]') -eq 1 -and
        $combat::ReadCompletionistCount('COMPLETIONIST [2/4]') -eq 2 -and
        $combat::ReadCompletionistCount('COMPLETIONIST [0/4]') -eq 0)) {
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
$combat = [CamoConnector.Detector.CombatRecognition]
$swatch = [Convert]::FromBase64String('q6CalpCNgnRrbGxrp5uSjYuLgnd0cGdZm5aQioOGgnp2cmhPmJCNi4Z/e3BsamFGlIuGhn9yaGBdV05Chn9xbGliWVZSTUdBdWtlYl9VU1BLSkNAbGdfWlxWV09LSUVHZ2dgWFhXVVJNSUlGYV1dWlhWU1NRTUpGYFtdWldXVFFRTktLX11cWllYVVNQTk5K')
[byte[]]$platinum = foreach ($value in $swatch) { $value; $value; $value }
[byte[]]$flatGray = @(100) * 432
[byte[]]$gradient = foreach ($index in 0..143) {
    $value = 130 - 4 * [Math]::Floor($index / 12) - 3 * ($index % 12)
    $value; $value; $value
}
if (!$combat::IsPlatinumArtwork($platinum) -or $combat::IsPlatinumArtwork($flatGray) -or
    $combat::IsPlatinumArtwork($gradient)) { throw 'Platinum artwork texture check failed' }
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
if ($specials::Resolve('Krait P68',3,[double[]]@(.3,.2,.8),$camos).Length -or
    $specials::Resolve('Krait P68',0,[double[]]@(.3,.2,.1),$camos).Length) { throw 'Contradictory Special count accepted' }
$unlock = $specials::Resolve('Sang 9mm',3,[double[]]@(.3,.2,.1),$camos)
if (($unlock.Challenge -join ',') -ne 'menu-autumn,menu-snakeskin,menu-space-time') { throw 'Sang menu unlock mapping failed' }
$confirmation = [CamoConnector.Detector.SpecialUnlockConfirmation]::new()
if ($confirmation.Observe($unlock).Length -or $confirmation.Observe($unlock).Length -ne 3 -or
    $confirmation.Observe($unlock).Length) { throw 'Special confirmation did not require two reads or deduplicate' }
$catalog = Get-Content (Join-Path $PSScriptRoot '../special-catalog.json') -Raw | ConvertFrom-Json
$osprey = [CamoConnector.Detector.CamoReference[]]@(
    foreach ($entry in $catalog.'Cronen Osprey') {
        [CamoConnector.Detector.CamoReference]::new($entry.name, $null, $entry.slot, $entry.goal)
    }
)
$unlocks = $specials::Resolve('Cronen Osprey', 3, [double[]]@(.3,.2,.1), $osprey)
if (($unlocks.Challenge -join ',') -ne 'In the Weeds,Dark Ichor,Chained') {
    throw 'Cronen Osprey Specials were dropped when their numeric goals were absent'
}
$partial = $specials::Resolve('Cronen Osprey', 1, [double[]]@(.8,.3,.8), $osprey)
if (($partial.Challenge -join ',') -ne 'Dark Ichor') { throw 'Partial Cronen Osprey unlock mapped to the wrong slot' }
'Passed Special count, padlock ambiguity, menu event mapping and two-scan confirmation.'
if ($specials::ReadBanner('CHALLENGE COMPLETED','25 player kills while aiming down sights') -ne 'ads-25' -or
    $specials::ReadBanner('CHALLENGE COMPLETED','3 player kills without dying 5 times') -ne 'streak-3x5' -or
    $specials::ReadBanner('CHALLENGE COMPLETED','50 player kills while using Akimbo') -ne 'menu-space-time' -or
    $specials::ReadBanner('','25 player kills while aiming down sights')) { throw 'Special banner checks failed' }
$pistolSpecials = [CamoConnector.Detector.CamoReference[]]@(
    foreach ($entry in $catalog.'.50 GS') {
        [CamoConnector.Detector.CamoReference]::new($entry.name, $null, $entry.slot, $entry.goal)
    }
)
if ($specials::ReadNamedBanner([string[]]@('SPLATTER OPS'), $pistolSpecials) -ne 'Splatter Ops' -or
    $specials::ReadNamedBanner([string[]]@('SPLATTER OPS','FROSTED'), $pistolSpecials)) {
    throw 'Named Special badge recognition failed'
}
'Passed named Special completion banner rules.'

$garland = $menuReader::ReadProgress('Mojave .357', 'GARLAND', @(
    'Earn 43 more weapon unlocks',
    'Get 30 Point Blank Kills against Players',
    '3/30'
), @('Miami', 'Wallpaper', 'Garland'))
if ($garland.Weapon -ne 'Mojave .357' -or $garland.Camo -ne 'Garland' -or $garland.Count -ne 3 -or $garland.Goal -ne 30) {
    throw 'Garland should report 3/30 point-blank kills from the camo menu.'
}
$catalog = Get-Content "$PSScriptRoot/../special-catalog.json" -Raw | ConvertFrom-Json
if (($catalog.'Mojave .357' | Where-Object name -eq 'Garland').goal -ne 30) {
    throw 'Garland should have a 30-kill catalog goal.'
}

$struckOil = $menuReader::ReadProgress('Rezi 12', 'STRUCK OIL', @(
    'Earn 21 more weapon unlocks', 'Get 20 Point Blank Kills against Players', '16/20'
), @('Struck Oil', 'Brushstroke'))
if ($struckOil.Camo -ne 'Struck Oil' -or $struckOil.Count -ne 16 -or $struckOil.Goal -ne 20) {
    throw 'Struck Oil should use the 16/20 objective counter.'
}

$brushstrokeObjective = 'Get 10 Longshot Kills against Players'
$brushstrokeWide = $menuReader::ReadProgress('Rezi 12', 'Brushstroke', @($brushstrokeObjective, '1/18'), @('Brushstroke'))
$brushstrokeTight = $menuReader::ReadProgress('Rezi 12', 'Brushstroke', @('1/10'), @('Brushstroke'))
$brushstrokeWide = $menuReader::CheckObjectiveGoal($brushstrokeWide, 10, $brushstrokeObjective)
if ($menuReader::ConfirmCounter($brushstrokeWide, $brushstrokeTight, $null).Count -ne 1) {
    throw 'Brushstroke OCR reads must agree after goal correction.'
}
