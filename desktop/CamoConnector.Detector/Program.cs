using System.Text.Json;
using CamoConnector.Detector;

var listDevices = args.Length == 1 && args[0] == "--list-devices";
var previewOnly = args.Length == 3 && args[0] is "--preview-monitor" or "--preview-device";
var publishPreview = args.Length == 4 && args[0] is "--monitor" or "--device";
if (!listDevices && !previewOnly && !publishPreview &&
    (args.Length != 3 || args[0] is not ("--image" or "--monitor" or "--device" or "--device-frame" or "--video")))
{
    Console.Error.WriteLine("CamoConnector.Detector --list-devices | (--image <capture> | --video <file> | --monitor <index> | --device <input> | --device-frame <input>) <data-directory>");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    var ffmpeg = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Capture", "ffmpeg.exe"));
    if (listDevices || args[0] is "--device" or "--device-frame" or "--video" or "--preview-device")
    {
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("Missing bundled FFmpeg: " + ffmpeg);
        if (listDevices)
        {
            Console.WriteLine(JsonSerializer.Serialize(await ExternalCapture.ListAsync(ffmpeg, stop.Token)));
            return 0;
        }
    }
    var preview = previewOnly ? new CapturePreview(args[2]) : publishPreview ? new CapturePreview(args[3]) : null;
    if (previewOnly)
    {
        await using var source = args[0] == "--preview-device" ? ExternalCapture.Open(ffmpeg, args[1]) : null;
        var monitor = source == null ? FindMonitor(args[1]) : null;
        while (!stop.IsCancellationRequested)
        {
            using var frame = source == null ? HudCapture.Desktop(monitor!) : await source.NextAsync(stop.Token);
            preview!.Publish(frame);
            await Task.Delay(250, stop.Token);
        }
        return 0;
    }
    var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Ocr"));
    var capture = new HudCapture(runtime);
    await using var external = args[0] switch
    {
        "--device" or "--device-frame" => ExternalCapture.Open(ffmpeg, args[1]),
        "--video" => ExternalCapture.OpenVideoForTest(ffmpeg, Path.GetFullPath(args[1])),
        _ => null
    };
    if (args[0] is "--image" or "--video" or "--device-frame")
    {
        using var image = external == null ? new Bitmap(args[1]) : await external.NextAsync(stop.Token);
        var result = await capture.DetectAsync(image, stop.Token);
        var unlock = result.Replay ? null : await capture.DetectMilitaryUnlockAsync(image, stop.Token, allowCroppedImage: true);
        var replay = result.Replay || unlock != null && result.Weapon == null && await capture.IsReplayAsync(image, stop.Token);
        var menuWeapon = replay ? null : await capture.ReadCamoMenuWeaponAsync(image, stop.Token);
        var challenge = replay ? null : await capture.DetectChallengeBannerAsync(image, stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            weapon = result.Weapon?.Name, akimbo = result.Weapon?.Akimbo, replay, militaryUnlock = replay ? null : unlock,
            camoProgress = await capture.DetectCamoProgressAsync(image, menuWeapon, stop.Token),
            trackedCamos = replay ? [] : await capture.DetectTrackedCamosAsync(image, stop.Token, savedImage: true),
            combat = replay ? null : await capture.DetectCombatAsync(image, stop.Token),
            longshot = !replay && await capture.DetectLongshotAsync(image, stop.Token),
            goldWeapon = await capture.DetectCompletionistGoldAsync(image, menuWeapon, stop.Token),
            goldBanner = challenge == "gold-15", challenge,
            specialUnlocks = await capture.DetectSpecialUnlocksAsync(image, menuWeapon, stop.Token)
        }));
        return 0;
    }
    var screen = external == null ? FindMonitor(args[1]) : null;
    var data = Directory.CreateDirectory(Path.GetFullPath(args[2])).FullName;
    var log = Path.Combine(data, "sunburn-events.jsonl");
    var confirmation = new WeaponConfirmation();
    var seenUnlocks = new HashSet<MilitaryUnlock>();
    var progressConfirmation = new CamoProgressConfirmation();
    var combat = new CombatConfirmation();
    var specialConfirmation = new SpecialUnlockConfirmation();
    var goldUnlocked = new HashSet<string>();
    var seenChallenges = new HashSet<string>();
    string? goldCandidate = null, matchPhase = null;
    string? confirmedWeapon = null;
    var lastConfirmedWeapon = DateTimeOffset.MinValue;
    var lastChallengeScan = DateTimeOffset.MinValue;
    var match = 0;
    var lastLobbyScan = DateTimeOffset.MinValue;
    var lastAkimbo = DateTimeOffset.MinValue;
    var lastLongshot = DateTimeOffset.MinValue;
    var longshotCounted = false;
    var lastProgressScan = DateTimeOffset.MinValue;
    while (!stop.IsCancellationRequested)
    {
        using var frame = external == null ? HudCapture.Desktop(screen!) : await external.NextAsync(stop.Token);
        if (preview != null)
        {
            try { preview.Publish(frame); }
            catch (IOException) { }
        }
        var now = DateTimeOffset.UtcNow;
        var result = await capture.DetectAsync(frame, stop.Token);
        if (!result.Replay && now - lastLobbyScan >= TimeSpan.FromSeconds(1))
        {
            lastLobbyScan = now;
            var notice = await capture.DetectMatchNoticeAsync(frame, stop.Token);
            if (notice != null && notice != matchPhase)
            {
                if (notice == "match-voting" || notice == "match-confirmed" && matchPhase != "match-voting")
                    match++;
                var previous = new { kills = combat.Kills, deaths = combat.Deaths, bestStreak = combat.Best };
                if (notice != "match-confirmed" || matchPhase != "match-voting") combat.Reset();
                confirmation.Reset();
                confirmedWeapon = null;
                matchPhase = notice;
                lastAkimbo = lastLongshot = DateTimeOffset.MinValue;
                longshotCounted = false;
                await AppendAsync(log, new
                {
                    kind = notice, source = "live", match, at = now,
                    kills = notice == "match-ended" ? previous.kills : combat.Kills,
                    deaths = notice == "match-ended" ? previous.deaths : combat.Deaths,
                    bestStreak = notice == "match-ended" ? previous.bestStreak : combat.Best
                }, stop.Token);
            }
        }
        if (confirmation.Observe(result.Weapon, result.Replay || matchPhase is "match-voting" or "match-ended") is { } weapon)
        {
            confirmedWeapon = weapon.Name;
            var record = JsonSerializer.Serialize(new
            {
                kind = "weapon", source = "live", mode = "mp", weapon = weapon.Name,
                akimbo = weapon.Akimbo, at = DateTimeOffset.UtcNow
            });
            await File.AppendAllTextAsync(log, record + "\n", stop.Token);
        }
        if (!result.Replay && result.Weapon?.Name == confirmedWeapon && confirmedWeapon != null)
            lastConfirmedWeapon = now;
        if (result.Weapon is { Akimbo: true }) lastAkimbo = now;
        else if (result.Weapon != null) lastAkimbo = DateTimeOffset.MinValue;
        if (!result.Replay && matchPhase is not ("match-ended" or "match-voting"))
        {
            var read = await capture.DetectCombatAsync(frame, stop.Token);
            if (!(read.Kill || read.Death) || result.Weapon != null || !await capture.IsReplayAsync(frame, stop.Token))
            {
                foreach (var entry in combat.Observe(read, now))
                {
                    if (matchPhase != "match-started")
                    {
                        if (matchPhase == null) match++;
                        matchPhase = "match-started";
                        await AppendAsync(log, new { kind = matchPhase, source = "live", match, at = now }, stop.Token);
                    }
                    await AppendAsync(log, new
                    {
                        kind = entry.Kind, source = "live", match, at = now, opponent = "unknown",
                        kills = entry.Kills, deaths = entry.Deaths, killStreak = entry.KillStreak,
                        bestStreak = entry.BestStreak, endedStreak = entry.EndedStreak
                    }, stop.Token);
                    if (entry.Kind == "death") lastAkimbo = DateTimeOffset.MinValue;
                    if (entry.Kind == "kill" && !read.Death && (result.Weapon is { Name: "Sang 9mm", Akimbo: true } ||
                        result.Weapon == null && now - lastAkimbo <= TimeSpan.FromMilliseconds(1500)))
                        await AppendAsync(log, new
                        {
                            kind = "akimbo-kill", source = "live", mode = "mp", weapon = "Sang 9mm",
                            at = now, detectedAt = DateTimeOffset.UtcNow, writtenAt = DateTimeOffset.UtcNow
                        }, stop.Token);
                }
            }
            if (result.Weapon?.Name == "Signal 50" && await capture.DetectLongshotAsync(frame, stop.Token))
            {
                lastLongshot = now;
                if (!longshotCounted)
                {
                    longshotCounted = true;
                    await AppendAsync(log, new
                    {
                        key = "signal-50:sp-3", delta = 1, weapon = "Signal 50", source = "live", mode = "mp", at = now
                    }, stop.Token);
                }
            }
            else if (result.Weapon?.Name == "Signal 50" && now - lastLongshot >= TimeSpan.FromMilliseconds(650))
                longshotCounted = false;
            var candidate = result.Weapon?.Name ?? (now - lastConfirmedWeapon <= TimeSpan.FromSeconds(5) ? confirmedWeapon : null);
            if (now - lastChallengeScan >= TimeSpan.FromSeconds(1))
            {
                lastChallengeScan = now;
                var challenge = await capture.DetectChallengeBannerAsync(frame, stop.Token);
                if (challenge != null && (result.Weapon != null || !await capture.IsReplayAsync(frame, stop.Token)))
                {
                    if (challenge == "gold-15" && candidate != null && goldUnlocked.Add(candidate))
                        await AppendAsync(log, new
                        {
                            kind = "completionist-unlock", source = "live", mode = "mp", weapon = candidate, challenge = "Gold", at = now
                        }, stop.Token);
                    else if (challenge != "gold-15" && (candidate == "Sang 9mm" || candidate == null && challenge != "menu-space-time") &&
                        seenChallenges.Add((candidate ?? "") + "|" + challenge))
                        await AppendAsync(log, new
                        {
                            kind = "challenge-completed", source = "live", mode = "mp", weapon = candidate, challenge, at = now
                        }, stop.Token);
                }
            }
        }
        if (!result.Replay && await capture.DetectMilitaryUnlockAsync(frame, stop.Token) is { } unlock &&
            (result.Weapon != null || !await capture.IsReplayAsync(frame, stop.Token)) &&
            seenUnlocks.Add(unlock))
        {
            var record = JsonSerializer.Serialize(new
            {
                kind = "military-unlock", source = "live", mode = "mp", weapon = unlock.Weapon,
                tier = unlock.Tier, at = DateTimeOffset.UtcNow
            });
            await File.AppendAllTextAsync(log, record + "\n", stop.Token);
        }
        if (DateTimeOffset.UtcNow - lastProgressScan >= TimeSpan.FromSeconds(1))
        {
            lastProgressScan = DateTimeOffset.UtcNow;
            var menuWeapon = result is { Weapon: null, Replay: false }
                ? await capture.ReadCamoMenuWeaponAsync(frame, stop.Token) : null;
            var gold = await capture.DetectCompletionistGoldAsync(frame, menuWeapon, stop.Token);
            if (gold != null && gold == goldCandidate && goldUnlocked.Add(gold))
                await AppendAsync(log, new
                {
                    kind = "completionist-unlock", source = "live", mode = "mp", weapon = gold, challenge = "Gold", at = now
                }, stop.Token);
            goldCandidate = gold;
            var specials = await capture.DetectSpecialUnlocksAsync(frame, menuWeapon, stop.Token);
            foreach (var special in specialConfirmation.Observe(specials))
                await AppendAsync(log, new
                {
                    kind = special.Kind, source = "live", mode = "mp",
                    weapon = special.Weapon, challenge = special.Challenge, at = now
                }, stop.Token);
            var hovered = await capture.DetectCamoProgressAsync(frame, menuWeapon, stop.Token);
            CamoProgress[] reads = result.Replay ? [] : hovered != null ? [hovered] :
                await capture.DetectTrackedCamosAsync(frame, stop.Token);
            foreach (var progress in progressConfirmation.ObserveRows(reads))
            {
                var record = JsonSerializer.Serialize(new
                {
                    kind = "progress-snapshot", source = "live", mode = "mp", weapon = progress.Weapon,
                    camo = progress.Camo, count = progress.Count, goal = progress.Goal, type = "camo", identity = progress.Identity,
                    key = progress.Weapon == "Signal 50" && progress.Camo.Equals("Sunburn", StringComparison.OrdinalIgnoreCase) &&
                        progress.Goal == 40 ? "signal-50:sp-3" : null, at = DateTimeOffset.UtcNow
                });
                await File.AppendAllTextAsync(log, record + "\n", stop.Token);
            }
        }
        await Task.Delay(500, stop.Token);
    }
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static Task AppendAsync(string log, object record, CancellationToken stop) =>
    File.AppendAllTextAsync(log, JsonSerializer.Serialize(record) + "\n", stop);

static Screen FindMonitor(string value)
{
    var screens = Screen.AllScreens;
    if (!int.TryParse(value, out var index) || index < 0 || index >= screens.Length)
        throw new ArgumentException("Select an available monitor, using a zero-based index.");
    return screens[index];
}
