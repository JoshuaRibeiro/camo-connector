using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CamoConnector.Updater;

internal sealed class UpdateWindow : Form
{
    private readonly Label status;
    private readonly Font bodyFont = new("Bahnschrift", 10);
    private readonly bool contrast = SystemInformation.HighContrast;

    public UpdateWindow(Version? expected, bool recovery)
    {
        Text = recovery ? "Recovering CoD Streaming Tools" : $"Updating CoD Streaming Tools to v{expected}";
        Font = bodyFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(340, 220);
        BackColor = contrast ? SystemColors.Control : Color.FromArgb(17, 20, 25);
        ForeColor = contrast ? SystemColors.ControlText : Color.FromArgb(215, 226, 232);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(22, 22, 22, 16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new AnimatedLogo(contrast) { Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
        status = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty,
            Text = "Waiting for CoD Streaming Tools to close…"
        };
        layout.Controls.Add(status, 0, 1);
        Controls.Add(layout);
    }

    public void Report(string message) => status.Text = message;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (contrast) return;
        var corners = 2;
        DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) bodyFont.Dispose();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private sealed class AnimatedLogo : Control
    {
        private readonly Bitmap logo;
        private readonly Color accent;
        private readonly System.Windows.Forms.Timer animation = new() { Interval = 33 };
        private int angle;

        public AnimatedLogo(bool contrast)
        {
            DoubleBuffered = true;
            AccessibleName = "CoD Streaming Tools logo";
            AccessibleRole = AccessibleRole.Graphic;
            accent = contrast ? SystemColors.ControlText : Color.FromArgb(141, 215, 206);
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CamoConnector.AppIcon")!;
            using var icon = new Icon(resource, 64, 64);
            logo = icon.ToBitmap();
            animation.Tick += (_, _) =>
            {
                angle = (angle + 6) % 360;
                Invalidate();
            };
            animation.Enabled = !contrast && SystemInformation.IsMenuAnimationEnabled;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var size = Math.Min(LogicalToDeviceUnits(106), Math.Min(Width, Height) - 6);
            if (size < 4) return;
            var center = new PointF(Width / 2f, Height / 2f);
            var ring = new RectangleF(center.X - size / 2f, center.Y - size / 2f, size, size);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var track = new Pen(Color.FromArgb(56, accent), LogicalToDeviceUnits(2));
            using var sweep = new Pen(accent, LogicalToDeviceUnits(2))
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(track, ring);
            e.Graphics.DrawArc(sweep, ring, angle, 76);
            e.Graphics.DrawArc(sweep, ring, angle + 180, 30);

            var logoSize = size * (0.59f + 0.035f * MathF.Sin(angle * MathF.PI / 180f));
            var face = new RectangleF(center.X - logoSize / 2, center.Y - logoSize / 2, logoSize, logoSize);
            e.Graphics.DrawImage(logo, face);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animation.Dispose();
                logo.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
