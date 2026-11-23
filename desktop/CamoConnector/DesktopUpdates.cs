using CamoConnector.Update;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 4 * 60 * 60 * 1000 };
    private CaptionControl? installUpdate;
    private ReleaseVerification.Manifest? available;
    private bool checkingUpdate;

    private async Task UpdateAsync()
    {
        if (checkingUpdate) return;
        if (available == null)
        {
            await CheckForUpdateAsync(manual: true);
            return;
        }
        if (installingRuntime)
        {
            updateStatus.Text = "Wait for tracking support to finish downloading.";
            return;
        }
        try
        {
            UpdateEnvironment.HandOff(root, available.Version);
            Close();
        }
        catch (Exception error) { updateStatus.Text = error.Message; }
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        var key = updateKey;
        if (checkingUpdate || closing.IsCancellationRequested) return;
        if (key == null)
        {
            if (manual) updateStatus.Text = "This build has no desktop update key.";
            return;
        }
        checkingUpdate = true;
        update.Enabled = false;
        if (installUpdate != null) installUpdate.Enabled = false;
        var cancel = closing.Token;
        if (manual) updateStatus.Text = "Checking for updates…";
        try
        {
            var installed = new Version(desktopVersion.Major, desktopVersion.Minor, desktopVersion.Build);
            var cached = await Task.Run(() =>
            {
                using var archive = PendingUpdate.Open(root, installed, key, out var manifest);
                return manifest;
            }, cancel);
            if (cancel.IsCancellationRequested) return;
            available = cached;
            using var http = new HttpClient();
            var releases = new GitHubRelease(http);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var baseline = cached == null ? installed : Version.Parse(cached.Version);
            var release = await releases.CheckSignedAsync(baseline, key, timeout.Token);
            if (release != null)
            {
                updateStatus.Text = $"Downloading v{release.Manifest.Version} in the background.";
                await Task.Run(() => PendingUpdate.DownloadAsync(root, releases, release, cancel), cancel);
                cached = release.Manifest;
            }
            if (cancel.IsCancellationRequested) return;
            available = cached;
            if (available != null)
            {
                updateStatus.Text = $"v{available.Version} is downloaded and ready to install.";
            }
            else if (manual) updateStatus.Text = "Desktop is up to date.";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancel.IsCancellationRequested)
            {
                if (available != null)
                    updateStatus.Text = $"Could not refresh updates. v{available.Version} is ready to install.";
                else if (manual || updateStatus.Text.StartsWith("Downloading", StringComparison.Ordinal))
                    updateStatus.Text = "Could not prepare the update. Check again to retry.";
                LogLine(bridgeMessages, "Background update: " + error.Message);
            }
        }
        finally
        {
            checkingUpdate = false;
            if (!cancel.IsCancellationRequested)
            {
                update.Enabled = true;
                update.Text = available == null ? "Check for updates" : "Install update and restart";
                if (installUpdate != null)
                {
                    installUpdate.Visible = available != null;
                    installUpdate.Enabled = true;
                }
            }
            if (releaseNoticePending)
            {
                releaseNoticePending = false;
                if (!cancel.IsCancellationRequested) await CheckForUpdateAsync(manual: false);
            }
        }
    }
}
