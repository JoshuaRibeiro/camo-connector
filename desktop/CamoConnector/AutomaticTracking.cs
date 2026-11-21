namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly TrackingSwitch automaticTracking = new() { Text = "Enable auto tracking", Checked = true };
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
            previewRestarted = false;
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
            CheckAutomaticTracking();
            if (detector == null && !installingRuntime) StartPreview();
            RefreshCaptureBanner();
        };
    }

    private void CheckAutomaticTracking()
    {
        RefreshCaptureBanner();
        if (closing.IsCancellationRequested || !bridgeReady || detector != null || scanning || installingRuntime || !automaticTracking.Checked ||
            !File.Exists(DetectorPath) || DateTimeOffset.UtcNow < nextAutomaticCheck) return;
        if (sources.SelectedItem is not Source { Mode.Length: > 0 } source) return;
        nextAutomaticCheck = DateTimeOffset.UtcNow.AddSeconds(3);
        _ = RunDetectorAsync(source);
    }

    private void RefreshCaptureBanner()
    {
        string message;
        if (!bridgeReady) message = status.Text;
        else if (installingRuntime) message = "Downloading tracking support · See Auto tracking";
        else if (!File.Exists(DetectorPath)) message = "The bundled detector is missing";
        else if (detector is { HasExited: false } && activeCapture is { } source)
            message = "Tracking · " + source.Name;
        else if (!automaticTracking.Checked) message = "Auto tracking is off";
        else message = sources.SelectedItem is Source { Mode.Length: > 0 } selected
            ? "Waiting for " + selected.Name
            : "Select a monitor or capture device";
        captureBannerText.Text = message;
        if (dark == true) captureBannerText.ForeColor = detector is { HasExited: false } ? ShellAccent : ShellMuted;
    }
}
