using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CamoConnector.Detector;

internal sealed class HudCapture : IDisposable
{
    private static readonly byte[] Padlock = Convert.FromBase64String(
        "fX2AVi+Af35+e3194fv5+frjIRd9wuOAMixf3GkWfcytTVAxILxxLX3Mz9XGyNDy5FN8zPz///////56fuz////////+ZoT+///5+P///ml8/v//x6n///5phf7//8Wn///+doT+//+0lf///oZJ/v//6+D///dsK/7+/v7///1uLSz7+/v7/PyfTRY=");
    private static readonly (double X, double Y, double W, double H)[] WeaponAreas =
        { (.705, .864, .205, .053), (.727, .875, .098, .031), (.747, .869, .077, .044) };
    private readonly string ocr;
    private readonly string models;
    private readonly PersistentOcr? persistentOcr;
    public string OcrMode => persistentOcr != null ? "persistent Tesseract" : "Tesseract command line";
    private readonly Dictionary<string, CamoReference[]> catalog;
    private readonly Dictionary<string, CamoReference[]> completionist = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CamoReference[]> trackedCatalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> levelRequirements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> unlockRequirements = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pointBlankWeapons = new(StringComparer.OrdinalIgnoreCase);

    public HudCapture(string runtime)
    {
        ocr = Path.Combine(runtime, "tesseract.exe");
        models = Path.Combine(runtime, "tessdata");
        if (!File.Exists(ocr) || !File.Exists(Path.Combine(models, "eng.traineddata")))
            throw new FileNotFoundException("Missing the bundled OCR executable or English model in " + runtime);
        using var stream = typeof(HudCapture).Assembly.GetManifestResourceStream("CamoConnector.Detector.SpecialCatalog")
            ?? throw new InvalidDataException("The detector camo catalogue is missing.");
        using var document = JsonDocument.Parse(stream);
        catalog = document.RootElement.EnumerateObject().ToDictionary(
            weapon => weapon.Name,
            weapon => weapon.Value.EnumerateArray()
                .Where(camo => camo.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                .Select(camo => new CamoReference(camo.GetProperty("name").GetString()!,
                    camo.TryGetProperty("artworkRGB12", out var rgb) && rgb.ValueKind == JsonValueKind.String
                    ? Convert.FromBase64String(rgb.GetString()!) : null,
                    camo.TryGetProperty("slot", out var slot) && slot.ValueKind == JsonValueKind.Number && slot.TryGetInt32(out var slotNumber) ? slotNumber : 0,
                    camo.TryGetProperty("goal", out var goal) && goal.ValueKind == JsonValueKind.Number && goal.TryGetInt32(out var goalNumber) ? goalNumber : null,
                    camo.TryGetProperty("bannerArtworkRGB12", out var banner) && banner.ValueKind == JsonValueKind.String
                    ? Convert.FromBase64String(banner.GetString()!) : null)).ToArray());
        using var objectivesStream = typeof(HudCapture).Assembly.GetManifestResourceStream("CamoConnector.MultiplayerCatalog")
            ?? throw new InvalidDataException("The multiplayer objective catalogue is missing.");
        using var objectives = JsonDocument.Parse(objectivesStream);
        var completionByCategory = objectives.RootElement.GetProperty("shared").EnumerateArray()
            .Where(camo => camo.GetProperty("type").GetString() == "Completionist")
            .ToLookup(camo => camo.GetProperty("category").GetString(), camo => new CamoReference(
                camo.GetProperty("name").GetString()!, null,
                Goal: camo.TryGetProperty("goal", out var goal) && goal.ValueKind == JsonValueKind.Number
                    ? goal.GetInt32() : null)
                { Objective = camo.TryGetProperty("objective", out var objective) ? objective.GetString() : null });
        foreach (var weapon in objectives.RootElement.GetProperty("weapons").EnumerateArray())
        {
            var trackerName = weapon.GetProperty("name").GetString()!;
            var detectorName = catalog.Keys.FirstOrDefault(name => name.Equals(trackerName, StringComparison.OrdinalIgnoreCase))
                ?? trackerName;
            levelRequirements[detectorName] = weapon.GetProperty("camos").EnumerateArray()
                .Where(camo => camo.GetProperty("type").GetString() == "Military" &&
                    camo.TryGetProperty("unlock", out var unlock) && unlock.ValueKind == JsonValueKind.String &&
                    unlock.GetString()!.StartsWith("Reach Weapon Level ", StringComparison.OrdinalIgnoreCase) &&
                    camo.TryGetProperty("unlockTotal", out var total) && total.ValueKind == JsonValueKind.Number)
                .ToDictionary(camo => camo.GetProperty("name").GetString()!,
                    camo => camo.GetProperty("unlockTotal").GetInt32());
            unlockRequirements[detectorName] = weapon.GetProperty("camos").EnumerateArray()
                .Where(camo => camo.TryGetProperty("unlock", out var unlock) &&
                    unlock.ValueKind == JsonValueKind.String &&
                    unlock.GetString()!.Contains("weapon unlock", StringComparison.OrdinalIgnoreCase) &&
                    camo.TryGetProperty("unlockTotal", out var total) && total.ValueKind == JsonValueKind.Number)
                .ToDictionary(camo => camo.GetProperty("name").GetString()!,
                    camo => camo.GetProperty("unlockTotal").GetInt32());
            var category = weapon.GetProperty("category").GetString();
            completionist[detectorName] = completionByCategory[category == "Melee" ? category : category + "s"].ToArray();
            var entries = (catalog.GetValueOrDefault(detectorName) ?? []).ToList();
            foreach (var camo in weapon.GetProperty("camos").EnumerateArray())
            {
                if (camo.GetProperty("type").GetString() != "Special") continue;
                var objective = camo.TryGetProperty("objective", out var objectiveValue) ? objectiveValue.GetString() : null;
                if (objective != null && CombatRecognition.IsPointBlank(objective))
                    pointBlankWeapons.Add(detectorName);
                var name = camo.GetProperty("name").GetString()!;
                var goal = camo.TryGetProperty("goal", out var target) && target.TryGetInt32(out var number)
                    ? (int?)number : null;
                var index = entries.FindIndex(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    entries[index] = entries[index] with { Goal = goal ?? entries[index].Goal, Objective = objective };
                else
                {
                    var id = camo.GetProperty("id").GetString()!;
                    var slot = int.TryParse(id.AsSpan(3), out var parsedSlot) ? parsedSlot : 0;
                    entries.Add(new CamoReference(name, null, slot, goal) { Objective = objective });
                }
            }
            catalog[detectorName] = entries.ToArray();
            trackedCatalog[detectorName] = [.. entries, .. completionist[detectorName]];
        }
        persistentOcr = PersistentOcr.Open(runtime, models);
    }

    public void Dispose() => persistentOcr?.Dispose();

    public static Bitmap Desktop(Screen screen) => Desktop(screen.Bounds);

    public static Bitmap Desktop(Rectangle bounds)
    {
        var frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(frame);
            var desktop = GetDC(IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the desktop capture context.");
            try
            {
                var destination = graphics.GetHdc();
                try
                {
                    const uint copyLayeredDesktop = (uint)(CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                    if (!BitBlt(destination, 0, 0, bounds.Width, bounds.Height, desktop, bounds.X, bounds.Y, copyLayeredDesktop))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not capture the composited desktop.");
                }
                finally { graphics.ReleaseHdc(destination); }
            }
            finally { ReleaseDC(IntPtr.Zero, desktop); }
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    internal static Bitmap? CropPaddedViewport(Bitmap frame)
    {
        // Sample the middle of each axis so controls outside the video do not define its bounds.
        bool HasContent(bool vertical, int position)
        {
            var lit = 0;
            var length = vertical ? frame.Height : frame.Width;
            for (var i = 0; i < 24; i++)
            {
                var across = (int)(length * (.23 + i * .54 / 23));
                var pixel = vertical ? frame.GetPixel(position, across) : frame.GetPixel(across, position);
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) >= (vertical ? 9 : 5) &&
                    ++lit >= (vertical ? 5 : 3)) return true;
            }
            return false;
        }

        int FindSide(bool reverse)
        {
            var length = frame.Width;
            for (var offset = 0; offset < length / 5; offset++)
            {
                var position = reverse ? length - 1 - offset : offset;
                if (HasContent(true, position)) return position;
            }
            return reverse ? length - 1 : 0;
        }

        // Skip ordinary edge-to-edge captures quickly.
        if (HasContent(true, 0) || HasContent(true, frame.Width - 1)) return null;
        var left = FindSide(false);
        var right = FindSide(true);
        if (left < frame.Width * .015 || frame.Width - 1 - right < frame.Width * .015) return null;
        var width = right - left + 1;
        var height = (int)Math.Round(width * 9d / 16);
        if (height >= frame.Height) return null;
        var top = 0;
        while (top <= frame.Height - height &&
            !(HasContent(false, top) && HasContent(false, top + 1) && HasContent(false, top + 2))) top++;
        if (top > frame.Height - height) top = (frame.Height - height) / 2;
        var bounds = new Rectangle(left, top, width, height);
        return frame.Clone(bounds, PixelFormat.Format24bppRgb);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rasterOperation);

    public async Task<(WeaponRead? Weapon, bool Replay)> DetectAsync(Bitmap frame, CancellationToken cancel,
        Action<string, string, Bitmap>? trace = null)
    {
        // Read the current weapon label below the ammo counter before wider HUD regions.
        var labelArea = (.683, .876, .141, .027);
        foreach (var adjustment in new[] { PixelAdjustment.Normal, PixelAdjustment.Contrast })
        {
            var (label, _) = await ReadTextAsync(frame, labelArea, cancel, pageMode: 7, adjustment: adjustment,
                trace: trace, regionName: "hudLabel" + adjustment);
            if (WeaponRecognition.ReadHud(Lines(label), false) is { } weapon)
                return await IsReplayAsync(frame, cancel) ? (null, true) : (weapon, false);
        }
        foreach (var area in WeaponAreas)
        {
            var (text, _) = await ReadTextAsync(frame, area, cancel, trace: trace, regionName: $"hudBlock{area.X}");
            var read = WeaponRecognition.ReadHud(Lines(text), area.W >= .15);
            if (read != null) return await IsReplayAsync(frame, cancel) ? (null, true) : (read, false);
        }
        // Short labels need less background in the crop to separate them from the ammo display.
        foreach (var area in new[] { (.742, .876, .082, .026), (.782, .877, .048, .026), (.719, .884, .105, .026) })
        {
            var (label, _) = await ReadTextAsync(frame, area, cancel, pageMode: 7, trace: trace, regionName: $"hudLine{area.Item1}");
            if (WeaponRecognition.ReadHud(Lines(label), false) is { } weapon)
                return await IsReplayAsync(frame, cancel) ? (null, true) : (weapon, false);
        }
        var (shortLabel, _) = await ReadTextAsync(frame, (.782, .877, .048, .026), cancel,
            pageMode: 7, adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "hudShortContrast");
        if (WeaponRecognition.ReadHud(Lines(shortLabel), false) is { } pistol)
            return await IsReplayAsync(frame, cancel) ? (null, true) : (pistol, false);
        foreach (var area in new[] { (.744, .879, .082, .024), (.782, .879, .043, .024), (.767, .882, .057, .020) })
        {
            var (label, _) = await ReadTextAsync(frame, area, cancel, pageMode: 7, adjustment: PixelAdjustment.WhiteText,
                trace: trace, regionName: $"hudWhite{area.Item1}");
            if (WeaponRecognition.ReadHud(Lines(label), false) is { } weapon)
                return await IsReplayAsync(frame, cancel) ? (null, true) : (weapon, false);
        }
        var (tightLabel, _) = await ReadTextAsync(frame, (.807, .878, .018, .024), cancel, pageMode: 7,
            trace: trace, regionName: "hudTight");
        if (WeaponRecognition.ReadHud(Lines(tightLabel), false) is { Name: "M4" } rifle)
            return await IsReplayAsync(frame, cancel) ? (null, true) : (rifle, false);
        if (HasSelectedKnife(frame))
            return await IsReplayAsync(frame, cancel) ? (null, true) : (new("FSS Carbon Edge 1095", false), false);
        return (null, false);
    }

    private static bool HasSelectedKnife(Bitmap frame)
    {
        // The knife slot stays visible when inactive. The active slot has an olive selection band.
        var highlighted = 0;
        for (var column = 0; column < 8; column++)
        {
            var x = (int)((.909 + column * .008) * frame.Width);
            var upper = frame.GetPixel(x, (int)(.846 * frame.Height));
            var lower = frame.GetPixel(x, (int)(.852 * frame.Height));
            if (IsOlive(upper) || IsOlive(lower)) highlighted++;
        }
        if (highlighted < 5) return false;
        static bool IsOlive(Color pixel) => pixel.G >= pixel.R + 4 && pixel.R >= pixel.B + 6;
        string[] silhouette =
        [
            "0000000000000000000000000000000000000000",
            "0000000000000000000000000000000000000000",
            "0000000111111111110001000000000000000000",
            "0000111111111111111110110111100000000000",
            "0001111111111111111110111111111111000000",
            "0000111111111111011111111111111111101000",
            "0000011111111111111111111111111111110000",
            "0000001111111111111111111111111111111100",
            "0000000001111111111111111111111111111100",
            "0000000000000000011100111111111111111100",
            "0000000000000000000000110000000000001100",
            "0000000000000000000000000000000000000000"
        ];
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var overlap = 0;
            var union = 0;
            for (var y = 0; y < silhouette.Length; y++)
            for (var x = 0; x < silhouette[y].Length; x++)
            {
                var pixel = frame.GetPixel(
                    (int)Math.Round((.939 + (x + dx) * .034 / 40) * frame.Width),
                    (int)Math.Round((.856 + (y + dy) * .010 / 12) * frame.Height));
                var bright = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) >= 172;
                var expected = silhouette[y][x] == '1';
                if (bright && expected) overlap++;
                if (bright || expected) union++;
            }
            if (overlap >= union * .72) return true;
        }
        return false;
    }

    public async Task<MilitaryUnlock?> DetectMilitaryUnlockAsync(Bitmap frame, CancellationToken cancel,
        bool allowCroppedImage = false)
    {
        var (header, _) = await ReadTextAsync(frame, (.900, .210, .080, .041), cancel, pageMode: 7,
            adjustment: PixelAdjustment.WhiteText);
        if (!WeaponRecognition.IsUnlockHeader(header))
            (header, _) = await ReadTextAsync(frame, (.909, .218, .063, .024), cancel, pageMode: 7);
        if (!WeaponRecognition.IsUnlockHeader(header))
            (header, _) = await ReadTextAsync(frame, (.909375, .21597, .05859, .02778), cancel, pageMode: 7, adjustment: PixelAdjustment.NativeWhite);
        MilitaryUnlock? badge = null;
        if (WeaponRecognition.IsUnlockHeader(header))
        {
            var body = await ReadAsync(frame, (.833, .242, .144, .065), cancel);
            badge = WeaponRecognition.ReadMilitaryUnlock([header, .. body]);
            if (badge == null)
            {
                // Container seams and other bright edges can join the weapon text in the combined crop.
                var (weapon, _) = await ReadTextAsync(frame, (.833, .246, .099, .025), cancel, pageMode: 7);
                var (tier, _) = await ReadTextAsync(frame, (.836, .268, .135, .030), cancel, pageMode: 7);
                badge = WeaponRecognition.ReadMilitaryUnlock([header, weapon, tier]);
                if (badge == null)
                {
                    var (yellowTier, _) = await ReadTextAsync(frame, (.833, .269, .135, .030), cancel,
                        pageMode: 7, adjustment: PixelAdjustment.YellowText);
                    badge = WeaponRecognition.ReadMilitaryUnlock([header, weapon, yellowTier]);
                }
            }
        }
        if (badge == null && WeaponRecognition.IsUnlockHeader(header)) {
            var (weapon, _) = await ReadTextAsync(frame, (.833, .245, .042, .025), cancel, pageMode: 7);
            var (tier, _) = await ReadTextAsync(frame, (.83359, .26944, .13281, .03194), cancel, pageMode: 7, adjustment: PixelAdjustment.BadgeGold);
            badge = WeaponRecognition.ReadMilitaryUnlock([header, weapon, tier]);
            if (badge == null && tier.Contains("CAMO", StringComparison.OrdinalIgnoreCase) && WeaponRecognition.Read([weapon], false) == null) {
                var hud = await DetectAsync(frame, cancel);
                if (!hud.Replay && hud.Weapon != null) badge = WeaponRecognition.ReadMilitaryUnlock([header, hud.Weapon.Name, tier]);
            }
        }
        badge ??= WeaponRecognition.ReadMilitaryUnlock(await ReadAsync(frame, (.827, .045, .155, .405), cancel));
        if (badge == null) badge = await DetectMilitarySummaryAsync(frame, cancel);
        if (badge == null && allowCroppedImage)
            return WeaponRecognition.ReadMilitaryUnlock(await ReadAsync(frame, (0, 0, 1, 1), cancel));
        return badge;
    }

    private async Task<MilitaryUnlock?> DetectMilitarySummaryAsync(Bitmap frame, CancellationToken cancel)
    {
        var (heading, _) = await ReadTextAsync(frame, (.052, .080, .150, .039), cancel, pageMode: 7);
        if (!WeaponRecognition.IsWeaponSummary(heading)) return null;
        var (weapon, _) = await ReadTextAsync(frame, (.048, .128, .365, .051), cancel, pageMode: 7);
        MilitaryUnlock? best = null;
        foreach (var (x, width) in new[] { (.453, .094), (.527, .103), (.349, .137) })
        {
            // Reward names appear before the XP bar finishes. Completion lights both sides of the card.
            if (!HasCompletedRewardBorder(frame, x + width / 2)) continue;
            foreach (var adjustment in new[] { PixelAdjustment.Normal, PixelAdjustment.Contrast, PixelAdjustment.Invert })
            {
                var (card, _) = await ReadTextAsync(frame, (x, .652, width, .026), cancel, pageMode: 7,
                    adjustment: adjustment);
                if (WeaponRecognition.ReadMilitarySummary(heading, weapon, card) is not { } unlock) continue;
                if (best == null || unlock.Tier > best.Tier) best = unlock;
                break;
            }
        }
        return best;
    }

    private static bool HasCompletedRewardBorder(Bitmap frame, double center)
    {
        foreach (var side in new[] { -1, 1 })
        {
            var litRows = 0;
            for (var row = 0; row < 12; row++)
            {
                var y = (int)((.535 + row * .013) * frame.Height);
                for (var step = 0; step < 11; step++)
                {
                    var x = (int)((center + side * (.070 + step * .001)) * frame.Width);
                    var pixel = frame.GetPixel(x, y);
                    if (pixel.R < 175 || pixel.G < 185 || pixel.B > pixel.G - 20) continue;
                    litRows++;
                    break;
                }
            }
            if (litRows < 8) return false;
        }
        return true;
    }

    public async Task<bool> IsReplayAsync(Bitmap frame, CancellationToken cancel)
    {
        foreach (var area in new[] { (.30, .027, .40, .12), (.29, .73, .42, .15) })
            if (WeaponRecognition.IsReplay(string.Join(" ", await ReadAsync(frame, area, cancel)))) return true;
        return false;
    }

    public async Task<CombatRead> DetectCombatAsync(Bitmap frame, CancellationToken cancel) => CombatRecognition.Read(
        string.Join(" ", await ReadAsync(frame, (.599, .427, .037, .030), cancel)),
        string.Join(" ", await ReadAsync(frame, (.378, .798, .076, .033), cancel)),
        string.Join(" ", await ReadAsync(frame, (.467, .934, .078, .033), cancel)));

    public async Task<bool> DetectLongshotAsync(Bitmap frame, CancellationToken cancel) =>
        CombatRecognition.IsLongshot(string.Join(" ", await ReadAsync(frame, (.578, .395, .139, .175), cancel)));

    public bool TracksPointBlank(string weapon) => pointBlankWeapons.Contains(weapon);

    public async Task<bool> DetectPointBlankAsync(Bitmap frame, CancellationToken cancel) =>
        CombatRecognition.IsPointBlank(string.Join(" ", await ReadAsync(frame, (.588, .431, .133, .116), cancel)));

    public async Task<string?> DetectMatchNoticeAsync(Bitmap frame, CancellationToken cancel)
    {
        var result = string.Join(" ", await ReadAsync(frame, (.797, .042, .181, .127), cancel));
        if (System.Text.RegularExpressions.Regex.IsMatch(result, @"\b(?:VICTORY|DEFEAT|DRAW)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var detail = string.Join(" ", await ReadAsync(frame, (.827, .045, .155, .405), cancel));
            return CombatRecognition.ReadLobby(result + " " + detail);
        }
        // Gun Game Wager shows the result and its reason together in the upper left.
        var wagerHeading = await ReadTextAsync(frame, (.039, .055, .19, .11), cancel, pageMode: 7);
        if (System.Text.RegularExpressions.Regex.IsMatch(wagerHeading.Text, @"\b(?:VICTORY|DEFEAT|DRAW)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var reason = await ReadTextAsync(frame, (.045, .216, .16, .034), cancel, pageMode: 7);
            var wagerNotice = CombatRecognition.ReadLobby(wagerHeading.Text + " " + reason.Text);
            if (wagerNotice != null) return wagerNotice;
        }
        var notice = CombatRecognition.ReadLobby(string.Join(" ", await ReadAsync(frame, (0, 0, .99, .42), cancel)));
        if (notice != null) return notice;
        var matchmaking = await ReadTextAsync(frame, (.34, .70, .32, .06), cancel, pageMode: 7);
        return CombatRecognition.ReadLobby(matchmaking.Text);
    }

    public async Task<int> DetectCompletionistCountAsync(Bitmap frame, string? weapon, CancellationToken cancel)
    {
        if (weapon == null) return 0;
        var completion = string.Join(" ", await ReadAsync(frame, (.070, .550, .110, .040), cancel));
        var count = CombatRecognition.ReadCompletionistCount(completion);
        // Platinum is the second tile. A visible or ambiguous lock vetoes the counter.
        if (count >= 2 && PadlockScore(frame, .1313, .595) >= .55) return 1;
        return count;
    }

    public async Task<SpecialUnlock[]> DetectSpecialUnlocksAsync(Bitmap frame, string? weapon, CancellationToken cancel)
    {
        if (weapon == null) return [];
        var text = string.Join(" ", await ReadAsync(frame, (.070, .389, .090, .039), cancel));
        var count = SpecialUnlocks.ReadCount(text);
        if (count is not > 0) return [];
        var scores = new[] { .075, .150, .225 }.Select(x => PadlockScore(frame, x)).ToArray();
        return SpecialUnlocks.Resolve(weapon, count.Value, scores, catalog.GetValueOrDefault(weapon) ?? []);
    }

    private static double PadlockScore(Bitmap frame, double tileX, double tileY = .426)
    {
        var best = 0d;
        const int count = 140;
        var targetMean = Padlock.Average(value => (double)value);
        var targetVariance = Padlock.Sum(value => Math.Pow(value - targetMean, 2));
        for (var dx = -6; dx <= 9; dx++)
        for (var dy = 0; dy <= 12; dy++)
        {
            var x = tileX + dx * .00085;
            var y = tileY + dy * .0011;
            double sum = 0, squares = 0, covariance = 0;
            for (var py = 0; py < 14; py++)
            for (var px = 0; px < 10; px++)
            {
                var pixel = frame.GetPixel((int)((x + (px + .5) * .0009) * frame.Width),
                    (int)((y + (py + .5) * .019 / 14) * frame.Height));
                var value = (double)Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                sum += value;
                squares += value * value;
                covariance += value * (Padlock[py * 10 + px] - targetMean);
            }
            var variance = squares - sum * sum / count;
            if (variance >= count * 24 * 24) best = Math.Max(best, covariance / Math.Sqrt(variance * targetVariance));
        }
        return best;
    }

    public async Task<(string? Challenge, bool Completed, string[]? Details, WeaponLevelRead? LevelUp)> DetectChallengeBannerAsync(Bitmap frame, CancellationToken cancel,
        Action<string, string, Bitmap>? trace = null)
    {
        var warmPixels = 0;
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 5; x++)
        {
            var pixel = frame.GetPixel((int)Math.Round((.480 + x * .010) * frame.Width),
                (int)Math.Round((.198 + y * .009) * frame.Height));
            if (pixel.R > 90 && pixel.R >= pixel.G + 15 && pixel.R >= pixel.B + 28) warmPixels++;
        }
        var goldTile = warmPixels >= 22;
        async Task<string> ReadBannerText((double X, double Y, double W, double H) area, string region,
            PixelAdjustment adjustment = PixelAdjustment.Normal, int pageMode = 7) =>
            string.Join(" ", Lines((await ReadTextAsync(frame, area, cancel, pageMode: pageMode,
                adjustment: adjustment, trace: trace, regionName: region)).Text));
        var heading = await ReadBannerText((.365, .257, .274, .041), "unlockHeading", PixelAdjustment.CyanText);
        if (!LooksCompleted(heading))
            heading = await ReadBannerText((.350, .250, .300, .047), "unlockHeadingOriginal", pageMode: 6);
        var levelHeading = heading;
        if (!LooksCompleted(heading) && !WeaponRecognition.IsLevelUpHeading(heading))
            levelHeading = await ReadBannerText((.425, .268, .150, .037), "weaponLevelHeading", PixelAdjustment.YellowText);
        if (!LooksCompleted(heading) && !WeaponRecognition.IsLevelUpHeading(levelHeading))
            levelHeading = await ReadBannerText((.42969, .27083, .140625, .03472), "levelHeadingTight", PixelAdjustment.BrightGold, 8);
        if (WeaponRecognition.IsLevelUpHeading(levelHeading))
        {
            var name = await ReadBannerText((.447, .308, .106, .030), "weaponLevelName");
            var (number, _) = await ReadTextAsync(frame, (.479, .334, .042, .048), cancel, pageMode: 7,
                countersOnly: true, adjustment: PixelAdjustment.YellowText, trace: trace, regionName: "weaponLevelNumber");
            if (WeaponRecognition.ReadLevelUp(levelHeading, name, number) == null) {
                if (WeaponRecognition.Read([name], false) == null) name = await ReadBannerText((.4781, .3111, .04375, .02292), "levelNameTight");
                (number, _) = await ReadTextAsync(frame, (.484, .33333, .032, .04583), cancel, pageMode: 8,
                    countersOnly: true, adjustment: PixelAdjustment.BrightGold, trace: trace, regionName: "levelNumberTight");
            }
            if (WeaponRecognition.ReadLevelUp(levelHeading, name, number) is { } levelUp)
                return (null, false, null, levelUp);
        }
        var completed = LooksCompleted(heading);
        var cyanLetters = 0;
        if (!goldTile && !completed)
        {
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 72; x++)
            {
                var pixel = frame.GetPixel((int)((.363 + x * .0038) * frame.Width),
                    (int)((.260 + y * .0037) * frame.Height));
                if (Math.Min(pixel.G, pixel.B) >= 155 && Math.Min(pixel.G, pixel.B) - pixel.R >= 22)
                    cyanLetters++;
            }

        }
        // A close match to captured banner art also works when the completion heading is unreadable.
        if (!completed && !goldTile)
            completed = IdentifySpecialBanner(frame, null, null, bannerArtworkOnly: true) != null;
        if (!goldTile && (completed || heading.Contains("CHALLEN", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(heading, @"[CG]OMPLE", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) &&
            CombatRecognition.IsPlatinumArtwork(Sample(frame, .148, (.471, .058, .102, 0))))
            return ("platinum", true, null, null);
        if (!goldTile && !completed && cyanLetters < 15) return (null, false, null, null);
        var detail = await ReadBannerText((.395, .300, .210, .020), "unlockObjective");
        string? Match(string text) => CombatRecognition.IsGoldBanner(heading, text, goldTile)
            ? "gold-15" : SpecialUnlocks.ReadBanner(heading, text);
        if (Match(detail) is { } match) return (match, true, null, null);
        var details = new List<string> { detail };
        foreach (var adjustment in new[] { PixelAdjustment.Normal, PixelAdjustment.Contrast, PixelAdjustment.Invert })
        {
            detail = await ReadBannerText((.382, .301, .237, .022), $"unlockObjective{adjustment}", adjustment);
            if (Match(detail) is { } fallback) return (fallback, true, null, null);
            details.Add(detail);
        }
        // Hardpoint notices can cover the middle of the Gold objective.
        var goalStart = await ReadBannerText((.402, .297, .083, .025), "goldObjectiveStart");
        if (CombatRecognition.IsSplitGoldObjective(goalStart, string.Join(" ", details)))
            return ("gold-15", true, null, null);
        return (null, completed && !goldTile, details.ToArray(), null);

        static bool LooksCompleted(string text) => System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\bCOMPLETED\b|\bCHALLENGE\w{0,2}\s+LETED\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public SpecialUnlock? IdentifySpecialBanner(Bitmap frame, string? weapon, string[]? details, bool bannerArtworkOnly = false)
    {
        var sample = Sample(frame, .148, (.471, .058, .102, 0));
        // Completed challenges can appear after respawn, when the current weapon is unrelated.
        var all = catalog.Values.SelectMany(entries => entries).ToArray();
        static string Compact(string value) =>
            System.Text.RegularExpressions.Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]", "");
        var lines = details?.Select(Compact).Where(text => text.Length > 0).ToArray() ?? [];
        var matchingObjectives = all.Where(entry => entry.Objective is { Length: > 0 } objective &&
            lines.Any(line => line.Contains(Compact(objective), StringComparison.Ordinal))).ToArray();
        var candidates = matchingObjectives.Length == 0 ? all : matchingObjectives;
        var captured = all.Where(entry => entry.BannerRgb != null);
        if (MatchBannerArtwork(captured, 15) is { } capturedName &&
            candidates.Any(entry => entry.Name == capturedName) && ResolveUnique(capturedName) is { } capturedUnlock)
            return capturedUnlock;
        if (bannerArtworkOnly) return null;
        if (matchingObjectives.Length == 1 && ResolveUnique(matchingObjectives[0].Name) is { } objectiveUnlock)
            return objectiveUnlock;
        if (matchingObjectives.Length > 1 && MatchBannerArtwork(matchingObjectives) is { } objectiveArt &&
            ResolveUnique(objectiveArt) is { } matchedUnlock)
            return matchedUnlock;
        if (weapon != null && catalog.TryGetValue(weapon, out var entries))
        {
            var eligible = entries.Where(candidates.Contains).ToArray();
            var name = SpecialUnlocks.ReadNamedBanner(details, eligible) ?? MatchBannerArtwork(eligible);
            return name != null
                ? new(weapon, name, "catalog-special-unlocked") : null;
        }
        var unique = SpecialUnlocks.ReadNamedBanner(details, candidates) ?? MatchBannerArtwork(candidates);
        return unique == null ? null : ResolveUnique(unique);

        SpecialUnlock? ResolveUnique(string name)
        {
            var weapons = catalog.Where(pair => pair.Value.Any(entry => entry.Name == name)).Take(2).ToArray();
            return weapons.Length == 1 ? new(weapons[0].Key, name, "catalog-special-unlocked") : null;
        }

        string? MatchBannerArtwork(IEnumerable<CamoReference> candidates, double maximumError = 31) => TrackedCamoRecognition.MatchArtwork(sample,
            candidates.Select(entry => entry.BannerRgb == null ? entry : entry with { Rgb = entry.BannerRgb }).ToArray(), maximumError);
    }

    public async Task<CamoProgress?> DetectCamoProgressAsync(Bitmap frame, string? weapon, CancellationToken cancel,
        Action<string, string, Bitmap>? trace = null)
    {
        if (weapon == null) return null;
        var references = (catalog.GetValueOrDefault(weapon) ?? [])
            .Concat(completionist.GetValueOrDefault(weapon) ?? []).ToArray();
        var requirements = unlockRequirements.GetValueOrDefault(weapon);
        var names = references.Select(camo => camo.Name).ToArray();
        var titles = names.Concat(Enumerable.Range(1, 8).Select(tier => $"Military Camo {tier}")).ToArray();
        var (titleText, _) = await ReadTextAsync(frame, (.344, .628, .310, .041), cancel, pageMode: 7,
            trace: trace, regionName: "camoTitle");
        var title = CamoMenuRecognition.ReadKnownCamoTitle(titleText, titles);
        if (title == null && names.Length > 0)
        {
            // Leave room for the top of the lettering while excluding the lock icon.
            var (specialTitle, _) = await ReadTextAsync(frame, (.343, .613, .255, .060), cancel, pageMode: 7,
                adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "tightCamoTitleContrast");
            title = CamoMenuRecognition.ReadKnownCamoTitle(specialTitle, titles);
        }
        // Some layouts place the selected camo details above the lower menu region.
        var raisedDetails = false;
        if (title == null && names.Length > 0)
        {
            var (raisedTitle, _) = await ReadTextAsync(frame, (.344, .398, .310, .055), cancel, pageMode: 7,
                trace: trace, regionName: "raisedCamoTitle");
            title = CamoMenuRecognition.ReadKnownCamoTitle(raisedTitle, titles);
            raisedDetails = title != null;
        }
        if (title == null)
        {
            var (brightTitle, _) = await ReadTextAsync(frame, (.344, .613, .310, .060), cancel, pageMode: 7,
                adjustment: PixelAdjustment.WhiteText, trace: trace, regionName: "whiteCamoTitle");
            title = CamoMenuRecognition.ReadKnownCamoTitle(brightTitle, titles);
        }
        if (title == null)
        {
            var (wideTitle, _) = await ReadTextAsync(frame, (.312, .614, .472, .064), cancel,
                trace: trace, regionName: "wideCamoTitle");
            title = CamoMenuRecognition.ReadKnownCamoTitle(wideTitle, titles) ?? string.Join(" ", Lines(wideTitle));
        }
        if (title.Length == 0) return null;
        var objectivesArea = raisedDetails ? (.313, .455, .342, .159) : (.313, .679, .342, .181);
        var (objectives, _) = await ReadTextAsync(frame, objectivesArea, cancel,
            trace: trace, regionName: "camoObjectives");
        if (CamoMenuRecognition.ReadMilitaryLevel(weapon, title, objectives) is { } weaponLevel)
            return weaponLevel;
        if (title.StartsWith("Military Camo ", StringComparison.OrdinalIgnoreCase))
        {
            var (detail, _) = await ReadTextAsync(frame, (.344, .712, .250, .033), cancel, pageMode: 7,
                adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "militaryRequirement");
            var (counter, _) = await ReadTextAsync(frame, (.524, .744, .050, .035), cancel, pageMode: 7,
                countersOnly: true, trace: trace, regionName: "militaryLevelCounter");
            if (CamoMenuRecognition.ReadMilitaryLevel(weapon, title, detail + "\n" + counter) is { } isolatedLevel)
                return isolatedLevel;
            if (levelRequirements.GetValueOrDefault(weapon)?.TryGetValue(title, out var levelGoal) == true)
            {
                var (alternateCounter, _) = await ReadTextAsync(frame, (.527, .748, .035, .027), cancel, pageMode: 7,
                    countersOnly: true, adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "militaryLevelCounterContrast");
                if (CamoMenuRecognition.ConfirmMilitaryLevel(weapon, levelGoal, counter, alternateCounter) is { } confirmedLevel)
                    return confirmedLevel;
            }
            if (requirements?.TryGetValue(title, out var threshold) == true &&
                CamoMenuRecognition.ReadWeaponUnlocks(weapon, title, objectives, threshold) == null)
                objectives += "\n" + detail;
        }
        var progress = CamoMenuRecognition.ReadProgress(weapon, title, Lines(objectives), names);
        if (requirements?.TryGetValue(title, out var unlockTotal) == true &&
            !System.Text.RegularExpressions.Regex.IsMatch(objectives, @"\d\s*/\s*\d") &&
            CamoMenuRecognition.ReadWeaponUnlocks(weapon, title, objectives, unlockTotal) is { } weaponUnlocks)
            return weaponUnlocks;
        if (!names.Contains(title)) return progress;

        var reference = references.FirstOrDefault(camo => camo.Name == title);
        var catalogGoal = reference?.Goal;
        progress = CamoMenuRecognition.CheckObjectiveGoal(progress, catalogGoal, objectives);

        // Check the isolated counter even when the wider objective OCR returns a plausible value.
        async Task<CamoProgress?> ReadCounter((double X, double Y, double W, double H) area, PixelAdjustment adjustment, string region)
        {
            var (counter, _) = await ReadTextAsync(frame, area, cancel, pageMode: 7,
                countersOnly: true, adjustment: adjustment, trace: trace, regionName: region);
            return System.Text.RegularExpressions.Regex.IsMatch(counter.Trim(), @"\A[0-9]+\s*/\s*[0-9]+\z")
                ? CamoMenuRecognition.CheckObjectiveGoal(
                    CamoMenuRecognition.ReadProgress(weapon, title, [counter], names), catalogGoal, objectives)
                : null;
        }

        CamoProgress? Validate(CamoProgress? candidate)
        {
            // A visible Special counter is an in-progress read. Unlocks are verified from the tiles or banner.
            if (candidate != null && candidate.Count == candidate.Goal) return null;
            return CamoMenuRecognition.CheckObjectiveGoal(candidate, catalogGoal, objectives);
        }
        // Keep the progress bar out of the first OCR pass for the standard layout.
        // When the objective crop already supplied a value, the tight crop can confirm it.
        CamoProgress? tight = null;
        if (!raisedDetails && progress != null)
        {
            tight = await ReadCounter((.534, .810, .032, .027), PixelAdjustment.Normal, "camoTightCounter");
            if (CamoMenuRecognition.ConfirmCounter(progress, tight, null) is { } tightConfirmed)
                return Validate(tightConfirmed);
            if (tight != null) return null;
        }
        var isolated = await ReadCounter(raisedDetails ? (.525, .572, .045, .037) : (.512, .795, .060, .045), PixelAdjustment.Normal, "camoCounter");
        if (CamoMenuRecognition.ConfirmCounter(progress, isolated, null) is { } confirmed)
            return Validate(confirmed);
        // A wider counter crop retains digits when the menu is shifted or the counter is longer.
        var alternate = await ReadCounter(raisedDetails ? (.520, .563, .062, .053) : (.499, .780, .102, .066), PixelAdjustment.Contrast, "camoCounterContrast");
        if (progress != null)
        {
            if (isolated != null && isolated != progress) return null;
            return Validate(CamoMenuRecognition.ConfirmCounter(progress, alternate, null));
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(objectives, @"\d\s*/\s*\d") &&
            Validate(CamoMenuRecognition.ConfirmCounter(null, isolated, alternate)) is { } paired)
            return paired;

        // A crossed-out prerequisite can confuse the multiline OCR. Read the challenge
        // and its meter separately before accepting progress from this menu layout.
        if (raisedDetails || reference?.Objective is not { } expectedObjective) return null;
        var (challenge, _) = await ReadTextAsync(frame, (.341, .774, .280, .033), cancel, pageMode: 7,
            adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "camoChallengeLine");
        if (!CamoMenuRecognition.MatchesCamoObjective(challenge, expectedObjective)) return null;
        tight = await ReadCounter((.534, .810, .032, .027), PixelAdjustment.Normal, "camoTightCounter");
        if (tight == null || (isolated != null && isolated != tight) || (alternate != null && alternate != tight))
            return null;
        var corroborating = isolated ?? alternate ??
            await ReadCounter((.533, .807, .038, .033), PixelAdjustment.Contrast, "camoTightCounterContrast");
        return Validate(CamoMenuRecognition.ConfirmCounter(null, tight, corroborating));
    }

    public async Task<(string Weapon, int Count, int Goal)?> ReadPostMatchUnlocksAsync(Bitmap frame, CancellationToken cancel, Action<string, string, Bitmap>? trace = null)
    {
        var header = await ReadTextAsync(frame, (.054, .084, .117, .032), cancel, pageMode: 7, trace: trace, regionName: "summaryheader");
        if (!header.Text.Contains("PERFORMING", StringComparison.OrdinalIgnoreCase)) return null;
        var label = await ReadTextAsync(frame, (.877, .210, .052, .027), cancel, pageMode: 7, trace: trace, regionName: "summarylabel");
        if (!string.Concat(label.Text.Where(char.IsLetter)).Equals("UNLOCKS", StringComparison.OrdinalIgnoreCase)) return null;
        var title = await ReadTextAsync(frame, (.050, .128, .36, .054), cancel, pageMode: 7, adjustment: PixelAdjustment.WhiteText, trace: trace, regionName: "summarytitle");
        var weapon = TrackedCamoRecognition.ReadWeapon(title.Text);
        if (weapon == null) {
            var compact = await ReadTextAsync(frame, (.050, .128, .118, .054), cancel, pageMode: 7,
                adjustment: PixelAdjustment.WhiteText, trace: trace, regionName: "summaryCompactTitle");
            weapon = TrackedCamoRecognition.ReadWeapon(compact.Text);
        }
        if (weapon == null) return null;
        var current = await ReadTextAsync(frame, (.862, .241, .042, .066), cancel, pageMode: 7, countersOnly: true, adjustment: PixelAdjustment.BrightRedChannel, trace: trace, regionName: "summarycurrent");
        var maximum = await ReadTextAsync(frame, (.912, .241, .040, .066), cancel, pageMode: 8, countersOnly: true, adjustment: PixelAdjustment.YellowText, trace: trace, regionName: "summarymaximum");
        return int.TryParse(current.Text.Trim(), out var count) && int.TryParse(maximum.Text.Trim(), out var goal) &&
            goal is > 0 and <= 150 && count >= 0 && count <= goal ? (weapon, count, goal) : null;
    }

    public async Task<string?> ReadCamoMenuWeaponAsync(Bitmap frame, CancellationToken cancel, Action<string, string, Bitmap>? trace = null, bool requireHeading = false)
    {
        // End before the game logo beside CAMO SELECT.
        var (heading, _) = await ReadTextAsync(frame, (.072, .047, .114, .040), cancel, pageMode: 7,
            trace: trace, regionName: "heading");
        if (requireHeading && !CamoMenuRecognition.IsCamoMenu(heading)) return null;
        var (tab, _) = await ReadTextAsync(frame, (.071, .102, .147, .033), cancel, pageMode: 7,
            trace: trace, regionName: "progressionTab");
        var weapon = CamoMenuRecognition.ReadProgressionWeapon(tab);
        if (weapon == null)
        {
            var (activeTab, _) = await ReadTextAsync(frame, (.075, .106, .112, .027), cancel, pageMode: 7,
                trace: trace, regionName: "activeProgressionTab");
            weapon = CamoMenuRecognition.ReadProgressionWeapon(activeTab);
        }
        if (weapon == null && CamoMenuRecognition.IsCamoMenu(heading))
        {
            var (standardTab, _) = await ReadTextAsync(frame, (.073, .102, .133, .033), cancel, pageMode: 7,
                trace: trace, regionName: "standardProgressionTab");
            weapon = CamoMenuRecognition.ReadProgressionWeapon(standardTab);
        }
        if (weapon == null && CamoMenuRecognition.IsCamoMenu(heading))
        {
            var (wideTab, _) = await ReadTextAsync(frame, (.071, .102, .202, .033), cancel, pageMode: 7,
                trace: trace, regionName: "wideProgressionTab");
            weapon = CamoMenuRecognition.ReadProgressionWeapon(wideTab);
        }
        if (weapon == null && CamoMenuRecognition.IsCamoMenu(heading))
        {
            var (breadcrumb, _) = await ReadTextAsync(frame, (.072, .023, .295, .023), cancel, pageMode: 7,
                trace: trace, regionName: "camoBreadcrumb");
            weapon = CamoMenuRecognition.ReadBreadcrumbWeapon(breadcrumb);
        }
        if (weapon == null || CamoMenuRecognition.IsCamoMenu(heading)) return weapon;
        // The progression tab and a military row together identify the menu
        // when its heading drops out or is partly obscured.
        return await ReadMilitaryMenuCountAsync(frame, cancel, trace) != null ? weapon : null;
    }

    public async Task<int?> ReadMilitaryMenuCountAsync(Bitmap frame, CancellationToken cancel,
        Action<string, string, Bitmap>? trace = null)
    {
        var (military, _) = await ReadTextAsync(frame, (.071, .163, .095, .029), cancel, pageMode: 7,
            trace: trace, regionName: "militaryRow");
        var count = CamoMenuRecognition.ReadMilitaryMenuCount(military);
        if (count == null)
        {
            var (alternateRow, _) = await ReadTextAsync(frame, (.070, .160, .091, .033), cancel, pageMode: 7,
                adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "militaryRowContrast");
            count = CamoMenuRecognition.ReadMilitaryMenuCount(alternateRow);
        }
        if (count is not > 0) return count;

        // A stable OCR error can survive multiple frames. Require a second read of just the fraction.
        var (counter, _) = await ReadTextAsync(frame, (.119, .163, .043, .029), cancel, pageMode: 7,
            trace: trace, regionName: "militaryCounter");
        if (!CamoMenuRecognition.ConfirmsMilitaryCounter(count.Value, counter))
        {
            var (alternate, _) = await ReadTextAsync(frame, (.115, .160, .049, .033), cancel, pageMode: 7,
                adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "militaryCounterContrast");
            if (!CamoMenuRecognition.ConfirmsMilitaryCounter(count.Value, alternate)) return null;
        }

        // A visible padlock contradicts a claim that the tile has already been unlocked.
        for (var tile = 0; tile < count.Value; tile++)
        {
            var x = .075 + tile % 4 * .0563;
            var y = tile < 4 ? .193 : .293;
            if (PadlockScore(frame, x, y) >= .70) return null;
        }
        return count;
    }

    public async Task<CamoProgress[]> DetectTrackedCamosAsync(Bitmap frame, CancellationToken cancel, bool savedImage = false,
        Action<string, string, Bitmap>? trace = null)
    {
        var layouts = savedImage && (double)frame.Width / frame.Height < 1.4
            ? new[] { (Heading: (.02, .01, .36, .09), Area: (.25, .14, .52, .69), Thumbnail: (.075, .155, .168, .007)) }
            : new[]
            {
                (Heading: (.067, .114, .110, .038), Area: (.121, .156, .141, .335), Thumbnail: (.084, .039, .069, .004)),
                (Heading: (.740, .117, .110, .037), Area: (.792, .159, .146, .335), Thumbnail: (.760, .039, .069, .004)),
                (Heading: (.654, .383, .095, .035), Area: (.714, .432, .135, .278), Thumbnail: (.670, .039, .069, .004))
            };
        for (var i = 0; i < layouts.Length; i++)
        {
            var layout = layouts[i];
            var lobby = layouts.Length == 3 && i < 2;
            var (heading, _) = await ReadTextAsync(frame, layout.Heading, cancel, trace: trace, regionName: $"trackedHeading{i + 1}");
            if (!TrackedCamoRecognition.IsPanel(heading) && lobby)
            {
                var (headingX, headingY, _, _) = layout.Heading;
                (heading, _) = await ReadTextAsync(frame, (headingX + .006, headingY + .006, .051, .026),
                    cancel, pageMode: 7, adjustment: PixelAdjustment.Contrast, trace: trace,
                    regionName: $"trackedHeadingTight{i + 1}");
            }
            if (!TrackedCamoRecognition.IsPanel(heading) &&
                !(lobby && heading.Trim().Equals("CAMUS", StringComparison.OrdinalIgnoreCase))) continue;
            var (text, height) = await ReadTextAsync(frame, layout.Area, cancel, positioned: true, pageMode: 11,
                trace: trace, regionName: "trackedRows");
            var lines = TrackedCamoRecognition.ReadTsv(text, height);
            byte[]? Thumbnail(double y) => Sample(frame, layout.Area.Item2 + y * layout.Area.Item4, layout.Thumbnail);
            var rows = TrackedCamoRecognition.ReadRows(lines, trackedCatalog, Thumbnail);
            var (x, y, w, h) = layout.Area;
            var counterArea = (x + w * .54, y, w * .46, h);
            var (counterText, counterHeight) = await ReadTextAsync(frame, counterArea, cancel,
                positioned: true, pageMode: 11, countersOnly: true, trace: trace, regionName: "trackedCounters");
            var counters = TrackedCamoRecognition.ReadTsv(counterText, counterHeight)
                .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line.Text, @"\A[0-9]{1,4}\s*/\s*[0-9]{1,4}\z"))
                .ToArray();
            var fallback = TrackedCamoRecognition.ReadRows(lines, trackedCatalog, Thumbnail, counters);
            var merged = TrackedCamoRecognition.MergeRows(rows, fallback);
            if (lobby)
            {
                var firstWeapon = lines.Where(line => line.Y < .10)
                    .Select(line => TrackedCamoRecognition.ReadWeapon(line.Text)).FirstOrDefault(name => name != null);
                if (firstWeapon == null || !merged.Any(row => row.Weapon == firstWeapon))
                {
                    // Isolate each line. Keep the full objective width for longer text such as "from Behind".
                    var (weapon, _) = await ReadTextAsync(frame, (x, .162, .077, .027), cancel, pageMode: 7,
                        trace: trace, regionName: "trackedFirstWeapon");
                    if ((TrackedCamoRecognition.ReadWeapon(weapon) ?? firstWeapon) is { } recovered &&
                        !merged.Any(row => row.Weapon == recovered))
                    {
                        var (objective, _) = await ReadTextAsync(frame, (x, .183, w, .030), cancel, pageMode: 7,
                            trace: trace, regionName: "trackedFirstObjective");
                        var firstCounterArea = (x + w * .66, .222, w * .34, .026);
                        var (counter, _) = await ReadTextAsync(frame, firstCounterArea, cancel,
                            pageMode: 7, countersOnly: true, trace: trace, regionName: "trackedFirstCounter");
                        if (!System.Text.RegularExpressions.Regex.IsMatch(counter.Trim(), @"\A[0-9]+\s*/\s*[0-9]+\z"))
                            (counter, _) = await ReadTextAsync(frame, firstCounterArea, cancel, pageMode: 7,
                                adjustment: PixelAdjustment.Contrast, trace: trace, regionName: "trackedFirstCounterContrast");
                        var first = TrackedCamoRecognition.ReadRows(
                            [new(recovered, 0), new(objective, .12)], trackedCatalog, static _ => null, [new(counter, .22)]);
                        merged = TrackedCamoRecognition.MergeRows(merged, first);
                    }
                }
            }
            if (merged.Length > 0) return merged;
        }
        return [];
    }

    private static byte[]? Sample(Bitmap frame, double top, (double X, double W, double H, double Offset) thumbnail)
    {
        var (x, w, h, offset) = thumbnail;
        top += offset;
        if (top < 0 || top + h > 1) return null;
        using var tiny = new Bitmap(12, 12, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(tiny))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var crop = new Rectangle((int)Math.Round((x + w * .17) * frame.Width),
                (int)Math.Round((top + h * .19) * frame.Height), (int)Math.Round(w * .66 * frame.Width),
                (int)Math.Round(h * .66 * frame.Height));
            if (crop.Width < 1 || crop.Height < 1) return null;
            graphics.DrawImage(frame, new Rectangle(0, 0, 12, 12), crop, GraphicsUnit.Pixel);
        }
        var rgb = new byte[432];
        for (var y = 0; y < 12; y++)
        for (var column = 0; column < 12; column++)
        {
            var pixel = tiny.GetPixel(column, y);
            var i = (y * 12 + column) * 3;
            rgb[i] = pixel.R; rgb[i + 1] = pixel.G; rgb[i + 2] = pixel.B;
        }
        return rgb;
    }

