using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CamoConnector.Detector;

internal sealed class CapturePreview(string directory)
{
    private readonly string target = Path.Combine(Directory.CreateDirectory(directory).FullName, "preview.jpg");

    public void Publish(Bitmap frame)
    {
        var width = Math.Min(640, frame.Width);
        var height = Math.Max(1, (int)Math.Round(frame.Height * (double)width / frame.Width));
        using var reduced = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(reduced))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(frame, 0, 0, width, height);
        }
        var temporary = target + ".tmp";
        try
        {
            reduced.Save(temporary, ImageFormat.Jpeg);
            File.Move(temporary, target, true);
        }
        finally { File.Delete(temporary); }
    }
}
