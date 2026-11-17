using System.Diagnostics;
using System.Text.Json;
using CamoConnector.Detector;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
var listDevices = args.Length == 1 && args[0] == "--list-devices";
var previewOnly = args.Length == 2 && args[0] is "--preview-monitor" or "--preview-device";
var publishPreview = args.Length == 4 && args[3] == "--preview" && args[0] is "--monitor" or "--device";
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
        if (!File.Exists(ffmpeg))
            ffmpeg = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
                .Where(directory => Path.IsPathFullyQualified(directory))
                .Select(directory => Path.Combine(directory, "ffmpeg.exe"))
                .FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Capture device discovery requires FFmpeg. Use a build that bundles Runtime/Capture/ffmpeg.exe.");
        if (listDevices)
        {
            Console.WriteLine(JsonSerializer.Serialize(await ExternalCapture.ListAsync(ffmpeg, stop.Token)));
            return 0;
        }
    }
    if (previewOnly)
    {
        await using var source = args[0] == "--preview-device" ? ExternalCapture.Open(ffmpeg, args[1]) : null;
        var monitor = source == null ? FindMonitor(args[1]) : null;
        await CapturePreview.RunAsync(token => source == null
            ? Task.FromResult(HudCapture.Desktop(monitor!)) : source.NextAsync(token), stop.Token);
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
            weapon = result.Weapon?.Name, menuWeapon, akimbo = result.Weapon?.Akimbo, replay, militaryUnlock = replay ? null : unlock,
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
    using var previewStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    var previewTask = publishPreview ? Task.Run(() => CapturePreview.RunAsync(token => external == null
        ? Task.FromResult(HudCapture.Desktop(screen!)) : external.NextAsync(token, forPreview: true), previewStop.Token)) : null;
    try
    {
        await DetectLiveAsync(args[2], capture, external, screen, stop.Token);
    }
    finally
    {
        previewStop.Cancel();
        if (previewTask != null)
        {
            try { await previewTask; }
            catch (OperationCanceledException) when (previewStop.IsCancellationRequested) { }
            catch (IOException error) { Console.Error.WriteLine("Preview stopped: " + error.Message); }
        }
    }
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static async Task DetectLiveAsync(string dataDirectory, HudCapture capture, ExternalCapture? external, Screen? screen, CancellationToken cancel)
{
    var data = Directory.CreateDirectory(Path.GetFullPath(dataDirectory)).FullName;
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
    var lastTiming = Stopwatch.GetTimestamp();
    var totalScanMs = 0d;
    var scans = 0;
    var weaponReads = 0;
    var menuReads = 0;
    string? lastMenuWeapon = null;
    var camoReads = 0;
    var replayReads = 0;
    var firstFrame = true;
    while (!cancel.IsCancellationRequested)
    {
        using var frame = external == null ? HudCapture.Desktop(screen!) : await external.NextAsync(cancel);
        if (firstFrame)
        {
            Console.Error.WriteLine($"First frame from {screen?.DeviceName ?? "capture device"}: {frame.Width} × {frame.Height}.");
            firstFrame = false;
        }
        var now = DateTimeOffset.UtcNow;
        var scanStarted = Stopwatch.GetTimestamp();
        var scanLobby = now - lastLobbyScan >= TimeSpan.FromSeconds(1);
        var scanChallenge = now - lastChallengeScan >= TimeSpan.FromMilliseconds(400);
        var unlockRead = ReadSnapshotAsync(frame, copy => capture.DetectMilitaryUnlockAsync(copy, cancel));
        var lobbyRead = scanLobby ? ReadSnapshotAsync(frame, copy => capture.DetectMatchNoticeAsync(copy, cancel)) : null;
        var challengeRead = scanChallenge ? ReadSnapshotAsync(frame, copy => capture.DetectChallengeBannerAsync(copy, cancel)) : null;
        var combatRead = ReadSnapshotAsync(frame, copy => capture.DetectCombatAsync(copy, cancel));
        var weaponRead = capture.DetectAsync(frame, cancel);
        await Task.WhenAll(unlockRead, (Task?)lobbyRead ?? Task.CompletedTask,
            (Task?)challengeRead ?? Task.CompletedTask, combatRead, weaponRead);
        var result = await weaponRead;
        if (result.Weapon != null) weaponReads++;
        if (result.Replay) replayReads++;
        if (!result.Replay && lobbyRead != null)
        {
            lastLobbyScan = now;
            var notice = await lobbyRead;
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
                }, cancel);
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
            await File.AppendAllTextAsync(log, record + "\n", cancel);
        }
        if (!result.Replay && result.Weapon?.Name == confirmedWeapon && confirmedWeapon != null)
            lastConfirmedWeapon = now;
        if (result.Weapon is { Akimbo: true }) lastAkimbo = now;
        else if (result.Weapon != null) lastAkimbo = DateTimeOffset.MinValue;
        if (!result.Replay && matchPhase is not ("match-ended" or "match-voting"))
        {
            var read = await combatRead;
            if (!(read.Kill || read.Death) || result.Weapon != null || !await capture.IsReplayAsync(frame, cancel))
            {
                foreach (var entry in combat.Observe(read, now))
                {
                    if (matchPhase != "match-started")
                    {
                        if (matchPhase == null) match++;
                        matchPhase = "match-started";
                        await AppendAsync(log, new { kind = matchPhase, source = "live", match, at = now }, cancel);
                    }
                    await AppendAsync(log, new
                    {
                        kind = entry.Kind, source = "live", match, at = now, opponent = "unknown",
                        kills = entry.Kills, deaths = entry.Deaths, killStreak = entry.KillStreak,
                        bestStreak = entry.BestStreak, endedStreak = entry.EndedStreak
                    }, cancel);
                    if (entry.Kind == "death") lastAkimbo = DateTimeOffset.MinValue;
                    if (entry.Kind == "kill" && !read.Death && (result.Weapon is { Name: "Sang 9mm", Akimbo: true } ||
                        result.Weapon == null && now - lastAkimbo <= TimeSpan.FromMilliseconds(1500)))
                        await AppendAsync(log, new
                        {
                            kind = "akimbo-kill", source = "live", mode = "mp", weapon = "Sang 9mm",
                            at = now, detectedAt = DateTimeOffset.UtcNow, writtenAt = DateTimeOffset.UtcNow
                        }, cancel);
                }
            }
            if (result.Weapon?.Name == "Signal 50" && await capture.DetectLongshotAsync(frame, cancel))
            {
                lastLongshot = now;
                if (!longshotCounted)
                {
                    longshotCounted = true;
                    await AppendAsync(log, new
                    {
                        key = "signal-50:sp-3", delta = 1, weapon = "Signal 50", source = "live", mode = "mp", at = now
                    }, cancel);
                }
            }
            else if (result.Weapon?.Name == "Signal 50" && now - lastLongshot >= TimeSpan.FromMilliseconds(650))
                longshotCounted = false;
            var candidate = result.Weapon?.Name ?? (now - lastConfirmedWeapon <= TimeSpan.FromSeconds(5) ? confirmedWeapon : null);
            if (challengeRead != null)
            {
                lastChallengeScan = now;
                var challenge = await challengeRead;
                if (challenge != null && (result.Weapon != null || !await capture.IsReplayAsync(frame, cancel)))
                {
                    if (challenge == "gold-15" && candidate != null && goldUnlocked.Add(candidate))
                        await AppendAsync(log, new
                        {
                            kind = "completionist-unlock", source = "live", mode = "mp", weapon = candidate, challenge = "Gold", at = now
                        }, cancel);
                    else if (challenge != "gold-15" && (candidate == "Sang 9mm" || candidate == null && challenge != "menu-space-time") &&
                        seenChallenges.Add((candidate ?? "") + "|" + challenge))
                        await AppendAsync(log, new
                        {
                            kind = "challenge-completed", source = "live", mode = "mp", weapon = candidate, challenge, at = now
                        }, cancel);
                }
            }
        }
        if (!result.Replay && await unlockRead is { } unlock &&
            (result.Weapon != null || !await capture.IsReplayAsync(frame, cancel)) &&
            seenUnlocks.Add(unlock))
        {
            var record = JsonSerializer.Serialize(new
            {
                kind = "military-unlock", source = "live", mode = "mp", weapon = unlock.Weapon,
                tier = unlock.Tier, at = DateTimeOffset.UtcNow
            });
            await File.AppendAllTextAsync(log, record + "\n", cancel);
        }
        if (DateTimeOffset.UtcNow - lastProgressScan >= TimeSpan.FromMilliseconds(400))
        {
            lastProgressScan = DateTimeOffset.UtcNow;
            var menuWeapon = result is { Weapon: null, Replay: false }
                ? await capture.ReadCamoMenuWeaponAsync(frame, cancel) : null;
            if (menuWeapon != null)
            {
                menuReads++;
                if (menuWeapon != lastMenuWeapon)
                    Console.Error.WriteLine($"Recognized camo menu for {menuWeapon}.");
            }
            lastMenuWeapon = menuWeapon;
            var goldRead = ReadSnapshotAsync(frame, copy => capture.DetectCompletionistGoldAsync(copy, menuWeapon, cancel));
            var specialRead = ReadSnapshotAsync(frame, copy => capture.DetectSpecialUnlocksAsync(copy, menuWeapon, cancel));
            var progressRead = ReadProgressAsync(frame, capture, menuWeapon, result.Replay, cancel);
            await Task.WhenAll(goldRead, specialRead, progressRead);
            var gold = await goldRead;
            if (gold != null && gold == goldCandidate && goldUnlocked.Add(gold))
                await AppendAsync(log, new
                {
                    kind = "completionist-unlock", source = "live", mode = "mp", weapon = gold, challenge = "Gold", at = now
                }, cancel);
            goldCandidate = gold;
            var specials = await specialRead;
            foreach (var special in specialConfirmation.Observe(specials))
                await AppendAsync(log, new
                {
                    kind = special.Kind, source = "live", mode = "mp",
                    weapon = special.Weapon, challenge = special.Challenge, at = now
                }, cancel);
            var reads = await progressRead;
            camoReads += reads.Length;
            foreach (var progress in progressConfirmation.ObserveRows(reads))
            {
                var record = JsonSerializer.Serialize(new
                {
                    kind = "progress-snapshot", source = "live", mode = "mp", weapon = progress.Weapon,
                    camo = progress.Camo, count = progress.Count, goal = progress.Goal, type = "camo", identity = progress.Identity,
                    key = progress.Weapon == "Signal 50" && progress.Camo.Equals("Sunburn", StringComparison.OrdinalIgnoreCase) &&
                        progress.Goal == 40 ? "signal-50:sp-3" : null, at = DateTimeOffset.UtcNow
                });
                await File.AppendAllTextAsync(log, record + "\n", cancel);
            }
        }
        totalScanMs += Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;
        scans++;
        if (Stopwatch.GetElapsedTime(lastTiming) >= TimeSpan.FromSeconds(10))
        {
            Console.Error.WriteLine($"Scanned {scans} frames ({totalScanMs / scans:F0} ms average). " +
                $"HUD weapons {weaponReads}, camo menus {menuReads}, counters {camoReads}, replays {replayReads}.");
            lastTiming = Stopwatch.GetTimestamp();
            totalScanMs = 0;
            scans = 0;
            weaponReads = menuReads = camoReads = replayReads = 0;
        }
        await Task.Delay(75, cancel);
    }
}

static async Task<T> ReadSnapshotAsync<T>(Bitmap frame, Func<Bitmap, Task<T>> read)
{
    using var copy = (Bitmap)frame.Clone();
    return await read(copy);
}

static async Task<CamoProgress[]> ReadProgressAsync(Bitmap frame, HudCapture capture, string? menuWeapon, bool replay, CancellationToken cancel)
{
    if (replay) return [];
    var hovered = await capture.DetectCamoProgressAsync(frame, menuWeapon, cancel);
    return hovered != null ? [hovered] : await capture.DetectTrackedCamosAsync(frame, cancel);
}

static Task AppendAsync(string log, object record, CancellationToken stop) =>
    File.AppendAllTextAsync(log, JsonSerializer.Serialize(record) + "\n", stop);

static Screen FindMonitor(string value)
{
    var screens = Screen.AllScreens;
    var selected = screens.FirstOrDefault(screen => screen.DeviceName.Equals(value, StringComparison.OrdinalIgnoreCase));
    if (selected != null) return selected;
    if (!int.TryParse(value, out var index) || index < 0 || index >= screens.Length)
        throw new ArgumentException("Select an available monitor, using a zero-based index.");
    return screens[index];
}
