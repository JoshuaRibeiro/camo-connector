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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rasterOperation);

    public async Task<(WeaponRead? Weapon, bool Replay)> DetectAsync(Bitmap frame, CancellationToken cancel)
    {
        // Read the current weapon label below the ammo counter before wider HUD regions.
        var labelArea = (.683, .876, .141, .027);
        foreach (var adjustment in new[] { PixelAdjustment.Normal, PixelAdjustment.Contrast })
        {
            var (label, _) = await ReadTextAsync(frame, labelArea, cancel, pageMode: 7, adjustment: adjustment);
            if (WeaponRecognition.Read(Lines(label), false) is { } weapon)
                return await IsReplayAsync(frame, cancel) ? (null, true) : (weapon, false);
        }
        foreach (var area in WeaponAreas)
        {
            var read = WeaponRecognition.Read(await ReadAsync(frame, area, cancel), area.W >= .15);
            if (read != null) return await IsReplayAsync(frame, cancel) ? (null, true) : (read, false);
        }
        // Short labels need less background in the crop to separate them from the ammo display.
        foreach (var area in new[] { (.742, .876, .082, .026), (.782, .877, .048, .026) })
        {
            var (label, _) = await ReadTextAsync(frame, area, cancel, pageMode: 7);
            if (WeaponRecognition.Read(Lines(label), false) is { } weapon)
                return await IsReplayAsync(frame, cancel) ? (null, true) : (weapon, false);
        }
        var (shortLabel, _) = await ReadTextAsync(frame, (.782, .877, .048, .026), cancel,
            pageMode: 7, adjustment: PixelAdjustment.Contrast);
        if (WeaponRecognition.Read(Lines(shortLabel), false) is { Name: ".50 GS" } pistol)
            return await IsReplayAsync(frame, cancel) ? (null, true) : (pistol, false);
        return (null, false);
    }

    public async Task<MilitaryUnlock?> DetectMilitaryUnlockAsync(Bitmap frame, CancellationToken cancel,
        bool allowCroppedImage = false)
    {
        var (header, _) = await ReadTextAsync(frame, (.900, .210, .080, .041), cancel, pageMode: 7,
            adjustment: PixelAdjustment.WhiteText);
        MilitaryUnlock? badge = null;
        if (WeaponRecognition.IsUnlockHeader(header))
        {
            var body = await ReadAsync(frame, (.833, .242, .144, .065), cancel);
            badge = WeaponRecognition.ReadMilitaryUnlock([header, .. body]);
        }
        badge ??= WeaponRecognition.ReadMilitaryUnlock(await ReadAsync(frame, (.827, .045, .155, .405), cancel));
        if (badge == null) badge = await DetectMilitarySummaryAsync(frame, cancel);
        if (badge == null && allowCroppedImage)
            return WeaponRecognition.ReadMilitaryUnlock(await ReadAsync(frame, (0, 0, 1, 1), cancel));
        return badge;
    }

    private async Task<MilitaryUnlock?> DetectMilitarySummaryAsync(Bitmap frame, CancellationToken cancel)
    {
        var (heading, _) = await ReadTextAsync(frame, (.052, .080, .142, .039), cancel, pageMode: 7);
        if (!WeaponRecognition.IsWeaponSummary(heading)) return null;
        var (weapon, _) = await ReadTextAsync(frame, (.048, .128, .285, .051), cancel, pageMode: 7);
        foreach (var adjustment in new[] { PixelAdjustment.Normal, PixelAdjustment.Contrast, PixelAdjustment.Invert })
        {
            var (card, _) = await ReadTextAsync(frame, (.453, .652, .094, .026), cancel, pageMode: 7,
                adjustment: adjustment);
            if (WeaponRecognition.ReadMilitarySummary(heading, weapon, card) is { } unlock) return unlock;
        }
        return null;
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

    private static double PadlockScore(Bitmap frame, double tileX)
    {
        var best = 0d;
        const int count = 140;
        var targetMean = Padlock.Average(value => (double)value);
        var targetVariance = Padlock.Sum(value => Math.Pow(value - targetMean, 2));
        for (var dx = -6; dx <= 9; dx++)
        for (var dy = 0; dy <= 12; dy++)
        {
            var x = tileX + dx * .00085;
            var y = .426 + dy * .0011;
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
        if (progress != null || !names.Contains(title) ||
            System.Text.RegularExpressions.Regex.IsMatch(objectives, @"\d\s*/\s*\d")) return progress;
        // The Special counter is at the right end of the objective's progress bar.
        var (counter, _) = await ReadTextAsync(frame, (.512, .795, .060, .045), cancel, pageMode: 7,
            countersOnly: true, trace: trace, regionName: "camoCounter");
        return System.Text.RegularExpressions.Regex.IsMatch(counter.Trim(), @"\A[0-9]+\s*/\s*[0-9]+\z")
            ? CamoMenuRecognition.ReadProgress(weapon, title, [counter], names) : null;
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
        return CamoMenuRecognition.ReadMilitaryMenuCount(military);
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

    private enum PixelAdjustment { Normal, Contrast, Invert, CyanText, WhiteText }

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
            if (adjustment is PixelAdjustment.Normal or PixelAdjustment.CyanText or PixelAdjustment.WhiteText)
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
        if (adjustment is PixelAdjustment.CyanText or PixelAdjustment.WhiteText)
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
                        var ink = adjustment == PixelAdjustment.WhiteText
                            ? Math.Min(row[x], Math.Min(row[x + 1], row[x + 2])) >= 205
                            : row[x] >= 165 && row[x + 1] >= 165 &&
                            Math.Min(row[x], row[x + 1]) - row[x + 2] >= 22;
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
