using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly PageDeck pages = new() { Dock = DockStyle.Fill };
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

    private void AddPages(Control setup, string build)
    {
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
        pages.TabPages.AddRange([capturePage, helpPage, settingsPage]);
        AddLocalTrackerPage();
        BuildShell(build);
        BindAutomaticTracking();
        pages.SelectedIndexChanged += (_, _) => RefreshLogs();
        diagnosticsTimer.Tick += (_, _) =>
        {
            RefreshTheme();
            CheckBridge();
            CheckAutomaticTracking();
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
        var previous = box.Text;
        if (previous == text) return;
        const int getFirstVisibleLine = 0x00CE, lineScroll = 0x00B6;
        var firstLine = (int)SendMessage(box.Handle, getFirstVisibleLine, 0, 0);
        var offset = box.SelectionStart;
        var length = box.SelectionLength;
        var lastVisible = box.GetCharIndexFromPosition(new Point(0, Math.Max(0, box.ClientSize.Height - 2)));
        var follow = previous.Length == 0 || length == 0 &&
            box.GetLineFromCharIndex(lastVisible) >= box.GetLineFromCharIndex(box.TextLength);
        var anchorStart = box.GetFirstCharIndexFromLine(firstLine);
        var anchorEnd = anchorStart >= 0 ? previous.IndexOf('\n', anchorStart) : -1;
        var anchor = anchorEnd > anchorStart ? previous[anchorStart..anchorEnd].TrimEnd('\r') : "";
        box.Text = text;
        if (follow)
        {
            box.Select(box.TextLength, 0);
            box.ScrollToCaret();
            return;
        }
        var anchorIndex = anchor.Length > 0 ? text.IndexOf(anchor, StringComparison.Ordinal) : -1;
        if (anchorIndex >= 0) offset += anchorIndex - anchorStart;
        var selection = Math.Clamp(offset, 0, box.TextLength);
        box.Select(selection, Math.Min(length, box.TextLength - selection));
        var newFirstLine = anchorIndex >= 0 ? box.GetLineFromCharIndex(anchorIndex) : firstLine;
        SendMessage(box.Handle, lineScroll, 0, newFirstLine - SendMessage(box.Handle, getFirstVisibleLine, 0, 0));
    }

    private void RefreshTheme()
    {
        var useDark = !SystemInformation.HighContrast;
        if (dark == useDark) return;
        dark = useDark;
        ApplyTheme(this, useDark);
        StyleShell();
        var enabled = useDark ? 1 : 0;
        if (IsHandleCreated)
        {
            DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
            var rounded = 2;
            DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
        }
        Invalidate(true);
    }

    private static void ApplyTheme(Control control, bool dark, bool insideCard = false)
    {
        if (control is PictureBox) return;
        var surface = dark ? (insideCard ? ShellPanel : ShellBackground) : SystemColors.Control;
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
                button.BackColor = ShellButton;
                button.FlatAppearance.BorderSize = 0;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 79, 86);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(34, 103, 106);
            }
        }
        if (control is TabPage page) page.UseVisualStyleBackColor = !dark;
        foreach (Control child in control.Controls) ApplyTheme(child, dark, insideCard || control is RoundedCard);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(IntPtr window, int message, nint wParam, nint lParam);
}