    private enum PixelAdjustment { NativeWhite, BadgeGold, BrightGold, BrightRedChannel, Normal, Contrast, Invert, CyanText, WhiteText, YellowText }

    private async Task<string[]> ReadAsync(Bitmap frame, (double X, double Y, double W, double H) area,
        CancellationToken cancel, PixelAdjustment adjustment = PixelAdjustment.Normal)
        => Lines((await ReadTextAsync(frame, area, cancel, adjustment: adjustment)).Text);

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private async Task<(string Text, int Height)> ReadTextAsync(Bitmap frame, (double X, double Y, double W, double H) area,
        CancellationToken cancel, bool positioned = false, int pageMode = 6, bool countersOnly = false,
        PixelAdjustment adjustment = PixelAdjustment.Normal, Action<string, string, Bitmap>? trace = null, string regionName = "")
    {
        var region = new Rectangle((int)Math.Round(area.X * frame.Width), (int)Math.Round(area.Y * frame.Height),
            Math.Max(1, (int)Math.Round(area.W * frame.Width)), Math.Max(1, (int)Math.Round(area.H * frame.Height)));
        var scale = adjustment is PixelAdjustment.BrightRedChannel or PixelAdjustment.BrightGold or PixelAdjustment.NativeWhite or PixelAdjustment.BadgeGold ? 1.0 : Math.Min(3.0, 2400.0 / Math.Max(region.Width, region.Height));
        using var crop = new Bitmap(Math.Max(1, (int)Math.Round(region.Width * scale)),
            Math.Max(1, (int)Math.Round(region.Height * scale)), PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(crop))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var destination = new Rectangle(Point.Empty, crop.Size);
            if (adjustment is PixelAdjustment.Normal or PixelAdjustment.CyanText or PixelAdjustment.WhiteText or PixelAdjustment.YellowText or PixelAdjustment.BrightRedChannel or PixelAdjustment.BrightGold or PixelAdjustment.NativeWhite or PixelAdjustment.BadgeGold)
                graphics.DrawImage(frame, destination, region, GraphicsUnit.Pixel);
            else
            {
                using var attributes = new ImageAttributes();
                var gain = adjustment == PixelAdjustment.Invert ? -1f : 1.6f;
                var offset = adjustment == PixelAdjustment.Invert ? 1f : -.36f;
                attributes.SetColorMatrix(new ColorMatrix
                {
                    Matrix00 = gain, Matrix11 = gain, Matrix22 = gain,
                    Matrix40 = offset, Matrix41 = offset, Matrix42 = offset
                });
                graphics.DrawImage(frame, destination, region.X, region.Y, region.Width, region.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        if (adjustment is PixelAdjustment.CyanText or PixelAdjustment.WhiteText or PixelAdjustment.YellowText or PixelAdjustment.BrightRedChannel or PixelAdjustment.BrightGold or PixelAdjustment.NativeWhite or PixelAdjustment.BadgeGold)
        {
            var pixels = crop.LockBits(new Rectangle(Point.Empty, crop.Size), ImageLockMode.ReadWrite, crop.PixelFormat);
            try
            {
                var row = new byte[crop.Width * 3];
                for (var y = 0; y < crop.Height; y++)
                {
                    var address = pixels.Scan0 + y * pixels.Stride;
                    Marshal.Copy(address, row, 0, row.Length);
                    for (var x = 0; x < row.Length; x += 3)
                    {
                        var ink = adjustment switch
                        {
                            PixelAdjustment.NativeWhite => Math.Min(row[x], Math.Min(row[x + 1], row[x + 2])) > 220,
                            PixelAdjustment.BadgeGold => row[x + 2] > 180 && row[x + 1] > 180 && row[x + 1] - row[x] > 35,
                            PixelAdjustment.BrightGold => row[x + 2] > 215 && row[x + 1] > 215 && row[x + 1] - row[x] > 25,
                            PixelAdjustment.BrightRedChannel => row[x + 2] > 205,
                            PixelAdjustment.WhiteText => Math.Min(row[x], Math.Min(row[x + 1], row[x + 2])) >= 205,
                            PixelAdjustment.YellowText => Math.Min(row[x + 1], row[x + 2]) >= 155 &&
                                Math.Min(row[x + 1], row[x + 2]) - row[x] >= 24,
                            _ => row[x] >= 165 && row[x + 1] >= 165 &&
                                Math.Min(row[x], row[x + 1]) - row[x + 2] >= 22
                        };
                        row[x] = row[x + 1] = row[x + 2] = ink ? (byte)0 : (byte)255;
                    }
                    Marshal.Copy(row, 0, address, row.Length);
                }
            }
            finally { crop.UnlockBits(pixels); }
        }
        using var encoded = new MemoryStream();
        // The in-process decoder consumes these pixels immediately; PNG compression
        // only saves bandwidth on the pipe used by the CLI fallback.
        crop.Save(encoded, persistentOcr == null ? ImageFormat.Png : ImageFormat.Bmp);
        if (persistentOcr != null && encoded.TryGetBuffer(out var image))
        {
            var text = await persistentOcr.ReadAsync(image, pageMode, cancel,
                countersOnly ? "0123456789/" : "", positioned);
            trace?.Invoke(regionName, text.Trim(), crop);
            return (text, crop.Height);
        }
        encoded.Position = 0;
        var start = new ProcessStartInfo(ocr)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["OMP_THREAD_LIMIT"] = "1";
        foreach (var argument in new[] { "stdin", "stdout", "--tessdata-dir", models, "-l", "eng", "--psm", pageMode.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        if (countersOnly)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("tessedit_char_whitelist=0123456789/");
        }
        if (positioned)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("tessedit_create_tsv=1");
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start OCR.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await encoded.CopyToAsync(process.StandardInput.BaseStream, deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new IOException("OCR failed: " + (await error).Trim());
            var text = await output;
            trace?.Invoke(regionName, text.Trim(), crop);
            return (text, crop.Height);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
