using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CamoConnector.Updater;

internal sealed class UpdateWindow : Form
{
    private readonly Label status;
    private readonly UpdateMeter meter;
    private readonly Font bodyFont = new("Bahnschrift", 10);
    private readonly Font headingFont = new("Bahnschrift", 18, FontStyle.Bold);
    private readonly bool contrast = SystemInformation.HighContrast;

    public UpdateWindow(Version? expected, bool recovery)
    {
        Text = "Camo Connector";
        Font = bodyFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ClientSize = new Size(470, 180);
        BackColor = contrast ? SystemColors.Control : Color.FromArgb(17, 20, 25);
        ForeColor = contrast ? SystemColors.ControlText : Color.FromArgb(215, 226, 232);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24, 18, 24, 18)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = recovery ? "Recovering Camo Connector" : $"Updating to v{expected}",
            Font = headingFont, AutoSize = true, Margin = Padding.Empty
        }, 0, 0);
        status = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty,
            Text = "Waiting for Camo Connector to close.",
            ForeColor = contrast ? SystemColors.ControlText : Color.FromArgb(158, 179, 193)
        };
        layout.Controls.Add(status, 0, 1);
        meter = new UpdateMeter(contrast) { Dock = DockStyle.Fill, Height = 10, Margin = Padding.Empty };
        layout.Controls.Add(meter, 0, 2);
        Controls.Add(layout);
    }

    public void Report(string message, int? percent)
    {
        status.Text = message;
        meter.SetProgress(percent);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (contrast) return;
        var dark = 1;
        var corners = 2;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        headingFont.Dispose();
        bodyFont.Dispose();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private sealed class UpdateMeter : Control
    {
        private readonly Color track;
        private readonly Color accent;
        private readonly System.Windows.Forms.Timer animation = new() { Interval = 30 };
        private int? percent;
        private int phase;

        public UpdateMeter(bool contrast)
        {
            DoubleBuffered = true;
            AccessibleName = "Update progress";
            AccessibleRole = AccessibleRole.ProgressBar;
            track = contrast ? SystemColors.ControlDark : Color.FromArgb(33, 45, 56);
            accent = contrast ? SystemColors.Highlight : Color.FromArgb(141, 215, 206);
            animation.Tick += (_, _) => { phase = (phase + 3) % 100; Invalidate(); };
            animation.Start();
        }

        public void SetProgress(int? value)
        {
            percent = value;
            AccessibleDescription = value is int current ? $"{current} percent" : "In progress";
            animation.Enabled = value == null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(ClientRectangle, new Size(Height / 2, Height / 2));
            using var background = new SolidBrush(track);
            using var fill = new SolidBrush(accent);
            e.Graphics.FillPath(background, shape);
            e.Graphics.SetClip(shape);
            if (percent is int value)
                e.Graphics.FillRectangle(fill, 0, 0, Width * value / 100, Height);
            else
            {
                var segment = Width / 4;
                var offset = (Width + segment) * phase / 100 - segment;
                e.Graphics.FillRectangle(fill, offset, 0, segment, Height);
            }
            e.Graphics.ResetClip();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) animation.Dispose();
            base.Dispose(disposing);
        }
    }
}
