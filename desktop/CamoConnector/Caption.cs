using System.Runtime.InteropServices;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    protected override CreateParams CreateParams
    {
        get
        {
            const int caption = 0x00C00000;
            var parameters = base.CreateParams;
            parameters.Style &= ~caption;
            return parameters;
        }
    }

    private enum CaptionAction { Minimize, Maximize, Restore, Close }

    private sealed class CaptionControl : Button
    {
        internal CaptionAction Kind;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var size = LogicalToDeviceUnits(10);
            var x = (ClientSize.Width - size) / 2;
            var y = (ClientSize.Height - size) / 2;
            using var pen = new Pen(ForeColor, LogicalToDeviceUnits(1));
            switch (Kind)
            {
                case CaptionAction.Minimize:
                    e.Graphics.DrawLine(pen, x, y + size, x + size, y + size);
                    break;
                case CaptionAction.Maximize:
                    e.Graphics.DrawRectangle(pen, x, y, size, size);
                    break;
                case CaptionAction.Restore:
                    var offset = LogicalToDeviceUnits(3);
                    e.Graphics.DrawRectangle(pen, x, y + offset, size - offset, size - offset);
                    e.Graphics.DrawLines(pen,
                    [
                        new Point(x + offset, y + offset), new Point(x + offset, y),
                        new Point(x + size, y), new Point(x + size, y + size - offset),
                        new Point(x + size - offset, y + size - offset)
                    ]);
                    break;
                case CaptionAction.Close:
                    e.Graphics.DrawLine(pen, x, y, x + size, y + size);
                    e.Graphics.DrawLine(pen, x + size, y, x, y + size);
                    break;
            }
        }
    }

    private void AddCaption()
    {
        var caption = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(36) };
        using var iconBitmap = Icon!.ToBitmap();
        var size = LogicalToDeviceUnits(16);
        var image = new Bitmap(iconBitmap, size, size);
        var icon = new PictureBox
        {
            Dock = DockStyle.Left, Width = LogicalToDeviceUnits(32), Image = image,
            SizeMode = PictureBoxSizeMode.CenterImage
        };
        icon.Disposed += (_, _) => image.Dispose();
        var title = new Label
        {
            Text = Text, Font = ShellFont(Font.FontFamily.Name, Font.Size, FontStyle.Bold),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        void Drag(object? sender, MouseEventArgs args)
        {
            if (args.Button != MouseButtons.Left) return;
            if (args.Clicks > 1) ToggleMaximized();
            else
            {
                ReleaseCapture();
                var cursor = Cursor.Position;
                SendMessage(Handle, 0x00A1, 2, (cursor.X & 0xFFFF) | ((cursor.Y & 0xFFFF) << 16)); // WM_NCLBUTTONDOWN, HTCAPTION
            }
        }
        caption.MouseDown += Drag;
        title.MouseDown += Drag;
        icon.MouseDown += Drag;

        CaptionControl CaptionButton(CaptionAction kind, string name, Action action)
        {
            var button = new CaptionControl
            {
                Dock = DockStyle.Right, Width = LogicalToDeviceUnits(46), AccessibleName = name, Kind = kind
            };
            button.Click += (_, _) => action();
            return button;
        }
        var maximize = CaptionButton(CaptionAction.Maximize, "Maximize", ToggleMaximized);
        Resize += (_, _) =>
        {
            var maximized = WindowState == FormWindowState.Maximized;
            maximize.Kind = maximized ? CaptionAction.Restore : CaptionAction.Maximize;
            maximize.AccessibleName = maximized ? "Restore" : "Maximize";
            maximize.Invalidate();
        };
        caption.Controls.Add(title);
        caption.Controls.Add(icon);
        caption.Controls.Add(CaptionButton(CaptionAction.Minimize, "Minimize", () => WindowState = FormWindowState.Minimized));
        caption.Controls.Add(maximize);
        caption.Controls.Add(CaptionButton(CaptionAction.Close, "Close", Close));
        Controls.Add(caption);
    }

    private void ToggleMaximized() => WindowState = WindowState == FormWindowState.Maximized
        ? FormWindowState.Normal : FormWindowState.Maximized;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
}
