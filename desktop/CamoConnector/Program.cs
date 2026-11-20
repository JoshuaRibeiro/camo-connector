using System.Diagnostics;
using System.Net;
using System.Reflection;
using CamoConnector.Update;

namespace CamoConnector;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var root = AppContext.BaseDirectory;
        try
        {
            using var lease = UpdateEnvironment.Lock(root);
            if (Directory.Exists(Path.Combine(root, ".camo-update")))
            {
                UpdateEnvironment.HandOff(root, "recover");
                return;
            }
            if (args.Length == 3 && args[0] == "--cleanup-updater" && int.TryParse(args[2], out var pid))
                CleanupUpdater(args[1], pid);
            Application.Run(new ConnectorWindow());
        }
        catch (Exception error) { MessageBox.Show(error.Message, "Camo Connector"); }
    }

    private static void CleanupUpdater(string path, int pid)
    {
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        const string prefix = "CamoConnector.Updater-";
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            StringComparison.OrdinalIgnoreCase) || Path.GetExtension(fullPath) != ".exe" ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return;
        try
        {
            try { using var updater = Process.GetProcessById(pid); if (!updater.WaitForExit(2000)) return; }
            catch (ArgumentException) { }
            File.Delete(fullPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed partial class ConnectorWindow : Form
{
    private readonly string root = AppContext.BaseDirectory;
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(1)
    };
    private readonly Label status = new() { Text = "Starting the local bridge…", AutoSize = true };
    private readonly Button update = new() { Text = "Check for updates" };
    private readonly Label updateStatus = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly string? updateKey = UpdateEnvironment.TrustedKey();
    private static readonly Version desktopVersion = typeof(Program).Assembly.GetName().Version!;
    private Process? bridge;
    private readonly CancellationTokenSource closing = new();

    public ConnectorWindow()
    {
        var build = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? desktopVersion.ToString(3);
        Text = $"Camo Connector v{build}";
        Font = ShellFont("Bahnschrift", 10);
        using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("CamoConnector.AppIcon")!;
        using var icon = new Icon(iconStream);
        Icon = (Icon)icon.Clone();
        StartPosition = FormStartPosition.CenterScreen;
        ConfigureWindowFrame();
        ClientSize = new Size(1040, 720);
        MinimumSize = new Size(570, 420);
        update.Click += async (_, _) => await UpdateAsync();
        AddPages(CreateSetupView(), build);
        Shown += async (_, _) => await RefreshSourcesAsync();
        Shown += async (_, _) =>
        {
            updateTimer.Start();
            _ = ListenForReleaseNoticesAsync(closing.Token);
            await CheckForUpdateAsync(manual: false);
        };
        updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(manual: false);
        Shown += async (_, _) =>
        {
            await StartBridgeAsync();
            if (!IsDisposed && !closing.IsCancellationRequested && pages.SelectedTab == localTrackerPage)
                await OpenLocalTrackerAsync();
        };
    }

    private async Task StartBridgeAsync()
    {
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "Data")).FullName;
            var executable = Path.Combine(root, "Runtime", "Bridge", "CamoConnector.Bridge.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(data);
            bridge = Process.Start(start) ?? throw new InvalidOperationException("The local bridge failed to start.");
            bridge.OutputDataReceived += (_, item) => LogLine(bridgeMessages, item.Data);
            bridge.ErrorDataReceived += (_, item) => LogLine(bridgeMessages, item.Data);
            bridge.BeginOutputReadLine();
            bridge.BeginErrorReadLine();
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!bridge.HasExited)
            {
                try
                {
                    var keyFile = Path.Combine(data, "bridge-pairing.key");
                    if (File.Exists(keyFile))
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:47150/focus");
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer", File.ReadAllText(keyFile).Trim());
                        using var response = await client.SendAsync(request, readyTimeout.Token);
                        if (response.StatusCode == HttpStatusCode.OK && !bridge.HasExited)
                        {
                            status.Text = "Local bridge ready";
                            bridgeReady = true;
                            UpdateCaptureButtons();
                            return;
                        }
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!readyTimeout.IsCancellationRequested) { }
                await Task.Delay(200, readyTimeout.Token);
            }
            status.Text = "The local bridge stopped. Close any other Camo Connector instance and retry.";
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error) { if (!IsDisposed) status.Text = "Could not start the local bridge: " + error.Message; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closing.Cancel();
        updateTimer.Dispose();
        setupHeadingFont?.Dispose();
        DisposeTrackerTray();
        diagnosticsTimer.Dispose();
        ReleaseUsage();
        logFont.Dispose();
        foreach (var font in shellFonts) font.Dispose();
        previewTimer.Dispose();
        try { StopPreview(); }
        catch (Exception error) { MessageBox.Show(error.Message, "Could not stop preview"); }
        try { StopDetector(); }
        catch (Exception error) { MessageBox.Show(error.Message, "Could not stop detection"); }
        if (bridge != null)
        {
            try { if (!bridge.HasExited) bridge.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { bridge.Dispose(); }
        }
        client.Dispose();
        closing.Dispose();
        base.OnFormClosed(e);
    }
}
