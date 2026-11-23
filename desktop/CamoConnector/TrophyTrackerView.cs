using System.Diagnostics;
using CamoConnector.Update;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private static readonly Uri TrophyUrl = new("http://127.0.0.1:47851/");
    private readonly TabPage trophyPage = new("Trophy Tracker");
    private readonly TrackingSwitch trophyEnabled = new() { Text = "Enable Trophy Tracker" };
    private readonly Label trophyStatus = new() { AutoSize = true, Text = "Trophy Tracker is off." };
    private readonly Panel trophyHost = new() { Dock = DockStyle.Fill };
    private string TrophyPreference => Path.Combine(DataDirectory, "trophy-tracking.txt");
    private CancellationTokenSource? trophySession;
    private Process? trophyServer;
    private WebView2? trophyView;
    private byte[]? pendingTrophyFrame;
    private bool trophyFrameRequested;

    private void BindTrophyTracker()
    {
        var controls = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14), ColumnCount = 1 };
        controls.Controls.Add(trophyEnabled);
        controls.Controls.Add(new Label { AutoSize = true, Text = "Uses the capture selected in Auto tracking." });
        controls.Controls.Add(trophyStatus);
        trophyPage.Controls.Add(trophyHost);
        trophyPage.Controls.Add(controls);
        try { trophyEnabled.Checked = File.Exists(TrophyPreference) && File.ReadAllText(TrophyPreference).Trim() == "on"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { trophyStatus.Text = error.Message; }

        trophyEnabled.CheckedChanged += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var temporary = TrophyPreference + ".tmp";
                File.WriteAllText(temporary, trophyEnabled.Checked ? "on" : "off");
                File.Move(temporary, TrophyPreference, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                trophyStatus.Text = "Could not save the trophy preference: " + error.Message;
                return;
            }
            if (trophyEnabled.Checked) await StartTrophyTrackerAsync();
            else
            {
                StopTrophyTracker();
                if (detector == null && pages.SelectedTab != capturePage) StopPreview();
                trophyStatus.Text = "Trophy Tracker is off.";
            }
        };
        Shown += async (_, _) => { if (trophyEnabled.Checked) await StartTrophyTrackerAsync(); };
    }

    private async Task StartTrophyTrackerAsync()
    {
        StopTrophyTracker();
        trophyStatus.Text = "Starting Trophy Tracker…";
        var session = trophySession = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        var token = session.Token;
        try
        {
            var assets = Directory.CreateDirectory(Path.Combine(DataDirectory, "TrophyTracker")).FullName;
            var assembly = typeof(ConnectorWindow).Assembly;
            const string prefix = "TrophyTracker.";
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                using var input = assembly.GetManifestResourceStream(resource)!;
                using var output = File.Create(Path.Combine(assets, resource[prefix.Length..]));
                input.CopyTo(output);
            }

            var node = await EnsureTrophyNodeAsync(token);
            token.ThrowIfCancellationRequested();
            trophyStatus.Text = "Starting Trophy Tracker.";
            var instance = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(node) { WorkingDirectory = assets, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(assets, "server.js"));
            start.Environment["TROPHY_INSTANCE"] = instance;
            start.Environment["TROPHY_TESSERACT"] = Path.Combine(root, "Runtime", "Ocr", "tesseract.exe");
            start.Environment["TROPHY_OCR_WORKER"] = Path.Combine(root, "Runtime", "Detector", "CoDStreamingTools.Detector.exe");
            trophyServer = Process.Start(start) ?? throw new InvalidOperationException("The trophy service could not start.");
            var readyBy = DateTime.UtcNow.AddSeconds(8);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (trophyServer.HasExited || DateTime.UtcNow >= readyBy)
                    throw new InvalidOperationException("The trophy service could not start. Check that port 47851 is available.");
                try
                {
                    if (await client.GetStringAsync(new Uri(TrophyUrl, "api/instance"), token) == "\"" + instance + "\"") break;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(150, token);
            }

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
            };
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(DataDirectory, "TrophyWebView"), options: options);
            token.ThrowIfCancellationRequested();
            var view = trophyView = new WebView2 { Dock = DockStyle.Fill };
            trophyHost.Controls.Add(view);
            await view.EnsureCoreWebView2Async(environment);
            token.ThrowIfCancellationRequested();
            view.CoreWebView2.NavigationStarting += (_, args) =>
                args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    uri.GetLeftPart(UriPartial.Authority) != TrophyUrl.GetLeftPart(UriPartial.Authority);
            view.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            view.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                if (args.Source == TrophyUrl.AbsoluteUri && args.TryGetWebMessageAsString() == "ready")
                    trophyFrameRequested = true;
            };
            view.Source = TrophyUrl;
            if (detector == null && previewWorker == null) StartPreview();
            trophyStatus.Text = "";
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            StopTrophyTracker();
            trophyStatus.Text = error.Message + " Turn Trophy Tracker off and back on to retry.";
        }
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    private async Task<string> EnsureTrophyNodeAsync(CancellationToken cancel)
    {
        var portable = Path.Combine(root, "Runtime", "Node", "node.exe");
        if (File.Exists(portable)) return portable;
        var installed = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, "node.exe"))
            .FirstOrDefault(File.Exists);
        if (installed != null) return installed;
        if (updateKey == null)
            throw new IOException("This build has no release verification key for the Trophy Tracker runtime.");

        trophyStatus.Text = "Downloading Trophy Tracker support…";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var release = new GitHubRelease(http);
        var version = new Version(desktopVersion.Major, desktopVersion.Minor, desktopVersion.Build);
        try
        {
            var manifest = await release.RuntimeAsync(version, "node", updateKey, deadline.Token);
            var path = Path.Combine(Path.GetTempPath(), $"CoDStreamingTools-node-{Guid.NewGuid():N}.zip");
            await using var archive = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                65536, FileOptions.DeleteOnClose);
            await release.DownloadArchiveAsync(manifest, archive, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            PortableReplacement.InstallRuntime(archive, manifest, root, "Node");
            return portable;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new IOException("The Trophy Tracker runtime download timed out.");
        }
    }

    private void StopTrophyTracker()
    {
        trophySession?.Cancel();
        trophySession?.Dispose();
        trophySession = null;
        trophyView?.Dispose();
        trophyView = null;
        trophyFrameRequested = false;
        Interlocked.Exchange(ref pendingTrophyFrame, null);
        if (trophyServer is not { } process) return;
        trophyServer = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception error) { LogLine(captureMessages, error.Message); }
        finally { process.Dispose(); }
    }

    private void SendTrophyFrame()
    {
        var frame = Interlocked.Exchange(ref pendingTrophyFrame, null);
        if (frame == null || !trophyFrameRequested || trophyView?.CoreWebView2 is not { } core || trophyView.Source != TrophyUrl) return;
        trophyFrameRequested = false;
        core.PostWebMessageAsString(Convert.ToBase64String(frame));
    }
}
