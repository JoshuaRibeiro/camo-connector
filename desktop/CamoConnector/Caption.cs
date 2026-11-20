using System.Runtime.InteropServices;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private const int NativeCaptionStyle = 0x00C00000;

    protected override CreateParams CreateParams
    {
        get
        {
            const int thickFrame = 0x00040000, maximizeBox = 0x00010000, systemMenu = 0x00080000;
            var parameters = base.CreateParams;
            parameters.Style = (parameters.Style & ~NativeCaptionStyle) | thickFrame | maximizeBox | systemMenu;
            return parameters;
        }
    }

    private Point resizeStart;
    private Rectangle resizeBounds;
    private Point resizeDirection;

    private void ConfigureWindowFrame()
    {
        FormBorderStyle = FormBorderStyle.None;
        UpdateResizePadding();
        Resize += (_, _) => UpdateResizePadding();
        HandleCreated += (_, _) =>
        {
            const int borderColor = 34;
            var noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(Handle, borderColor, ref noBorder, sizeof(int));
        };
    }

    private void UpdateResizePadding() => Padding =
        WindowState == FormWindowState.Normal ? new Padding(LogicalToDeviceUnits(4)) : Padding.Empty;

    private Point ResizeDirection(Point cursor) => new(
        cursor.X < Padding.Left ? -1 : cursor.X >= ClientSize.Width - Padding.Right ? 1 : 0,
        cursor.Y < Padding.Top ? -1 : cursor.Y >= ClientSize.Height - Padding.Bottom ? 1 : 0);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || WindowState != FormWindowState.Normal) return;
        resizeDirection = ResizeDirection(e.Location);
        if (resizeDirection.IsEmpty) return;
        resizeStart = Cursor.Position;
        resizeBounds = Bounds;
        Capture = true;
    }

    protected override void WndProc(ref Message message)
    {
        const int calculateClient = 0x0083, setCursor = 0x0020, styleChanging = 0x007C;
        const int windowStyle = -16;
        if (message.Msg == styleChanging && message.WParam == windowStyle)
        {
            base.WndProc(ref message);
            // STYLESTRUCT stores the proposed style after the current style.
            var proposedStyle = Marshal.ReadInt32(message.LParam, sizeof(int));
            Marshal.WriteInt32(message.LParam, sizeof(int), proposedStyle & ~NativeCaptionStyle);
            return;
        }
        if (message.Msg == calculateClient)
        {
            // With either form of WM_NCCALCSIZE, the first RECT is the proposed window area.
            // Keep the custom caption and clip the maximized frame's overhang to the work area.
            if (IsZoomed(message.HWnd))
            {
                var proposed = Rectangle.FromLTRB(
                    Marshal.ReadInt32(message.LParam, 0), Marshal.ReadInt32(message.LParam, 4),
                    Marshal.ReadInt32(message.LParam, 8), Marshal.ReadInt32(message.LParam, 12));
                var client = Rectangle.Intersect(proposed, Screen.FromRectangle(proposed).WorkingArea);
                Marshal.WriteInt32(message.LParam, 0, client.Left);
                Marshal.WriteInt32(message.LParam, 4, client.Top);
                Marshal.WriteInt32(message.LParam, 8, client.Right);
                Marshal.WriteInt32(message.LParam, 12, client.Bottom);
            }
            message.Result = 0;
            return;
        }
        if (message.Msg != setCursor || message.WParam != message.HWnd || WindowState != FormWindowState.Normal)
        {
            base.WndProc(ref message);
            return;
        }

        var direction = ResizeDirection(PointToClient(Cursor.Position));
        Cursor.Current = (direction.X, direction.Y) switch
        {
            (-1, -1) or (1, 1) => Cursors.SizeNWSE,
            (-1, 1) or (1, -1) => Cursors.SizeNESW,
            (_, 0) when direction.X != 0 => Cursors.SizeWE,
            (0, _) when direction.Y != 0 => Cursors.SizeNS,
            _ => Cursors.Default
        };
        message.Result = 1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (resizeDirection.IsEmpty || !Capture) return;
        var direction = resizeDirection;

        var cursor = Cursor.Position;
        var dx = cursor.X - resizeStart.X;
        var dy = cursor.Y - resizeStart.Y;
        int left = resizeBounds.Left, top = resizeBounds.Top;
        int right = resizeBounds.Right, bottom = resizeBounds.Bottom;
        if (direction.X < 0) left = Math.Min(left + dx, right - MinimumSize.Width);
        if (direction.X > 0) right = Math.Max(right + dx, left + MinimumSize.Width);
        if (direction.Y < 0) top = Math.Min(top + dy, bottom - MinimumSize.Height);
        if (direction.Y > 0) bottom = Math.Max(bottom + dy, top + MinimumSize.Height);
        Bounds = Rectangle.FromLTRB(left, top, right, bottom);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && !resizeDirection.IsEmpty) Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) resizeDirection = Point.Empty;
    }

    private enum CaptionAction { Minimize, Maximize, Restore, Close, Download }

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
                case CaptionAction.Download:
                    var middle = x + size / 2;
                    var tip = y + size * 3 / 4;
                    e.Graphics.DrawLine(pen, middle, y, middle, tip);
                    e.Graphics.DrawLines(pen, [new Point(x + 1, y + size / 3), new Point(middle, tip),
                        new Point(x + size - 1, y + size / 3)]);
                    e.Graphics.DrawLine(pen, x, y + size + 2, x + size, y + size + 2);
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
        installUpdate = CaptionButton(CaptionAction.Download, "Install downloaded update", () => _ = UpdateAsync());
        installUpdate.Visible = false;
        caption.Controls.Add(installUpdate);
        var updateHint = new ToolTip();
        updateHint.SetToolTip(installUpdate, "Install downloaded update and restart");
        caption.Disposed += (_, _) => updateHint.Dispose();
        caption.Controls.Add(CaptionButton(CaptionAction.Minimize, "Minimize", () => WindowState = FormWindowState.Minimized));
        caption.Controls.Add(maximize);
        caption.Controls.Add(CaptionButton(CaptionAction.Close, "Close", Close));
        Controls.Add(caption);
    }

    private void ToggleMaximized()
    {
        if (WindowState == FormWindowState.Maximized)
            WindowState = FormWindowState.Normal;
        else
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);
}
