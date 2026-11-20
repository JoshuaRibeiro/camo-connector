using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly TabPage localTrackerPage = new("Tracker");
    private readonly BannerButton reloadTracker = new() { Text = "Refresh tracker", AutoSize = true };
    private readonly Label trackerMessage = new() { AutoSize = true, Dock = DockStyle.Top };
    private WebView2? localTrackerView;
    private bool localTrackerLoading;

    private void AddLocalTrackerPage()
    {
        localTrackerPage.Controls.Add(trackerMessage);
        pages.TabPages.Add(localTrackerPage);
        reloadTracker.Click += async (_, _) =>
        {
            if (localTrackerView?.CoreWebView2 is { } core) core.Reload();
            else await OpenLocalTrackerAsync();
        };
        pages.SelectedIndexChanged += async (_, _) =>
        {
            if (bridgeReady && pages.SelectedTab == localTrackerPage && localTrackerView == null)
                await OpenLocalTrackerAsync();
        };
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
            localTrackerPage.Controls.Add(view);
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
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += (_, args) => trackerMessage.Text = args.IsSuccess ? "" : "Local tracker could not load.";
            var key = File.ReadAllText(Path.Combine(root, "Data", "bridge-pairing.key")).Trim();
            core.Navigate("http://127.0.0.1:47150/local/tracker#" + key);
            localTrackerView = view;
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
