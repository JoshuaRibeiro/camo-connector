using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CamoConnector.Detector;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
var listDevices = args.Length == 1 && args[0] == "--list-devices";
var previewOnly = args.Length == 2 && args[0] is "--preview-monitor" or "--preview-device";
var saveMenuCrops = args.Length == 4 && args[2] == "--save-crops";
var probe = (args.Length == 2 || saveMenuCrops) &&
    args[0] is "--probe-menu-monitor" or "--probe-menu-image" or "--probe-tracked-monitor" or "--probe-tracked-image" or "--probe-unlock-image";
var publishPreview = args.Length == 4 && args[3] == "--preview" && args[0] is "--monitor" or "--device";
if (!listDevices && !previewOnly && !publishPreview && !probe &&
    (args.Length != 3 || args[0] is not ("--image" or "--monitor" or "--device" or "--device-frame" or "--video")))
{
    Console.Error.WriteLine("CamoConnector.Detector --list-devices | (--image <capture> | --video <file> | --monitor <index> | --device <input> | --device-frame <input>) <data-directory>");
    Console.Error.WriteLine("CamoConnector.Detector (--probe-menu-monitor <display> | --probe-menu-image <capture>) [--save-crops <directory>]");
    Console.Error.WriteLine("CamoConnector.Detector (--probe-tracked-monitor <display> | --probe-tracked-image <capture>) [--save-crops <directory>]");
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
            ? Task.FromResult(HudCapture.Desktop(monitor!)) : source.NextAsync(token), stop.Token,
            filterIsolatedFrames: source == null);
        return 0;
    }
    var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Ocr"));
    using var capture = new HudCapture(runtime);
    if (probe)
    {
        var monitor = args[0].EndsWith("-monitor", StringComparison.Ordinal) ? FindMonitor(args[1]) : null;
        using var frame = monitor == null ? new Bitmap(args[1]) : HudCapture.Desktop(monitor);
        var cropDirectory = saveMenuCrops
            ? Directory.CreateDirectory(Path.Combine(Path.GetFullPath(args[3]), $"camo-probe-{Guid.NewGuid():N}")).FullName
            : null;
        var reads = new Dictionary<string, string>();
        void RecordCrop(string region, string raw, Bitmap crop)
        {
            reads[region] = raw;
            if (cropDirectory != null)
                crop.Save(Path.Combine(cropDirectory, region + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        object result;
        var source = monitor?.DeviceName ?? Path.GetFullPath(args[1]);
        if (args[0].StartsWith("--probe-tracked-", StringComparison.Ordinal))
        {
            var trackedCamos = await capture.DetectTrackedCamosAsync(frame, stop.Token, savedImage: monitor == null, trace: RecordCrop);
            result = new { source, frame.Width, frame.Height, trackedCamos, cropDirectory, reads };
        }
        else if (args[0] == "--probe-unlock-image")
        {
            var (weapon, replay) = await capture.DetectAsync(frame, stop.Token);
            var banner = await capture.DetectChallengeBannerAsync(frame, stop.Token, RecordCrop);
            var special = banner.Completed && banner.Challenge == null && !replay
                ? capture.IdentifySpecialBanner(frame, weapon?.Name, banner.Details) : null;
            result = new { source, weapon, replay, banner.Challenge, banner.Completed, special, cropDirectory, reads };
        }
        else
        {
            var menuWeapon = await capture.ReadCamoMenuWeaponAsync(frame, stop.Token, RecordCrop);
            var militaryProgress = menuWeapon == null ? null : await capture.ReadMilitaryMenuCountAsync(frame, stop.Token, RecordCrop);
            var camoProgress = await capture.DetectCamoProgressAsync(frame, menuWeapon, stop.Token, RecordCrop);
            result = new { source, frame.Width, frame.Height, menuWeapon, militaryProgress, camoProgress, cropDirectory, reads };
        }
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = saveMenuCrops });
        if (cropDirectory != null) File.WriteAllText(Path.Combine(cropDirectory, "ocr.json"), json);
        Console.WriteLine(json);
        return 0;
    }
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
        var banner = replay ? default : await capture.DetectChallengeBannerAsync(image, stop.Token);
        var challenge = banner.Challenge;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            weapon = result.Weapon?.Name, menuWeapon, akimbo = result.Weapon?.Akimbo, replay, militaryUnlock = replay ? null : unlock,
            camoProgress = await capture.DetectCamoProgressAsync(image, menuWeapon, stop.Token),
            trackedCamos = replay ? [] : await capture.DetectTrackedCamosAsync(image, stop.Token, savedImage: true),
            combat = replay ? null : await capture.DetectCombatAsync(image, stop.Token),
            longshot = !replay && await capture.DetectLongshotAsync(image, stop.Token),
            goldWeapon = await capture.DetectCompletionistGoldAsync(image, menuWeapon, stop.Token),
            goldBanner = challenge == "gold-15", challenge,
            specialBanner = banner.Completed && challenge == null ? capture.IdentifySpecialBanner(image, result.Weapon?.Name, banner.Details) : null,
            specialUnlocks = await capture.DetectSpecialUnlocksAsync(image, menuWeapon, stop.Token)
        }));
        return 0;
    }
    var screen = external == null ? FindMonitor(args[1]) : null;
    using var previewStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    var acceptedFrames = publishPreview && external == null
        ? Channel.CreateBounded<Bitmap>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest },
            dropped => dropped.Dispose())
        : null;
    var previewTask = publishPreview ? Task.Run(async () =>
    {
        try
        {
            await CapturePreview.RunAsync(token => external == null
                ? Task.FromResult(HudCapture.Desktop(screen!)) : external.NextAsync(token, forPreview: true),
                previewStop.Token, filterIsolatedFrames: external == null,
                onAccepted: acceptedFrames == null ? null : frame =>
                {
                    var copy = (Bitmap)frame.Clone();
                    if (!acceptedFrames.Writer.TryWrite(copy)) copy.Dispose();
                });
        }
        catch (Exception error)
        {
            acceptedFrames?.Writer.TryComplete(error);
            throw;
        }
        finally { acceptedFrames?.Writer.TryComplete(); }
    }) : null;
    try
    {
        await DetectLiveAsync(args[2], capture, external, screen, acceptedFrames?.Reader, stop.Token);
    }
    finally
    {
        previewStop.Cancel();
        try
        {
            if (previewTask != null) await previewTask;
        }
        catch (OperationCanceledException) when (previewStop.IsCancellationRequested) { }
        catch (IOException error) { Console.Error.WriteLine("Preview stopped: " + error.Message); }
        finally
        {
            if (acceptedFrames != null)
                while (acceptedFrames.Reader.TryRead(out var frame)) frame.Dispose();
        }
    }
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error.StackTrace);
    Console.Error.WriteLine("Detector error: " + error.Message.ReplaceLineEndings(" | "));
    return 1;
}

