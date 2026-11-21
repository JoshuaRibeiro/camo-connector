using System.Runtime.InteropServices;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly System.Windows.Forms.Timer turboTimer = new() { Interval = 100 };
    private IntPtr heldMouse, releaseHook;
    private MouseHook? releaseCallback;
    private bool physicalLeftDown, discardRepeat;
    private const uint RepeatMarker = 0x434F4454;
    private bool listening;
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
            new Label { AutoSize = true, Text = "Press Mouse 5 (XButton2) to arm or disarm. Hold left click to repeat." }]);
        link.LinkClicked += (_, _) => panel.Visible = !panel.Visible;
        speed.ValueChanged += (_, _) => turboTimer.Interval = (int)Math.Round(1000m / speed.Value);
        enable.CheckedChanged += (_, _) => {
            if (!enable.Checked) { StopTurbo(); return; }
            listening = RegisterMouse(0x2100, Handle); // INPUTSINK and DEVNOTIFY
            if (listening) {
                releaseCallback = FilterRepeat;
                releaseHook = SetWindowsHookExW(14, releaseCallback, GetModuleHandleW(null), 0);
            }
            if (!listening || releaseHook == IntPtr.Zero) {
                enable.Checked = false;
                MessageBox.Show(this, "Windows could not start mouse input tracking.");
            }
        };
        turboTimer.Tick += (_, _) => {
            if (turboHeld && turboArmed && physicalLeftDown) {
                mouse_event(2, 0, 0, 0, RepeatMarker);
                mouse_event(4, 0, 0, 0, RepeatMarker);
            }
        };
        return [link, panel];
    }

    private void ReadTurboInput(Message message)
    {
        if (!listening) return;
        if (message.Msg == 0x00FE && message.WParam == 2) {
            ResetHold();
            sideHeld = false;
            return;
        }
        if (message.Msg != 0x00FF) return;
        uint size = (uint)Marshal.SizeOf<RawMousePacket>();
        if (GetRawInputData(message.LParam, 0x10000003, out var packet, ref size,
            (uint)(8 + 2 * IntPtr.Size)) == uint.MaxValue || packet.Type != 0 || packet.Device == IntPtr.Zero) return;
        var flags = packet.ButtonFlags;
        if ((flags & 0x100) != 0 && !sideHeld) {
            sideHeld = true;
            turboArmed = !turboArmed;
            ResetHold();
            if (turboEnabled != null) turboEnabled.Text = turboArmed ? "Enable turbo clicker (armed)" : "Enable turbo clicker";
        }
        if ((flags & 0x200) != 0) sideHeld = false;
        if ((flags & 1) != 0 && turboArmed) {
            heldMouse = packet.Device;
            turboHeld = true;
            turboTimer.Start();
        }
        if ((flags & 2) != 0 && packet.Device == heldMouse) ResetHold();
    }

    // Raw input identifies the holding device. At delivery time the hook also
    // sees whether a physical release overtook a repeat already in the queue.
    private IntPtr FilterRepeat(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == 0x201 || message == 0x202)) {
            bool down = message == 0x201;
            if ((Marshal.ReadInt32(data, 12) & 1) == 0) {
                physicalLeftDown = down;
            } else if (Marshal.ReadIntPtr(data, IntPtr.Size == 8 ? 24 : 20) == (IntPtr)RepeatMarker) {
                if (down) discardRepeat = !physicalLeftDown || !turboHeld || !turboArmed;
                if (discardRepeat) return (IntPtr)1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32")] private static extern IntPtr SetWindowsHookExW(int type, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private void ResetHold()
    {
        turboTimer.Stop();
        turboHeld = false;
        heldMouse = IntPtr.Zero;
    }
    private void StopTurbo()
    {
        turboTimer.Stop();
        if (listening) RegisterMouse(1, IntPtr.Zero);
        if (releaseHook != IntPtr.Zero) UnhookWindowsHookEx(releaseHook);
        releaseHook = IntPtr.Zero;
        physicalLeftDown = discardRepeat = false;
        listening = false;
        turboArmed = turboHeld = sideHeld = false;
        if (turboEnabled != null) turboEnabled.Text = "Enable turbo clicker";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMousePacket {
        public uint Type, Size;
        public IntPtr Device, Parameter;
        public ushort MouseFlags, Padding, ButtonFlags, ButtonData;
        public uint RawButtons;
        public int X, Y;
        public uint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice {
        public ushort Page, Usage;
        public uint Flags;
        public IntPtr Target;
    }
    private static bool RegisterMouse(uint flags, IntPtr target) {
        var device = new RawDevice { Page = 1, Usage = 2, Flags = flags, Target = target };
        return RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>());
    }
    [DllImport("user32")] private static extern bool RegisterRawInputDevices(ref RawDevice device, uint count, uint size);
    [DllImport("user32")] private static extern uint GetRawInputData(IntPtr handle, uint command, out RawMousePacket packet, ref uint size, uint headerSize);
    [DllImport("user32")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
}
