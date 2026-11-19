using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private static readonly Uri TrackerUri = new("https://mw4.darkmattersolutions.uk/");
    private readonly TabPage trackerPage = new("Tracker");
    private readonly System.Windows.Forms.Timer trackerTimer = new() { Interval = 350 };
    private WebView2? trackerView;
    private NotifyIcon? trackerTray;
    private bool trackerLoading, trackerBusy, trackerDocumentReady, trackerResizing;
    private int trackerNavigation;
    private string? lastTrackerSnapshot;

    private void AddTrackerPage()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
        var open = new Button { Text = "Open tracker", AutoSize = true };
        var hide = new Button { Text = "Hide to tray", AutoSize = true };
        var message = new Label { AutoSize = true, Margin = new Padding(8), Text = "Sign in to sync in this tab." };
        bar.Controls.AddRange([open, hide, message]);
        trackerPage.Controls.Add(bar);
        pages.TabPages.Add(trackerPage);
        open.Click += async (_, _) =>
        {
            if (trackerView?.CoreWebView2 is { } core) core.Navigate(TrackerUri.AbsoluteUri);
            else await OpenTrackerAsync(message);
        };
        hide.Click += (_, _) => HideTrackerToTray();
        pages.SelectedIndexChanged += async (_, _) =>
        {
            if (pages.SelectedTab == trackerPage && trackerView == null) await OpenTrackerAsync(message);
        };
        Resize += (_, _) =>
        {
            if (trackerView != null && Visible && WindowState == FormWindowState.Minimized) HideTrackerToTray();
        };
        ResizeBegin += (_, _) => trackerResizing = true;
        ResizeEnd += async (_, _) =>
        {
            trackerResizing = false;
            await PublishTrackerEventsAsync();
        };
        trackerTimer.Tick += async (_, _) => await PublishTrackerEventsAsync();
    }

    private async Task OpenTrackerAsync(Label message)
    {
        if (trackerLoading || IsDisposed) return;
        trackerLoading = true;
        WebView2? view = null;
        try
        {
            message.Text = "Opening tracker…";
            var profile = Path.Combine(root, "Data", "TrackerProfile");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (IsDisposed || closing.IsCancellationRequested) return;
            view = new WebView2 { Dock = DockStyle.Fill };
            trackerPage.Controls.Add(view);
            view.BringToFront();
            await view.EnsureCoreWebView2Async(environment);
            if (IsDisposed || closing.IsCancellationRequested) return;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.NavigationStarting += (_, args) =>
            {
                if (!IsTrackerAddress(args.Uri))
                {
                    args.Cancel = true;
                    message.Text = "Tracker navigation is limited to the MW4 site.";
                    return;
                }
                trackerDocumentReady = false;
                trackerNavigation++;
                lastTrackerSnapshot = null;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += async (_, args) =>
            {
                trackerDocumentReady = args.IsSuccess && IsTrackerAddress(core.Source);
                message.Text = trackerDocumentReady ? "Sync stays active in the tray." : "Tracker page failed to load.";
                if (trackerDocumentReady) await PublishTrackerEventsAsync();
            };
            core.WebMessageReceived += async (_, args) => await HandleTrackerMessageAsync(core, args);
            var script = ReadTrackerResource("TrackerHost") + "\n" + ReadTrackerResource("Tracker") +
                "\n" + ReadTrackerResource("Backup");
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "(() => { if (window.top !== window || location.origin !== " +
                JsonSerializer.Serialize(TrackerUri.GetLeftPart(UriPartial.Authority)) +
                ") return; document.addEventListener('DOMContentLoaded', () => {\n" + script + "\n}); })();");
            if (IsDisposed || closing.IsCancellationRequested) return;
            trackerView = view;
            trackerTimer.Start();
            core.Navigate(TrackerUri.AbsoluteUri);
        }
        catch (Exception error)
        {
            if (!IsDisposed) message.Text = "Could not open tracker: " + error.Message;
            trackerView = null;
            trackerDocumentReady = false;
            trackerTimer.Stop();
        }
        finally
        {
            if (trackerView != view) view?.Dispose();
            trackerLoading = false;
        }
    }

    private static bool IsTrackerAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == TrackerUri.Scheme &&
        uri.Host == TrackerUri.Host && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static string ReadTrackerResource(string name)
    {
        using var stream = typeof(ConnectorWindow).Assembly.GetManifestResourceStream($"CamoConnector.{name}.js")
            ?? throw new InvalidOperationException("Tracker integration script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private HttpRequestMessage TrackerBridgeRequest(HttpMethod method, string path)
    {
        var key = File.ReadAllText(Path.Combine(root, "Data", "bridge-pairing.key")).Trim();
        return new HttpRequestMessage(method, "http://127.0.0.1:47150/" + path)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", key) }
        };
    }

    private async Task PublishTrackerEventsAsync()
    {
        if (trackerBusy || !trackerDocumentReady || !bridgeReady ||
            trackerView?.CoreWebView2 is not { } core || !IsTrackerAddress(core.Source)) return;
        trackerBusy = true;
        try
        {
            using var request = TrackerBridgeRequest(HttpMethod.Get, "tracker-events");
            using var response = await client.SendAsync(request, closing.Token);
            if (!response.IsSuccessStatusCode) return;
            var snapshot = await response.Content.ReadAsStringAsync(closing.Token);
            // Keep the bridge lease alive while the page lays out at the new size.
            if (!IsDisposed && !trackerResizing && trackerDocumentReady && IsTrackerAddress(core.Source))
            {
                core.PostWebMessageAsJson(snapshot == lastTrackerSnapshot ? "{\"tick\":true}" :
                    "{\"events\":" + snapshot + "}");
                lastTrackerSnapshot = snapshot;
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or
            OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            LogLine(bridgeMessages, "Embedded tracker: " + error.Message);
        }
        finally { trackerBusy = false; }
    }

    private async Task HandleTrackerMessageAsync(CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!trackerDocumentReady || !bridgeReady || !IsTrackerAddress(args.Source)) return;
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var rootElement = message.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object ||
                !rootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            if (type.GetString() is "mw4-read-backup" or "mw4-save-backup")
            {
                await HandleTrackerBackupAsync(core, rootElement, type.GetString() == "mw4-save-backup");
                return;
            }
            if (type.GetString() != "mw4-focus" || !rootElement.TryGetProperty("weapon", out var value) ||
                value.ValueKind != JsonValueKind.String || value.GetString() is not { } weapon ||
                !Regex.IsMatch(weapon, @"\A[a-zA-Z0-9 .-]{2,32}\z")) return;
            using var request = TrackerBridgeRequest(HttpMethod.Post, "focus?weapon=" + Uri.EscapeDataString(weapon));
            request.Headers.Add("X-MW4-Connector", "focus-v1");
            using var response = await client.SendAsync(request, closing.Token);
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or IOException or
            OperationCanceledException or InvalidOperationException)
        {
            LogLine(bridgeMessages, "Embedded tracker message: " + error.Message);
        }
    }

    private async Task HandleTrackerBackupAsync(CoreWebView2 core, JsonElement message, bool save)
    {
        if (!message.TryGetProperty("backupRequestId", out var id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var requestId) || requestId <= 0) return;
        var navigation = trackerNavigation;
        object result = new { error = "Local backup unavailable" };
        try
        {
            using var request = TrackerBridgeRequest(save ? HttpMethod.Post : HttpMethod.Get, "backup");
            request.Headers.Add("X-MW4-Connector", "backup-v1");
            if (save && message.TryGetProperty("backup", out var backup) && backup.ValueKind == JsonValueKind.Object)
                request.Content = new StringContent(backup.GetRawText(), System.Text.Encoding.UTF8, "application/json");
            if (!save || request.Content != null)
            {
                using var response = await client.SendAsync(request, closing.Token);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(closing.Token));
                    result = document.RootElement.Clone();
                }
            }
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or IOException or
            OperationCanceledException or InvalidOperationException)
        {
            LogLine(bridgeMessages, "Embedded tracker backup: " + error.Message);
        }
        if (!IsDisposed && trackerDocumentReady && trackerNavigation == navigation && IsTrackerAddress(core.Source))
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { backupRequestId = requestId, result }));
    }

    private void HideTrackerToTray()
    {
        if (trackerTray == null)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Camo Connector", null, (_, _) => RestoreTracker());
            menu.Items.Add("Exit", null, (_, _) => Close());
            trackerTray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Camo Connector", ContextMenuStrip = menu };
            trackerTray.DoubleClick += (_, _) => RestoreTracker();
        }
        trackerTray.Visible = true;
        Hide();
    }

    private void RestoreTracker()
    {
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
        if (trackerTray != null) trackerTray.Visible = false;
        _ = PublishTrackerEventsAsync();
    }

    private void StopTracker()
    {
        trackerTimer.Dispose();
        if (trackerTray == null) return;
        trackerTray.Visible = false;
        trackerTray.ContextMenuStrip?.Dispose();
        trackerTray.Dispose();
    }
}
