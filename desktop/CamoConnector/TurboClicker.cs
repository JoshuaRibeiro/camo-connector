using System.Runtime.InteropServices;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly System.Windows.Forms.Timer turboTimer = new() { Interval = 100 };
    private MouseHook? turboCallback;
    private IntPtr turboHook;
    private bool turboArmed, turboHeld, sideHeld;
    private CheckBox? turboEnabled;

    private Control[] CreateTurboControls()
    {
        const string text = "Select the monitor showing Call of Duty in Auto tracking.";
        var link = new LinkLabel { AutoSize = true, Text = text, LinkBehavior = LinkBehavior.NeverUnderline };
        link.LinkArea = new LinkArea(text.IndexOf("Auto", StringComparison.Ordinal), 4);
        void MatchColour() => link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = link.ForeColor;
        MatchColour();
        link.ForeColorChanged += (_, _) => MatchColour();
        var panel = new FlowLayoutPanel { AutoSize = true, Visible = false, FlowDirection = FlowDirection.TopDown };
        var enable = new CheckBox { Text = "Enable turbo clicker", AutoSize = true };
        turboEnabled = enable;
        var speed = new NumericUpDown { Minimum = 1, Maximum = 30, Value = 10 };
        panel.Controls.AddRange([enable, new Label { AutoSize = true, Text = "Clicks per second" }, speed,
            new Label { AutoSize = true, Text = "Press Mouse 4 to arm or disarm. Hold left click to repeat." }]);
        link.LinkClicked += (_, _) => panel.Visible = !panel.Visible;
        speed.ValueChanged += (_, _) => turboTimer.Interval = (int)Math.Round(1000m / speed.Value);
        enable.CheckedChanged += (_, _) => {
            if (!enable.Checked) { StopTurbo(); return; }
            turboCallback = OnTurboMouse;
            turboHook = SetWindowsHookEx(14, turboCallback, GetModuleHandle(null), 0);
            if (turboHook == IntPtr.Zero) {
                enable.Checked = false;
                MessageBox.Show(this, "Windows could not start mouse input tracking.");
            }
        };
        turboTimer.Tick += (_, _) => {
            if (turboHeld && turboArmed) { mouse_event(4, 0, 0, 0, 0); mouse_event(2, 0, 0, 0, 0); }
        };
        return [link, panel];
    }

    private IntPtr OnTurboMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (Marshal.ReadInt32(data, 12) & 1) == 0) {
            switch (message.ToInt32()) {
                case 0x20B when (Marshal.ReadInt32(data, 8) >> 16) == 1:
                    if (!sideHeld) {
                        turboArmed = !turboArmed;
                        turboTimer.Stop();
                        if (turboEnabled != null) turboEnabled.Text = turboArmed ? "Enable turbo clicker (armed)" : "Enable turbo clicker";
                    }
                    sideHeld = true;
                    break;
                case 0x20C when (Marshal.ReadInt32(data, 8) >> 16) == 1: sideHeld = false; break;
                case 0x201: turboHeld = true; if (turboArmed) turboTimer.Start(); break;
                case 0x202: turboHeld = false; turboTimer.Stop(); break;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void StopTurbo()
    {
        turboTimer.Stop();
        if (turboHook != IntPtr.Zero) UnhookWindowsHookEx(turboHook);
        turboHook = IntPtr.Zero;
        turboArmed = turboHeld = sideHeld = false;
        if (turboEnabled != null) turboEnabled.Text = "Enable turbo clicker";
    }

    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32")] private static extern IntPtr SetWindowsHookEx(int type, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
}
