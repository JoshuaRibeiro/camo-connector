using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace CamoConnector.Detector;

internal sealed class HudCapture
{
    private static readonly byte[] Padlock = Convert.FromBase64String(
        "fX2AVi+Af35+e3194fv5+frjIRd9wuOAMixf3GkWfcytTVAxILxxLX3Mz9XGyNDy5FN8zPz///////56fuz////////+ZoT+///5+P///ml8/v//x6n///5phf7//8Wn///+doT+//+0lf///oZJ/v//6+D///dsK/7+/v7///1uLSz7+/v7/PyfTRY=");
    private static readonly (double X, double Y, double W, double H)[] WeaponAreas =
        { (.705, .864, .205, .053), (.727, .875, .098, .031), (.747, .869, .077, .044) };
    private readonly string ocr;
    private readonly string models;
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
                    camo.TryGetProperty("goal", out var goal) && goal.ValueKind == JsonValueKind.Number && goal.TryGetInt32(out var goalNumber) ? goalNumber : null)).ToArray());
    }

    public static Bitmap Desktop(Screen screen)
    {
        var bounds = screen.Bounds;
        var frame = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(frame);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    public async Task<(WeaponRead? Weapon, bool Replay)> DetectAsync(Bitmap frame, CancellationToken cancel)
    {
        foreach (var area in WeaponAreas)
        {
            var read = WeaponRecognition.Read(await ReadAsync(frame, area, cancel), area.W >= .15);
            if (read == null) continue;
            return await IsReplayAsync(frame, cancel) ? (null, true) : (read, false);
        }
        return (null, false);
    }

    public async Task<MilitaryUnlock?> DetectMilitaryUnlockAsync(Bitmap frame, CancellationToken cancel,
        bool allowCroppedImage = false)
    {
        var badge = WeaponRecognition.ReadMilitaryUnlock(await ReadAsync(frame, (.827, .045, .155, .405), cancel));
        if (badge == null && allowCroppedImage)
            return WeaponRecognition.ReadMilitaryUnlock(await ReadAsync(frame, (0, 0, 1, 1), cancel));
        return badge;
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
        return CombatRecognition.ReadLobby(string.Join(" ", await ReadAsync(frame, (0, 0, .99, .42), cancel)));
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

    public async Task<string?> DetectChallengeBannerAsync(Bitmap frame, CancellationToken cancel)
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
        var heading = string.Join(" ", await ReadAsync(frame, (.350, .250, .300, .047), cancel));
        if (!heading.Contains("COMPLETED", StringComparison.OrdinalIgnoreCase))
            heading = string.Join(" ", await ReadAsync(frame, (.366, .250, .269, .047), cancel, adjustment: PixelAdjustment.Invert));
        if (!goldTile && !heading.Contains("COMPLETED", StringComparison.OrdinalIgnoreCase)) return null;
        var detail = string.Join(" ", await ReadAsync(frame, (.382, .295, .237, .034), cancel));
        string? Match(string text) => CombatRecognition.IsGoldBanner(heading, text, goldTile)
            ? "gold-15" : SpecialUnlocks.ReadBanner(heading, text);
        if (Match(detail) is { } match) return match;
        foreach (var adjustment in new[] { PixelAdjustment.Normal, PixelAdjustment.Contrast, PixelAdjustment.Invert })
        {
            detail = string.Join(" ", await ReadAsync(frame, (.382, .301, .237, .022), cancel, adjustment));
            if (Match(detail) is { } fallback) return fallback;
        }
        return null;
    }

    public async Task<CamoProgress?> DetectCamoProgressAsync(Bitmap frame, string? weapon, CancellationToken cancel)
    {
        if (weapon == null) return null;
        var title = string.Join(" ", await ReadAsync(frame, (.344, .614, .440, .064), cancel));
        if (title.Length == 0) return null;
        var lines = await ReadAsync(frame, (.345, .709, .310, .151), cancel);
        var names = catalog.GetValueOrDefault(weapon)?.Select(camo => camo.Name).ToArray() ?? [];
        return CamoMenuRecognition.ReadProgress(weapon, title, lines, names);
    }

    public async Task<string?> ReadCamoMenuWeaponAsync(Bitmap frame, CancellationToken cancel)
    {
        var heading = string.Join(" ", await ReadAsync(frame, (.070, .043, .182, .045), cancel));
        if (!CamoMenuRecognition.IsCamoMenu(heading)) return null;
        var tab = string.Join(" ", await ReadAsync(frame, (.082, .106, .177, .027), cancel));
        return CamoMenuRecognition.ReadProgressionWeapon(tab);
    }

    public async Task<CamoProgress[]> DetectTrackedCamosAsync(Bitmap frame, CancellationToken cancel, bool savedImage = false)
    {
        var layouts = savedImage && (double)frame.Width / frame.Height < 1.4
            ? new[] { (Heading: (.02, .01, .36, .09), Area: (.24, .13, .50, .70), Thumbnail: (.095, .145, .155, .005)) }
            : new[]
            {
                (Heading: (.740, .117, .110, .037), Area: (.792, .159, .172, .335), Thumbnail: (.760, .039, .069, .004)),
                (Heading: (.067, .114, .110, .038), Area: (.121, .156, .169, .335), Thumbnail: (.084, .039, .069, .004))
            };
        foreach (var layout in layouts)
        {
            var heading = string.Join(" ", await ReadAsync(frame, layout.Heading, cancel));
            if (!TrackedCamoRecognition.IsPanel(heading)) continue;
            var (text, height) = await ReadTextAsync(frame, layout.Area, cancel, positioned: true, pageMode: 11);
            var lines = TrackedCamoRecognition.ReadTsv(text, height);
            byte[]? Thumbnail(double y) => Sample(frame, layout.Area.Item2 + y * layout.Area.Item4, layout.Thumbnail);
            var rows = TrackedCamoRecognition.ReadRows(lines, catalog, Thumbnail);
            var (x, y, w, h) = layout.Area;
            var counterArea = (x + w * .61, y, w * .22, h);
            var (counterText, counterHeight) = await ReadTextAsync(frame, counterArea, cancel,
                positioned: true, pageMode: 11, countersOnly: true);
            var counters = TrackedCamoRecognition.ReadTsv(counterText, counterHeight)
                .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line.Text, @"\A[0-9]{1,4}\s*/\s*[0-9]{1,4}\z"));
            var fallback = TrackedCamoRecognition.ReadRows(lines.Concat(counters).OrderBy(line => line.Y).ToArray(),
                catalog, Thumbnail);
            return rows.Concat(fallback.Where(candidate => !rows.Any(confirmed => confirmed.Weapon == candidate.Weapon))).ToArray();
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

    private enum PixelAdjustment { Normal, Contrast, Invert }

    private async Task<string[]> ReadAsync(Bitmap frame, (double X, double Y, double W, double H) area,
        CancellationToken cancel, PixelAdjustment adjustment = PixelAdjustment.Normal)
        => (await ReadTextAsync(frame, area, cancel, adjustment: adjustment)).Text.Split('\n',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private async Task<(string Text, int Height)> ReadTextAsync(Bitmap frame, (double X, double Y, double W, double H) area,
        CancellationToken cancel, bool positioned = false, int pageMode = 6, bool countersOnly = false,
        PixelAdjustment adjustment = PixelAdjustment.Normal)
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
            if (adjustment == PixelAdjustment.Normal)
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
        using var png = new MemoryStream();
        crop.Save(png, ImageFormat.Png);
        png.Position = 0;
        var start = new ProcessStartInfo(ocr)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
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
            return (await output, crop.Height);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
