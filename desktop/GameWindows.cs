using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CamoConnector.Capture;

internal static class GameWindows
{
    public sealed record Window(nint Handle, int ProcessId, string Title, bool IsCallOfDuty)
    {
        public string Input => $"{Handle}:{ProcessId}";
    }

    public static Window[] List()
    {
        var windows = new List<Window>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId || pid == 0) return true;
            var title = new StringBuilder(1025);
            GetWindowText(handle, title, title.Capacity);
            try
            {
                using var process = Process.GetProcessById(pid);
                var name = title.ToString();
                var isGame = process.ProcessName.Equals("cod", StringComparison.OrdinalIgnoreCase);
                if (isGame || name.Length > 0)
                    windows.Add(new(handle, pid, name.Length == 0 ? "Call of Duty" : name, isGame));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return windows.ToArray();
    }

    public static bool Exists(Window window)
    {
        GetWindowThreadProcessId(window.Handle, out var pid);
        return pid == window.ProcessId;
    }

    public static Window Parse(string input)
    {
        var parts = input.Split(':');
        if (parts.Length != 2 || !nint.TryParse(parts[0], out var handle) || handle == 0 ||
            !int.TryParse(parts[1], out var pid) || pid <= 0)
            throw new ArgumentException("Select a game window again.");
        return new(handle, pid, "", false);
    }

    public static Rectangle? VisibleClient(Window window)
    {
        if (!Exists(window) || GetForegroundWindow() != window.Handle || IsIconic(window.Handle) ||
            !GetClientRect(window.Handle, out var rect)) return null;
        var origin = new NativePoint();
        if (!ClientToScreen(window.Handle, ref origin) || rect.Right < 32 || rect.Bottom < 32) return null;
        var bounds = new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom);
        return Screen.FromHandle(window.Handle).Bounds.Contains(bounds) ? bounds : null;
    }

    private delegate bool WindowCallback(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
