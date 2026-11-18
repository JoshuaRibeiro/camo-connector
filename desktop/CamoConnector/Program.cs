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
    private readonly Button update = new() { Text = "Check for updates", AutoSize = true };
    private readonly Label updateStatus = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly ComboBox extensionBrowser = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly Button downloadExtension = new() { Text = "Download extension ZIP", AutoSize = true, Enabled = false };
    private readonly Label extensionStatus = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private GitHubRelease.ExtensionPackage? extensionPackage;
    private readonly string? updateKey = UpdateEnvironment.TrustedKey();
    private static readonly Version desktopVersion = typeof(Program).Assembly.GetName().Version!;
    private ReleaseVerification.Manifest? available;
    private Process? bridge;
    private readonly CancellationTokenSource closing = new();

    public ConnectorWindow()
    {
        var build = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? desktopVersion.ToString(3);
        Text = $"Camo Connector v{build}";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(570, 575);
        MinimumSize = new Size(500, 300);
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20),
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true
        };
        var updateBar = new FlowLayoutPanel
        {
            Width = 510, Height = 34, FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        updateBar.Controls.Add(update);
        updateBar.Controls.Add(updateStatus);
        layout.Controls.Add(updateBar);
        var extensionBar = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        extensionBrowser.Items.AddRange(new object[] { "Chrome", "Edge", "Firefox", "Opera" });
        extensionBrowser.SelectedIndex = 0;
        extensionBrowser.AccessibleName = "Browser for extension download";
        extensionBrowser.SelectedIndexChanged += (_, _) =>
        {
            extensionPackage = null;
            available = null;
            update.Text = "Check for updates";
            downloadExtension.Enabled = false;
            extensionStatus.Text = "Check for updates to find the selected browser ZIP.";
        };
        downloadExtension.Click += async (_, _) => await DownloadExtensionAsync();
        extensionBar.Controls.Add(extensionBrowser);
        extensionBar.Controls.Add(downloadExtension);
        layout.Controls.Add(extensionBar);
        layout.Controls.Add(extensionStatus);
        layout.Controls.Add(status);
        AddCaptureControls(layout);
        update.Click += async (_, _) => await UpdateAsync();
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(510, 0),
            Text = "Open the browser extension and choose Connect automatically. Approve the request on this computer."
        });
        AddPages(layout);
        Shown += async (_, _) => await RefreshSourcesAsync();
        Shown += async (_, _) => await StartBridgeAsync();
    }

    private async Task UpdateAsync()
    {
        SetUpdateControlsBusy(true);
        try
        {
            if (available != null)
            {
                UpdateEnvironment.HandOff(root, available.Version);
                Close();
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var http = new HttpClient();
            var releases = new GitHubRelease(http);
            extensionPackage = null;
            updateStatus.Text = "Checking desktop version.";
            extensionStatus.Text = "Checking browser extension.";
            var packageCheck = releases.FindExtensionAsync((string)extensionBrowser.SelectedItem!, timeout.Token);
            try
            {
                available = updateKey == null ? null : await releases.CheckAsync(
                    new Version(desktopVersion.Major, desktopVersion.Minor, desktopVersion.Build), updateKey, timeout.Token);
                if (!IsDisposed) updateStatus.Text = updateKey == null ? "This build has no desktop update key." :
                    available == null ? "Desktop is up to date." : $"Desktop v{available.Version} is available.";
            }
            catch (Exception error)
            {
                available = null;
                if (!IsDisposed) updateStatus.Text = error.Message;
            }
            try
            {
                extensionPackage = await packageCheck;
                if (!IsDisposed) extensionStatus.Text = extensionPackage == null ? "No extension ZIP found." :
                    $"{extensionBrowser.SelectedItem} extension v{extensionPackage.Version} is available to download.";
            }
            catch
            {
                if (!IsDisposed) extensionStatus.Text = "Could not check for the browser extension.";
            }

            if (!IsDisposed && available != null) update.Text = "Update and restart";
        }
        catch (Exception error) { if (!IsDisposed) updateStatus.Text = error.Message; }
        finally { if (!IsDisposed) SetUpdateControlsBusy(false); }
    }

    private void SetUpdateControlsBusy(bool busy)
    {
        update.Enabled = extensionBrowser.Enabled = !busy;
        downloadExtension.Enabled = !busy && extensionPackage != null;
    }

    private async Task DownloadExtensionAsync()
    {
        if (extensionPackage is not { } package) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save browser extension ZIP",
            FileName = package.Name,
            Filter = "ZIP archive (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        SetUpdateControlsBusy(true);
        extensionStatus.Text = "Downloading browser extension.";
        var temp = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var http = new HttpClient();
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                await new GitHubRelease(http).DownloadExtensionAsync(package, stream, timeout.Token);
            File.Move(temp, dialog.FileName, overwrite: true);
            if (!IsDisposed) extensionStatus.Text = "Extension ZIP saved. Install it from your browser.";
        }
        catch (Exception error) { if (!IsDisposed) extensionStatus.Text = error.Message; }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!IsDisposed) SetUpdateControlsBusy(false);
        }
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
                        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:47150/events");
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
        diagnosticsTimer.Dispose();
        logFont.Dispose();
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
