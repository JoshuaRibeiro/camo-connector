using CamoConnector.Capture;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly CheckBox automaticTracking = new() { Text = "Enable auto tracking", AutoSize = true, Checked = true };
    private readonly Label captureBannerText = new() { AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = false };
    private Source? activeCapture;
    private DateTimeOffset nextAutomaticCheck;
    private string AutomaticCaptureSettings => Path.Combine(DataDirectory, "auto-capture.txt");

    private void BindAutomaticTracking()
    {
        try
        {
            if (File.Exists(AutomaticCaptureSettings))
                automaticTracking.Checked = File.ReadAllText(AutomaticCaptureSettings).Trim() != "off";
        }
        catch (IOException error) { LogLine(captureMessages, error.Message); }
        catch (UnauthorizedAccessException error) { LogLine(captureMessages, error.Message); }
        automaticTracking.CheckedChanged += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(AutomaticCaptureSettings, automaticTracking.Checked ? "on" : "off");
            }
            catch (IOException error) { LogLine(captureMessages, error.Message); }
            catch (UnauthorizedAccessException error) { LogLine(captureMessages, error.Message); }
            nextAutomaticCheck = DateTimeOffset.MinValue;
            if (!automaticTracking.Checked && !detectorStopRequested)
            {
                try { StopDetector(); }
                catch (Exception error) { detectorStatus.Text = error.Message; }
            }
            RefreshCaptureBanner();
        };
    }

    private void CheckAutomaticTracking()
    {
        RefreshCaptureBanner();
        if (!bridgeReady || detector != null || scanning || installingRuntime || !automaticTracking.Checked ||
            !File.Exists(DetectorPath) || DateTimeOffset.UtcNow < nextAutomaticCheck) return;
        nextAutomaticCheck = DateTimeOffset.UtcNow.AddSeconds(3);
        if (sources.SelectedItem is Source { Mode.Length: > 0 } source)
        {
            if (source.Mode != "--window" || GameWindows.VisibleClient(GameWindows.Parse(source.Input)) != null)
                _ = RunDetectorAsync(source);
            else if (!GameWindows.Exists(GameWindows.Parse(source.Input)))
                sources.SelectedItem = AutomaticSource;
            return;
        }
        var game = GameWindows.List().FirstOrDefault(window =>
            window.IsCallOfDuty && GameWindows.VisibleClient(window) != null);
        if (game != null) _ = RunDetectorAsync(new Source("--window", game.Input, game.Title));
    }

    private void RefreshCaptureBanner()
    {
        string message;
        if (!bridgeReady) message = status.Text;
        else if (installingRuntime) message = "Downloading tracking support · See Auto tracking";
        else if (!File.Exists(DetectorPath)) message = "The bundled detector is missing";
        else if (detector is { HasExited: false } && activeCapture is { } source)
        {
            message = source.Mode == "--window" && GameWindows.VisibleClient(GameWindows.Parse(source.Input)) == null
                ? "Paused · Bring the game to the foreground on one display"
                : "Tracking · " + source.Name;
        }
        else if (!automaticTracking.Checked) message = "Auto tracking is off";
        else message = sources.SelectedItem is Source { Mode.Length: > 0 } selected
            ? "Waiting for " + selected.Name
            : "Waiting for Call of Duty in the foreground";
        captureBannerText.Text = message;
        if (dark == true) captureBannerText.ForeColor = detector is { HasExited: false } ? ShellAccent : ShellMuted;
    }
}
