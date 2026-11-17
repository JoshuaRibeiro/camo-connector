using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CamoConnector.Detector;

internal static class CapturePreview
{
    public static async Task RunAsync(Func<CancellationToken, Task<Bitmap>> next, CancellationToken cancel)
    {
        using var output = Console.OpenStandardOutput();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(66));
        using var encoded = new MemoryStream();
        var header = new byte[4];
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
            BinaryPrimitives.WriteInt32LittleEndian(header, (int)encoded.Length);
            await output.WriteAsync(header, cancel);
            await output.WriteAsync(encoded.GetBuffer().AsMemory(0, (int)encoded.Length), cancel);
        } while (await timer.WaitForNextTickAsync(cancel));
    }
}
