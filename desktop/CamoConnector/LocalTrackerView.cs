using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly TabPage localTrackerPage = new("Tracker");
    private readonly TabPage overlayPage = new("Overlays");
    private readonly BannerButton reloadTracker = new() { Text = "Refresh tracker", AutoSize = true };
    private readonly Label trackerMessage = new() { AutoSize = true, Dock = DockStyle.Top };
    private WebView2? localTrackerView;
    private bool localTrackerLoading;
    private bool localTrackerDocumentReady;

    private TabPage? TrackerHost => pages.SelectedTab is { } page &&
        (page == localTrackerPage || page == overlayPage) ? page : null;

    private void AddLocalTrackerPage()
    {
        localTrackerPage.Controls.Add(trackerMessage);
        pages.TabPages.Add(localTrackerPage);
        pages.TabPages.Add(overlayPage);
        reloadTracker.Click += async (_, _) =>
        {
            if (localTrackerView?.CoreWebView2 is { } core) core.Reload();
            else await OpenLocalTrackerAsync();
        };
        pages.SelectedIndexChanged += async (_, _) =>
        {
            if (TrackerHost is not { } host) return;
            host.Controls.Add(trackerMessage);
            if (localTrackerView == null) await OpenLocalTrackerAsync();
            else ShowTrackerSection();
        };
    }

    private void ShowTrackerSection()
    {
        if (TrackerHost is not { } host || localTrackerView is not { } view) return;
        if (view.Parent != host)
        {
            host.Controls.Add(view);
            view.BringToFront();
        }
        if (localTrackerDocumentReady)
            view.CoreWebView2.PostWebMessageAsString(host == overlayPage ? "overlays" : "tracker");
    }

    private async Task OpenLocalTrackerAsync()
    {
        if (localTrackerLoading || localTrackerView != null || IsDisposed || closing.IsCancellationRequested) return;
        if (!bridgeReady)
        {
            trackerMessage.Text = status.Text;
            return;
        }
        localTrackerLoading = true;
        WebView2? view = null;
        try
        {
            trackerMessage.Text = "Opening local tracker.";

            var profile = Path.Combine(root, "Data", "TrackerProfile");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (IsDisposed || closing.IsCancellationRequested) return;
            view = new WebView2 { Dock = DockStyle.Fill };
            (TrackerHost ?? localTrackerPage).Controls.Add(view);
            view.BringToFront();
            await view.EnsureCoreWebView2Async(environment);
            if (IsDisposed || closing.IsCancellationRequested) return;

            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += (_, args) =>
            {
                args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "http" || uri.Host != "127.0.0.1" ||
                    uri.Port != 47150 || uri.AbsolutePath != "/local/tracker";
                if (!args.Cancel) localTrackerDocumentReady = false;
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                const string channel = "https://www.twitch.tv/11annah";
                if (!args.IsUserInitiated || !string.Equals(args.Uri, channel, StringComparison.Ordinal)) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(channel)
                    {
                        UseShellExecute = true
                    });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    trackerMessage.Text = "Could not open 11annah’s Twitch channel.";
                }
            };
            core.NavigationCompleted += (_, args) =>
            {
                localTrackerDocumentReady = args.IsSuccess;
                trackerMessage.Text = args.IsSuccess ? "" : "Local tracker could not load.";
                if (args.IsSuccess) ShowTrackerSection();
            };
            var key = File.ReadAllText(Path.Combine(root, "Data", "bridge-pairing.key")).Trim();
            var query = pages.SelectedTab == overlayPage ? "?page=overlays" : "";
            core.Navigate("http://127.0.0.1:47150/local/tracker" + query + "#" + key);
            localTrackerView = view;
            ShowTrackerSection();
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed) trackerMessage.Text = "Could not open the local tracker: " + error.Message;
        }
        finally
        {
            if (localTrackerView != view) view?.Dispose();
            localTrackerLoading = false;
        }
    }
}
