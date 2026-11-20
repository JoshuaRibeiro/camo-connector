using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly TabPage localTrackerPage = new("Tracker");
    private WebView2? localTrackerView;
    private bool localTrackerLoading;

    private void AddLocalTrackerPage()
    {
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
        var reload = new Button { Text = "Reload tracker", AutoSize = true };
        var message = new Label { AutoSize = true, Margin = new Padding(8) };
        controls.Controls.AddRange([reload, message]);
        localTrackerPage.Controls.Add(controls);
        pages.TabPages.Add(localTrackerPage);
        reload.Click += async (_, _) =>
        {
            if (localTrackerView?.CoreWebView2 is { } core) core.Reload();
            else await OpenLocalTrackerAsync(message);
        };
        pages.SelectedIndexChanged += async (_, _) =>
        {
            if (pages.SelectedTab == localTrackerPage && localTrackerView == null)
                await OpenLocalTrackerAsync(message);
        };
    }

    private async Task OpenLocalTrackerAsync(Label message)
    {
        if (localTrackerLoading || IsDisposed) return;
        localTrackerLoading = true;
        WebView2? view = null;
        try
        {
            message.Text = "Opening local tracker.";
            for (var attempt = 0; !bridgeReady && attempt < 40; attempt++)
                await Task.Delay(250, closing.Token);
            if (!bridgeReady) throw new IOException("The local bridge is unavailable.");

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
            core.NavigationCompleted += (_, args) => message.Text = args.IsSuccess ? "" : "Local tracker could not load.";
            var key = File.ReadAllText(Path.Combine(root, "Data", "bridge-pairing.key")).Trim();
            core.Navigate("http://127.0.0.1:47150/local/tracker#" + key);
            localTrackerView = view;
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed) message.Text = "Could not open the local tracker: " + error.Message;
        }
        finally
        {
            if (localTrackerView != view) view?.Dispose();
            localTrackerLoading = false;
        }
    }
}
