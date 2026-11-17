using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CamoConnector.Detector;

internal sealed class ExternalCapture : IAsyncDisposable
{
    private const int Width = 1280, Height = 720, Stride = Width * 3;
    private readonly Process process;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task reader, errorReader;
    private readonly object gate = new();
    private byte[]? latest;
    private long serial, consumed, previewConsumed;
    private DateTimeOffset timestamp;
    private string error = "";

    private ExternalCapture(Process process)
    {
        this.process = process;
        reader = ReadFramesAsync();
        errorReader = ReadErrorsAsync();
    }

    public static async Task<CaptureDevice[]> ListAsync(string executable, CancellationToken cancel)
    {
        using var process = Launch(executable, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return CaptureDevices.Parse(await error);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    public static ExternalCapture Open(string executable, string input) => new(Launch(executable,
        ["-hide_banner", "-loglevel", "warning", "-nostdin", "-f", "dshow", "-rtbufsize", "128M",
            "-i", "video=" + input, "-an", "-vf",
            "fps=15,scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2",
            "-threads", "1", "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"]));

    public static ExternalCapture OpenVideoForTest(string executable, string input) => new(Launch(executable,
        ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", input, "-an", "-vf",
            "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2",
            "-threads", "1", "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1"]));

    private static Process Launch(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start the capture runtime.");
    }

    public async Task<Bitmap> NextAsync(CancellationToken cancel, bool forPreview = false)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (latest != null && serial != (forPreview ? previewConsumed : consumed) && DateTimeOffset.UtcNow - timestamp < TimeSpan.FromSeconds(3))
                {
                    var result = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
                    try
                    {
                        var bitmap = result.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, result.PixelFormat);
                        try
                        {
                            for (var row = 0; row < Height; row++)
                                Marshal.Copy(latest, row * Stride, bitmap.Scan0 + row * bitmap.Stride, Stride);
                        }
                        finally { result.UnlockBits(bitmap); }
                        if (forPreview) previewConsumed = serial;
                        else consumed = serial;
                        return result;
                    }
                    catch { result.Dispose(); throw; }
                }
            }
            if (reader.IsCompleted)
            {
                await reader;
                await errorReader;
                throw new IOException("Capture stopped. " + error);
            }
            await Task.Delay(12, cancel);
        }
        throw new IOException("No fresh capture frame. Check the device and its video signal. " + error);
    }

    private async Task ReadFramesAsync()
    {
        var pending = new byte[Stride * Height];
        var stream = process.StandardOutput.BaseStream;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(pending, stopping.Token);
                lock (gate)
                {
                    var previous = latest;
                    latest = pending;
                    pending = previous ?? new byte[pending.Length];
                    timestamp = DateTimeOffset.UtcNow;
                    serial++;
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ReadErrorsAsync()
    {
        var recent = new Queue<string>();
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (recent.Count == 5) recent.Dequeue();
            recent.Enqueue(line.Length > 320 ? line[..320] : line);
            error = string.Join(Environment.NewLine, recent);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        finally
        {
            try { await Task.WhenAll(reader, errorReader); }
            finally { process.Dispose(); stopping.Dispose(); }
        }
    }
}
