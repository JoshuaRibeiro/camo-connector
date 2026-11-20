using CamoConnector.Capture;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly CheckBox automaticTracking = new() { Text = "Auto-detect Call of Duty on PC", AutoSize = true, Checked = true };
    private readonly Label captureBannerText = new() { AutoSize = true, Anchor = AnchorStyles.Left, UseMnemonic = false };
    private Source? activeCapture;
    private bool captureWasAutomatic;
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
            if (!automaticTracking.Checked && captureWasAutomatic && !detectorStopRequested)
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
        if (!bridgeReady || detector != null || !automaticTracking.Checked ||
            MissingDetectionComponent != null || DateTimeOffset.UtcNow < nextAutomaticCheck) return;
        nextAutomaticCheck = DateTimeOffset.UtcNow.AddSeconds(3);
        var candidates = GameWindows.List().Where(window => window.IsCallOfDuty).ToArray();
        var game = candidates.FirstOrDefault(window => GameWindows.VisibleClient(window) != null) ??
            (candidates.Length == 1 ? candidates[0] : null);
        if (game != null) _ = RunDetectorAsync(new Source("--window", game.Input, game.Title));
    }

    private void RefreshCaptureBanner()
    {
        string message;
        if (!bridgeReady) message = status.Text;
        else if (MissingDetectionComponent is { } missing) message = missing + " is missing";
        else if (detector is { HasExited: false } && activeCapture is { } source)
        {
            message = source.Mode == "--window" && GameWindows.VisibleClient(GameWindows.Parse(source.Input)) == null
                ? "Paused · Bring the game to the foreground on one display"
                : (captureWasAutomatic ? "Game detected · " : "Tracking · ") + source.Name;
        }
        else message = automaticTracking.Checked
            ? "Game not detected · Select your game window in Auto tracking"
            : "Auto tracking is off";
        captureBannerText.Text = message;
        if (dark == true) captureBannerText.ForeColor = detector is { HasExited: false } ? ShellAccent : ShellMuted;
    }
}
