using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CamoConnector.Detector;

internal static class CapturePreview
{
    public static async Task RunAsync(Func<CancellationToken, Task<Bitmap>> next, CancellationToken cancel,
        bool filterIsolatedFrames = false, Action<Bitmap>? onAccepted = null, string? trophyPreference = null)
    {
        using var output = Console.OpenStandardOutput();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        using var encoded = new MemoryStream();
        var header = new byte[4];
        Color[]? previous = null, pendingSample = null;
        var sentFirstFrame = false;
        var suppressed = 0;
        var lastReport = Stopwatch.GetTimestamp();
        Bitmap? pendingFrame = null;
        var lastTrophySample = Stopwatch.GetTimestamp();
        try
        {
            do
            {
                using var frame = await next(cancel);
                var width = Math.Min(640, frame.Width);
                var height = Math.Max(1, (int)Math.Round(frame.Height * (double)width / frame.Width));
                using var reduced = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(reduced))
                {
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    graphics.DrawImage(frame, 0, 0, width, height);
                }
                encoded.SetLength(0);
                reduced.Save(encoded, ImageFormat.Jpeg);
                var bytes = encoded.GetBuffer().AsMemory(0, (int)encoded.Length);
                await SendAsync(bytes);
                if (trophyPreference != null && Stopwatch.GetElapsedTime(lastTrophySample) >= TimeSpan.FromMilliseconds(150))
                {
                    lastTrophySample = Stopwatch.GetTimestamp();
                    if (TrophyTrackingEnabled(trophyPreference))
                    {
                        encoded.SetLength(0);
                        frame.Save(encoded, ImageFormat.Jpeg);
                        if (encoded.Length <= 16 * 1024 * 1024)
                            await SendAsync(encoded.GetBuffer().AsMemory(0, (int)encoded.Length), trophy: true);
                    }
                }
                if (!filterIsolatedFrames) continue;

                // Stabilize the frames used by OCR without holding back the live preview.
                var sample = Sample(reduced);
                if (pendingSample != null)
                {
                    if (previous == null || !IsIsolatedChange(previous, pendingSample, sample))
                    {
                        previous = pendingSample;
                        if (pendingFrame != null) onAccepted!(pendingFrame);
                    }
                    else suppressed++;
                }
                if (Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromSeconds(5))
                {
                    if (suppressed > 0)
                        Console.Error.WriteLine($"Capture preview: suppressed {suppressed} isolated frame(s) in the last five seconds.");
                    lastReport = Stopwatch.GetTimestamp();
                    suppressed = 0;
                }
                pendingFrame?.Dispose();
                pendingFrame = onAccepted == null ? null : (Bitmap)frame.Clone();
                pendingSample = sample;
            } while (await timer.WaitForNextTickAsync(cancel));
        }
        finally { pendingFrame?.Dispose(); }

        async Task SendAsync(ReadOnlyMemory<byte> bytes, bool trophy = false)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, trophy ? -bytes.Length : bytes.Length);
            await output.WriteAsync(header, cancel);
            await output.WriteAsync(bytes, cancel);
            if (!sentFirstFrame)
            {
                sentFirstFrame = true;
                Console.Error.WriteLine(filterIsolatedFrames
                    ? "Capture preview: first frame delivered. OCR frame filter active."
                    : "Capture preview: first frame delivered.");
            }
        }
    }

    private static bool TrophyTrackingEnabled(string preference)
    {
        try { return File.Exists(preference) && File.ReadAllText(preference).Trim() == "on"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static Color[] Sample(Bitmap frame)
    {
        const int columns = 24, rows = 14;
        var sample = new Color[columns * rows];
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
            sample[row * columns + column] = frame.GetPixel(
                (2 * column + 1) * frame.Width / (2 * columns),
                (2 * row + 1) * frame.Height / (2 * rows));
        return sample;
    }

    private static bool IsIsolatedChange(Color[] before, Color[] middle, Color[] after)
    {
        var changed = 0;
        var returned = 0;
        for (var i = 0; i < before.Length; i++)
        {
            if (Distance(before[i], middle[i]) < 75) continue;
            changed++;
            if (Distance(before[i], after[i]) <= 30 && Distance(after[i], middle[i]) >= 60)
                returned++;
        }
        return changed >= 8 && returned * 4 >= changed * 3;
    }

    private static int Distance(Color first, Color second) =>
        Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);
}
