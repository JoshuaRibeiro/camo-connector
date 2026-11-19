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
        persistentOcr = PersistentOcr.Open(runtime, models);
    }

    public void Dispose() => persistentOcr?.Dispose();

    public static Bitmap Desktop(Screen screen)
    {
        var bounds = screen.Bounds;
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
        foreach (var area in new[] { (.742, .876, .082, .026), (.782, .877, .048, .026) })
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

    public async Task<string?> DetectCompletionistGoldAsync(Bitmap frame, string? weapon, CancellationToken cancel)
    {
        if (weapon == null) return null;
        var completion = string.Join(" ", await ReadAsync(frame, (.070, .550, .110, .040), cancel));
        return CombatRecognition.HasCompletionistGold(completion) ? weapon : null;
    }

    public async Task<SpecialUnlock[]> DetectSpecialUnlocksAsync(Bitmap frame, string? weapon, CancellationToken cancel)
    {
        if (weapon == null) return [];
        var text = string.Join(" ", await ReadAsync(frame, (.070, .389, .090, .039), cancel));
        var count = SpecialUnlocks.ReadCount(text);
        if (count is not > 0) return [];
        var scores = count == 3 ? [] : new[] { .075, .150, .225 }.Select(x => PadlockScore(frame, x)).ToArray();
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

    public async Task<(string? Challenge, bool Completed, string[]? Details)> DetectChallengeBannerAsync(Bitmap frame, CancellationToken cancel,
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
        var completed = LooksCompleted(heading);
        // HUD notices can cover the middle of the completion text. Require the camo artwork as corroboration.
        if (!completed && (heading.Contains("CHALLEN", StringComparison.OrdinalIgnoreCase) ||
            heading.Contains("OMPLETED", StringComparison.OrdinalIgnoreCase)))
            completed = !goldTile && IdentifySpecialBanner(frame, null, null, bannerArtworkOnly: true) != null;
        if (!goldTile && !completed) return (null, false, null);
        var detail = await ReadBannerText((.395, .300, .210, .020), "unlockObjective");
        string? Match(string text) => CombatRecognition.IsGoldBanner(heading, text, goldTile)
            ? "gold-15" : SpecialUnlocks.ReadBanner(heading, text);
        if (Match(detail) is { } match) return (match, true, null);
        var details = new List<string> { detail };
        foreach (var adjustment in new[] { PixelAdjustment.Normal, PixelAdjustment.Contrast, PixelAdjustment.Invert })
        {
            detail = await ReadBannerText((.382, .301, .237, .022), $"unlockObjective{adjustment}", adjustment);
            if (Match(detail) is { } fallback) return (fallback, true, null);
            details.Add(detail);
        }
        return (null, completed && !goldTile, details.ToArray());

        static bool LooksCompleted(string text) => System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\bCOMPLETED\b|\bCHALLENGE\w{0,2}\s+LETED\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public SpecialUnlock? IdentifySpecialBanner(Bitmap frame, string? weapon, string[]? details, bool bannerArtworkOnly = false)
    {
        var sample = Sample(frame, .148, (.471, .058, .102, 0));
        if (weapon != null && catalog.TryGetValue(weapon, out var entries))
        {
            var name = SpecialUnlocks.ReadNamedBanner(details, entries) ?? MatchBannerArtwork(entries);
            return name != null
                ? new(weapon, name, "catalog-special-unlocked") : null;
        }
        var candidates = catalog.Values.SelectMany(entries => entries).ToArray();
        var unique = SpecialUnlocks.ReadNamedBanner(details, candidates) ?? MatchBannerArtwork(candidates);
        if (unique == null) return null;
        var weapons = catalog.Where(pair => pair.Value.Any(entry => entry.Name == unique)).Take(2).ToArray();
        return weapons.Length == 1
            ? new(weapons[0].Key, unique, "catalog-special-unlocked") : null;

        string? MatchBannerArtwork(IEnumerable<CamoReference> candidates) => TrackedCamoRecognition.MatchArtwork(sample,
            candidates.Where(entry => !bannerArtworkOnly || entry.BannerRgb != null)
                .Select(entry => entry.BannerRgb == null ? entry : entry with { Rgb = entry.BannerRgb }).ToArray());
    }

    public async Task<CamoProgress?> DetectCamoProgressAsync(Bitmap frame, string? weapon, CancellationToken cancel,
        Action<string, string, Bitmap>? trace = null)
    {
        if (weapon == null) return null;
        var names = catalog.GetValueOrDefault(weapon)?.Select(camo => camo.Name).ToArray() ?? [];
        var (titleText, _) = await ReadTextAsync(frame, (.344, .628, .310, .041), cancel, pageMode: 7,
            trace: trace, regionName: "camoTitle");
        var title = CamoMenuRecognition.ReadKnownCamoTitle(titleText, names);
        if (title == null)
        {
            var (wideTitle, _) = await ReadTextAsync(frame, (.312, .614, .472, .064), cancel,
                trace: trace, regionName: "wideCamoTitle");
            title = CamoMenuRecognition.ReadKnownCamoTitle(wideTitle, names) ?? string.Join(" ", Lines(wideTitle));
        }
        if (title.Length == 0) return null;
        var (objectives, _) = await ReadTextAsync(frame, (.313, .679, .342, .181), cancel,
            trace: trace, regionName: "camoObjectives");
        var progress = CamoMenuRecognition.ReadProgress(weapon, title, Lines(objectives), names);
        if (!names.Contains(title)) return progress;

        // Check the isolated counter even when the wider objective OCR returns a plausible value.
        async Task<CamoProgress?> ReadCounter(PixelAdjustment adjustment, string region)
        {
            var (counter, _) = await ReadTextAsync(frame, (.512, .795, .060, .045), cancel, pageMode: 7,
                countersOnly: true, adjustment: adjustment, trace: trace, regionName: region);
            return System.Text.RegularExpressions.Regex.IsMatch(counter.Trim(), @"\A[0-9]+\s*/\s*[0-9]+\z")
                ? CamoMenuRecognition.ReadProgress(weapon, title, [counter], names) : null;
        }

        var catalogGoal = catalog.GetValueOrDefault(weapon)?.FirstOrDefault(camo => camo.Name == title)?.Goal;
        CamoProgress? Validate(CamoProgress? candidate) => CamoMenuRecognition.CheckObjectiveGoal(candidate, catalogGoal, objectives);
        var isolated = await ReadCounter(PixelAdjustment.Normal, "camoCounter");
        if (progress != null || isolated == null)
            return Validate(CamoMenuRecognition.ConfirmCounter(progress, isolated, null));
        // If the wider read contains a rejected fraction, avoid substituting a different row's count.
        if (System.Text.RegularExpressions.Regex.IsMatch(objectives, @"\d\s*/\s*\d")) return null;
        var alternate = await ReadCounter(PixelAdjustment.Contrast, "camoCounterContrast");
        return Validate(CamoMenuRecognition.ConfirmCounter(null, isolated, alternate));
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
                (Heading: (.654, .383, .095, .035), Area: (.714, .432, .135, .278), Thumbnail: (.670, .039, .069, .004)),
                (Heading: (.740, .117, .110, .037), Area: (.792, .159, .172, .335), Thumbnail: (.760, .039, .069, .004)),
                (Heading: (.067, .114, .110, .038), Area: (.121, .156, .169, .335), Thumbnail: (.084, .039, .069, .004))
            };
        for (var i = 0; i < layouts.Length; i++)
        {
            var layout = layouts[i];
            var (heading, _) = await ReadTextAsync(frame, layout.Heading, cancel, trace: trace, regionName: $"trackedHeading{i + 1}");
            if (!TrackedCamoRecognition.IsPanel(heading)) continue;
            var (text, height) = await ReadTextAsync(frame, layout.Area, cancel, positioned: true, pageMode: 11,
                trace: trace, regionName: "trackedRows");
            var lines = TrackedCamoRecognition.ReadTsv(text, height);
            byte[]? Thumbnail(double y) => Sample(frame, layout.Area.Item2 + y * layout.Area.Item4, layout.Thumbnail);
            var rows = TrackedCamoRecognition.ReadRows(lines, catalog, Thumbnail);
            var (x, y, w, h) = layout.Area;
            var counterArea = (x + w * .54, y, w * .42, h);
            var (counterText, counterHeight) = await ReadTextAsync(frame, counterArea, cancel,
                positioned: true, pageMode: 11, countersOnly: true, trace: trace, regionName: "trackedCounters");
            var counters = TrackedCamoRecognition.ReadTsv(counterText, counterHeight)
                .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line.Text, @"\A[0-9]{1,4}\s*/\s*[0-9]{1,4}\z"))
                .ToArray();
            var fallback = TrackedCamoRecognition.ReadRows(lines, catalog, Thumbnail, counters);
            var merged = TrackedCamoRecognition.MergeRows(rows, fallback);
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

    private enum PixelAdjustment { Normal, Contrast, Invert, CyanText, WhiteText, YellowText }

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
        var scale = Math.Min(3.0, 2400.0 / Math.Max(region.Width, region.Height));
        using var crop = new Bitmap(Math.Max(1, (int)Math.Round(region.Width * scale)),
            Math.Max(1, (int)Math.Round(region.Height * scale)), PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(crop))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var destination = new Rectangle(Point.Empty, crop.Size);
            if (adjustment is PixelAdjustment.Normal or PixelAdjustment.CyanText or PixelAdjustment.WhiteText or PixelAdjustment.YellowText)
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
        if (adjustment is PixelAdjustment.CyanText or PixelAdjustment.WhiteText or PixelAdjustment.YellowText)
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
        using var png = new MemoryStream();
        crop.Save(png, ImageFormat.Png);
        if (persistentOcr != null && !positioned && !countersOnly && png.TryGetBuffer(out var image))
        {
            var text = await persistentOcr.ReadAsync(image, pageMode, cancel);
            trace?.Invoke(regionName, text.Trim(), crop);
            return (text, crop.Height);
        }
        png.Position = 0;
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
            await png.CopyToAsync(process.StandardInput.BaseStream, deadline.Token);
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
