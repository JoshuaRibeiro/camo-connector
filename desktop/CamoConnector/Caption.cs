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

        Button CaptionButton(string symbol, string name, Action action)
        {
            var button = new Button
            {
                Dock = DockStyle.Right, Width = LogicalToDeviceUnits(46), Text = symbol, AccessibleName = name
            };
            button.Click += (_, _) => action();
            return button;
        }
        var maximize = CaptionButton("□", "Maximize", ToggleMaximized);
        Resize += (_, _) =>
        {
            var maximized = WindowState == FormWindowState.Maximized;
            maximize.Text = maximized ? "❐" : "□";
            maximize.AccessibleName = maximized ? "Restore" : "Maximize";
        };
        caption.Controls.Add(title);
        caption.Controls.Add(icon);
        caption.Controls.Add(CaptionButton("−", "Minimize", () => WindowState = FormWindowState.Minimized));
        caption.Controls.Add(maximize);
        caption.Controls.Add(CaptionButton("×", "Close", Close));
        Controls.Add(caption);
    }

    private void ToggleMaximized() => WindowState = WindowState == FormWindowState.Maximized
        ? FormWindowState.Normal : FormWindowState.Maximized;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
}
