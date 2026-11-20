using System.Drawing.Drawing2D;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private sealed class TrackingSwitch : CheckBox
    {
        public TrackingSwitch()
        {
            Dock = DockStyle.Fill;
            DoubleBuffered = true;
            CheckedChanged += (_, _) => Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize) =>
            new(TextRenderer.MeasureText(Text, Font).Width + LogicalToDeviceUnits(62), LogicalToDeviceUnits(32));

        protected override void OnPaint(PaintEventArgs e)
        {
            var highContrast = SystemInformation.HighContrast;
            var trackWidth = LogicalToDeviceUnits(42);
            var trackHeight = LogicalToDeviceUnits(22);
            var track = new Rectangle(Width - trackWidth - 2, (Height - trackHeight) / 2, trackWidth, trackHeight);
            var accent = highContrast ? SystemColors.Highlight : ShellAccent;
            var ink = highContrast ? SystemColors.ControlText : ShellText;
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Math.Max(0, track.Left - 8), Height),
                ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = new GraphicsPath();
            outline.AddRoundedRectangle(track, new Size(track.Height / 2, track.Height / 2));
            using var fill = new SolidBrush(highContrast ? SystemColors.Control : Checked ? ShellButton : ShellBackground);
            using var border = new Pen(Checked ? accent : highContrast ? ink : ShellBorder);
            e.Graphics.FillPath(fill, outline);
            e.Graphics.DrawPath(border, outline);
            var inset = LogicalToDeviceUnits(3);
            var knobSize = track.Height - inset * 2;
            using var knob = new SolidBrush(Checked ? accent : highContrast ? ink : ShellMuted);
            e.Graphics.FillEllipse(knob, Checked ? track.Right - inset - knobSize : track.Left + inset,
                track.Top + inset, knobSize, knobSize);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1), ink, BackColor);
        }
    }
}