static async Task DetectLiveAsync(string dataDirectory, HudCapture capture, ExternalCapture? external, Screen? screen,
    ChannelReader<Bitmap>? acceptedFrames, CancellationToken cancel)
{
    Console.Error.WriteLine("OCR engine: " + capture.OcrMode + ".");
    var data = Directory.CreateDirectory(Path.GetFullPath(dataDirectory)).FullName;
    var log = Path.Combine(data, "sunburn-events.jsonl");
    var confirmation = new WeaponConfirmation();
    var menuConfirmation = new WeaponConfirmation();
    var seenUnlocks = new HashSet<MilitaryUnlock>();
    MilitaryUnlock? previousMenuMilitary = null;
    var progressConfirmation = new CamoProgressConfirmation();
    var combat = new CombatConfirmation();
    var specialConfirmation = new SpecialUnlockConfirmation();
    var goldUnlocked = new HashSet<string>();
    var seenChallenges = new HashSet<string>();
    var seenSpecialBanners = new HashSet<SpecialUnlock>();
    string? goldCandidate = null, matchPhase = null;
    string? confirmedWeapon = null;
    var lastConfirmedWeapon = DateTimeOffset.MinValue;
    var lastChallengeScan = DateTimeOffset.MinValue;
    var unassignedGoldVisible = false;
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
        using var frame = acceptedFrames != null ? await acceptedFrames.ReadAsync(cancel) :
            external == null ? HudCapture.Desktop(screen!) : await external.NextAsync(cancel);
        if (firstFrame)
        {
            Console.Error.WriteLine($"First frame from {screen?.DeviceName ?? "capture device"}: {frame.Width} × {frame.Height}.");
            firstFrame = false;
        }
        var now = DateTimeOffset.UtcNow;
        var scanStarted = Stopwatch.GetTimestamp();
        var scanLobby = now - lastLobbyScan >= TimeSpan.FromSeconds(1);
        var scanChallenge = now - lastChallengeScan >= TimeSpan.FromMilliseconds(200);
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
        if (confirmation.Observe(result.Weapon, result.Replay, now) is { } weapon)
        {
            // Two matching, replay-checked HUD reads also prove gameplay resumed if the lobby transition was missed.
            if (matchPhase is "match-ended" or "match-voting")
            {
                if (matchPhase == "match-ended") match++;
                matchPhase = "match-started";
                await AppendAsync(log, new { kind = matchPhase, source = "live", match, at = now }, cancel);
            }
            menuConfirmation.Reset();
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
        var combatFrame = await combatRead;
        // A lobby notice can be missed between scans. A first-kill card outside a replay can start the next match.
        var resumedAfterLobby = matchPhase is "match-ended" or "match-voting" &&
            combatFrame.Ordinal == 1 && !result.Replay &&
            !await capture.IsReplayAsync(frame, cancel);
        if (!result.Replay && (matchPhase is not ("match-ended" or "match-voting") || resumedAfterLobby))
        {
            var read = combatFrame;
            if (!(read.Kill || read.Death) || result.Weapon != null || !await capture.IsReplayAsync(frame, cancel))
            {
                foreach (var entry in combat.Observe(read, now))
                {
                    if (matchPhase != "match-started")
                    {
                        if (matchPhase is null or "match-ended") match++;
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
                var banner = await challengeRead;
                var challenge = banner.Challenge;
                if (challenge != "gold-15") unassignedGoldVisible = false;
                if (banner.Completed && (result.Weapon != null || !await capture.IsReplayAsync(frame, cancel)))
                {
                    if (challenge == "gold-15" && (result.Weapon?.Name is { } goldWeapon
                        ? goldUnlocked.Add(goldWeapon) : !unassignedGoldVisible))
                        await AppendAsync(log, new
                        {
                            kind = "completionist-unlock", source = "live", mode = "mp", weapon = result.Weapon?.Name, challenge = "Gold", at = now
                        }, cancel);
                    else if (challenge != null && challenge != "gold-15" && (candidate == "Sang 9mm" || candidate == null && challenge != "menu-space-time") &&
                        seenChallenges.Add((candidate ?? "") + "|" + challenge))
                        await AppendAsync(log, new
                        {
                            kind = "challenge-completed", source = "live", mode = "mp", weapon = candidate, challenge, at = now
                        }, cancel);
                    else if (challenge == null && capture.IdentifySpecialBanner(frame, candidate, banner.Details) is { } special &&
                        seenSpecialBanners.Add(special))
                        await AppendAsync(log, new
                        {
                            kind = special.Kind, source = "live", mode = "mp", weapon = special.Weapon, challenge = special.Challenge, at = now
                        }, cancel);
                    if (challenge == "gold-15" && result.Weapon == null) unassignedGoldVisible = true;
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
            if (menuConfirmation.Observe(menuWeapon == null ? null : new WeaponRead(menuWeapon, false), result.Replay) is { } selected)
            {
                confirmation.Reset();
                await AppendAsync(log, new
                {
                    kind = "weapon", source = "live", mode = "mp", weapon = selected.Name,
                    context = "camo-menu", at = now
                }, cancel);
            }
            // GDI+ bitmap clones can share backing storage. Keep this menu scan on one reader.
            var gold = await capture.DetectCompletionistGoldAsync(frame, menuWeapon, cancel);
            var specials = await capture.DetectSpecialUnlocksAsync(frame, menuWeapon, cancel);
            var militaryTier = menuWeapon == null ? null : await capture.ReadMilitaryMenuCountAsync(frame, cancel);
            var reads = await ReadProgressAsync(frame, capture, menuWeapon, result.Replay, cancel);
            var menuMilitary = menuWeapon != null && militaryTier is int tier && tier > 0
                ? new MilitaryUnlock(menuWeapon, tier) : null;
            if (menuMilitary != null && menuMilitary == previousMenuMilitary && seenUnlocks.Add(menuMilitary))
            {
                await AppendAsync(log, new
                {
                    kind = "military-unlock", source = "live", mode = "mp", weapon = menuMilitary.Weapon,
                    tier = menuMilitary.Tier, context = "camo-menu", at = now
                }, cancel);
            }
            previousMenuMilitary = menuMilitary;
            if (gold != null && gold == goldCandidate && goldUnlocked.Add(gold))
                await AppendAsync(log, new
                {
                    kind = "completionist-unlock", source = "live", mode = "mp", weapon = gold, challenge = "Gold", at = now
                }, cancel);
            goldCandidate = gold;
            foreach (var special in specialConfirmation.Observe(specials))
                await AppendAsync(log, new
                {
                    kind = special.Kind, source = "live", mode = "mp",
                    weapon = special.Weapon, challenge = special.Challenge, at = now
                }, cancel);
            camoReads += reads.Length;
            foreach (var progress in progressConfirmation.ObserveRows(reads))
            {
                var record = JsonSerializer.Serialize(new
                {
                    kind = "progress-snapshot", source = "live", mode = "mp", weapon = progress.Weapon,
                    camo = progress.WeaponLevelObjective == null ? progress.Camo : null,
                    count = progress.Count, goal = progress.Goal, identity = progress.Identity,
                    type = progress.WeaponLevelObjective == null ? "camo" : "weapon-level",
                    objective = progress.WeaponLevelObjective,
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
    using var copy = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    using (var graphics = Graphics.FromImage(copy))
        graphics.DrawImageUnscaled(frame, 0, 0);
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
