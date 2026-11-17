using System.Collections.Concurrent;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly TabControl pages = new() { Dock = DockStyle.Fill };
    private readonly TabPage logsPage = new("Logs");
    private readonly TextBox detectorLog = LogBox(), bridgeLog = LogBox(), captureLog = LogBox();
    private readonly ConcurrentQueue<string> bridgeMessages = new(), captureMessages = new(), detectorMessages = new();
    private readonly System.Windows.Forms.Timer diagnosticsTimer = new() { Interval = 1000 };
    private readonly Font logFont = new("Consolas", 9f);
    private bool? dark;
    private (long Size, DateTime Modified)? eventRevision;
    private string events = "";

    private static TextBox LogBox() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill
    };

    private void AddPages(FlowLayoutPanel setup)
    {
        var capturePage = new TabPage("Setup");
        capturePage.Controls.Add(setup);
        var logs = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        var boxes = new[] { (detectorLog, "Detector"), (bridgeLog, "Bridge"), (captureLog, "Capture") };
        foreach (var (box, label) in boxes)
        {
            logs.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / boxes.Length));
            box.Font = logFont;
            var group = new GroupBox { Text = label, Dock = DockStyle.Fill };
            group.Controls.Add(box);
            logs.Controls.Add(group, 0, logs.RowStyles.Count - 1);
        }
        logsPage.Controls.Add(logs);
        pages.TabPages.AddRange([capturePage, logsPage]);
        pages.DrawItem += DrawTab;
        Controls.Add(pages);
        pages.SelectedIndexChanged += (_, _) => RefreshLogs();
        diagnosticsTimer.Tick += (_, _) =>
        {
            RefreshTheme();
            CheckBridge();
            RefreshLogs();
        };
        HandleCreated += (_, _) => { dark = null; RefreshTheme(); };
        diagnosticsTimer.Start();
    }

    private void CheckBridge()
    {
        if (!bridgeReady || bridge is not { HasExited: true }) return;
        bridgeReady = false;
        status.Text = "Local bridge stopped. Reopen Camo Connector to reconnect.";
        LogLine(bridgeMessages, status.Text);
        try { StopDetector(); }
        catch (Exception error) { LogLine(detectorMessages, error.Message); }
        UpdateCaptureButtons();
    }

    private static void LogLine(ConcurrentQueue<string> target, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        target.Enqueue(line.Length <= 2048 ? line : line[..2048]);
        while (target.Count > 100) target.TryDequeue(out _);
    }

    private void RefreshLogs()
    {
        if (pages.SelectedTab != logsPage) return;
        try
        {
            var file = new FileInfo(Path.Combine(DataDirectory, "sunburn-events.jsonl"));
            if (!file.Exists) { events = ""; eventRevision = null; }
            else if (eventRevision != (file.Length, file.LastWriteTimeUtc))
            {
                using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                // Bound the tail read even when a detector has been running for days.
                var size = Math.Min(65536, input.Length);
                input.Position = input.Length - size;
                var startsMidFile = input.Position > 0;
                var buffer = new byte[(int)size];
                var read = input.ReadAtLeast(buffer, buffer.Length, false);
                var text = Encoding.UTF8.GetString(buffer, 0, read);
                if (startsMidFile) text = text.IndexOf('\n') is int lf and >= 0 ? text[(lf + 1)..] : "";
                events = string.Join(Environment.NewLine, text.Split('\n')
                    .Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).TakeLast(80));
                eventRevision = (file.Length, file.LastWriteTimeUtc);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        SetLog(detectorLog, string.Join(Environment.NewLine,
            detectorMessages.Concat(events.Length == 0 ? [] : new[] { events })));
        SetLog(bridgeLog, string.Join(Environment.NewLine, bridgeMessages));
        SetLog(captureLog, string.Join(Environment.NewLine, captureMessages));
    }

    private static void SetLog(TextBox box, string text)
    {
        if (box.Text == text) return;
        var follow = box.SelectionStart + box.SelectionLength >= box.TextLength;
        var offset = box.SelectionStart;
        var length = box.SelectionLength;
        box.Text = text;
        box.SelectionStart = follow ? box.TextLength : Math.Min(offset, box.TextLength);
        box.SelectionLength = follow ? 0 : Math.Min(length, box.TextLength - box.SelectionStart);
        box.ScrollToCaret();
    }

    private void RefreshTheme()
    {
        var useDark = !SystemInformation.HighContrast && Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;
        if (dark == useDark) return;
        dark = useDark;
        ApplyTheme(this, useDark);
        pages.DrawMode = useDark ? TabDrawMode.OwnerDrawFixed : TabDrawMode.Normal;
        var enabled = useDark ? 1 : 0;
        if (IsHandleCreated) DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        Invalidate(true);
    }

    private static void ApplyTheme(Control control, bool dark)
    {
        if (control is PictureBox) return;
        var surface = dark ? Color.FromArgb(30, 32, 36) : SystemColors.Control;
        var ink = dark ? Color.FromArgb(234, 236, 240) : SystemColors.ControlText;
        control.BackColor = surface;
        control.ForeColor = ink;
        if (control is TextBoxBase or ComboBox)
            control.BackColor = dark ? Color.FromArgb(22, 24, 27) : SystemColors.Window;
        if (control is Button button)
        {
            button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
            button.UseVisualStyleBackColor = !dark;
            if (dark)
            {
                button.BackColor = Color.FromArgb(48, 51, 57);
                button.FlatAppearance.BorderColor = Color.FromArgb(90, 95, 104);
            }
        }
        if (control is TabPage page) page.UseVisualStyleBackColor = !dark;
        foreach (Control child in control.Controls) ApplyTheme(child, dark);
    }

    private void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= pages.TabCount) return;
        using var brush = new SolidBrush(e.Index == pages.SelectedIndex ? Color.FromArgb(48, 51, 57) : BackColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, pages.TabPages[e.Index].Text, pages.Font, e.Bounds, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
