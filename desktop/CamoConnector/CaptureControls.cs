using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private sealed record Source(string Mode, string Input, string Name, string? MonitorId = null)
    {
        public override string ToString() => Name;
    }


    private sealed record Device(string Name, string Input);
    private readonly ComboBox sources = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button refreshSources = new() { Text = "Refresh sources", AutoSize = true };
    private readonly Label detectorStatus = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label previewStatus = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 33 };
    private byte[]? pendingPreview;
    private CancellationTokenSource? previewRead;
    private Process? previewWorker;
    private Task<string>? previewErrors;
    private string DetectorPath => Path.Combine(root, "Runtime", "Detector", "CamoConnector.Detector.exe");
    private string OcrDirectory => Path.Combine(root, "Runtime", "Ocr");
    private string DataDirectory => Path.Combine(root, "Data");
    private string SourceSettings => Path.Combine(DataDirectory, "capture-source.json");
    private Process? detector;
    private bool detectorStopRequested;
    private bool scanning, bridgeReady;

    private void BindCaptureControls()
    {
        sources.SelectedIndexChanged += (_, _) =>
        {
            nextAutomaticCheck = DateTimeOffset.MinValue;
            if (!scanning && sources.SelectedItem is Source source)
            {
                try
                {
                    Directory.CreateDirectory(DataDirectory);
                    if (source.Mode is "--monitor" or "--device")
                        File.WriteAllText(SourceSettings, JsonSerializer.Serialize(source));
                    else File.Delete(SourceSettings);
                }
                catch (IOException error) { LogLine(captureMessages, error.Message); }
                catch (UnauthorizedAccessException error) { LogLine(captureMessages, error.Message); }
            }
            UpdateCaptureButtons();
            StartPreview();
        };
        refreshSources.Click += async (_, _) => await RefreshSourcesAsync();
        enableCaptureDevices.Click += async (_, _) =>
        {
            try
            {
                await InstallRuntimeAsync("Capture");
                await RefreshSourcesAsync();
            }
            catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!IsDisposed) detectorStatus.Text = error.Message;
                LogLine(captureMessages, error.Message);
            }
        };
        previewTimer.Tick += (_, _) => ReadPreview();
        previewTimer.Start();
        UpdateCaptureButtons();
    }

    private void UpdateCaptureButtons()
    {
        sources.Enabled = detector == null && !scanning && !installingRuntime;
        refreshSources.Enabled = detector == null && !scanning && !installingRuntime;
        enableCaptureDevices.Visible = !BundledCaptureReady;
        enableCaptureDevices.Enabled = !scanning && !installingRuntime && detector == null;
        RefreshCaptureBanner();
        if (detectionSummary != null)
            detectionSummary.Text = scanning ? "Finding capture sources…" : detector != null ? "Detection running" :
                automaticTracking.Checked ? "Waiting for capture" : "Auto tracking is off";
    }

    private async Task RefreshSourcesAsync()
    {
        if (scanning || installingRuntime || detector != null) return;
        scanning = true;
        UpdateCaptureButtons();
        var selected = sources.SelectedItem as Source;
        var found = Screen.AllScreens.Select((screen, index) =>
            new Source("--monitor", index.ToString(), $"Monitor {index + 1} ({screen.Bounds.Width} × {screen.Bounds.Height})",
                screen.DeviceName)).ToList();
        try
        {
            StopPreview();
            if (selected == null && File.Exists(SourceSettings))
            {
                try
                {
                    selected = JsonSerializer.Deserialize<Source>(File.ReadAllText(SourceSettings));
                    if (selected?.Mode == "--window") selected = null;
                }
                catch (JsonException) { }
            }
            detectorStatus.Text = File.Exists(DetectorPath) ? "" : "The bundled detector executable is missing.";
            if (File.Exists(DetectorPath) && CaptureReady)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var discovery = LaunchDetector("--list-devices");
                try
                {
                    var output = discovery.StandardOutput.ReadToEndAsync(deadline.Token);
                    var errors = discovery.StandardError.ReadToEndAsync(deadline.Token);
                    await discovery.WaitForExitAsync(deadline.Token);
                    if (discovery.ExitCode != 0) throw new IOException((await errors).Trim());
                    var devices = JsonSerializer.Deserialize<Device[]>(await output) ?? [];
                    found.AddRange(devices.Select(device => new Source("--device", device.Input, device.Name)));
                }
                finally { StopProcess(discovery); }
            }
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (!IsDisposed && File.Exists(DetectorPath))
                detectorStatus.Text = "Capture device scan failed: " + error.Message;
            LogLine(captureMessages, "Capture device scan failed: " + error.Message);
        }
        finally
        {
            if (!IsDisposed && !closing.IsCancellationRequested)
            {
                sources.Items.Clear();
                sources.Items.AddRange(found.ToArray());
                var preferred = found.FindIndex(source => selected != null && source.Mode == selected.Mode &&
                    (selected.MonitorId != null ? source.MonitorId == selected.MonitorId : source.Input == selected.Input));
                sources.SelectedIndex = preferred >= 0 || selected != null ? preferred :
                    found.FindIndex(source => source.MonitorId == Screen.PrimaryScreen?.DeviceName);
            }
            scanning = false;
            if (!IsDisposed && !closing.IsCancellationRequested)
            {
                UpdateCaptureButtons();
                StartPreview();
            }
        }
    }

    private Process LaunchDetector(params string[] arguments)
    {
        var start = new ProcessStartInfo(DetectorPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start the detector.");
    }

    private async Task RunDetectorAsync(Source source)
    {
        if (!bridgeReady || detector != null || installingRuntime || !File.Exists(DetectorPath)) return;
        activeCapture = source;
        Process? child = null;
        try
        {
            StopPreview();
            if (!OcrReady) await InstallRuntimeAsync("Ocr");
            if (closing.IsCancellationRequested || !automaticTracking.Checked) return;
            Directory.CreateDirectory(DataDirectory);
            detectorStopRequested = false;
            const int retryLimit = 3;
            for (var attempt = 0; ; attempt++)
            {
                child = detector = LaunchDetector(source.Mode, source.MonitorId ?? source.Input, DataDirectory, "--preview");
                var lastError = "";
                string? detectorError = null;
                child.ErrorDataReceived += (_, item) =>
                {
                    if (LogPreviewDiagnostic(item.Data)) return;
                    if (item.Data?.StartsWith("Detector error: ", StringComparison.Ordinal) == true)
                        detectorError = item.Data;
                    if (!string.IsNullOrWhiteSpace(item.Data)) lastError = item.Data;
                    LogLine(detectorMessages, item.Data);
                    if (source.Mode == "--device") LogLine(captureMessages, item.Data);
                };
                child.BeginErrorReadLine();
                var output = StartReadingPreview(child);
                detectorStatus.Text = "Detecting from " + source.Name;
                LogLine(detectorMessages, detectorStatus.Text);
                UpdateCaptureButtons();
                await child.WaitForExitAsync();
                await output;
                if (source.Mode == "--device" && child.ExitCode != 0 && !detectorStopRequested &&
                    !closing.IsCancellationRequested && !IsDisposed && attempt < retryLimit)
                {
                    detectorStatus.Text = $"Capture or detection interrupted. Reconnecting to {source.Name} ({attempt + 1}/{retryLimit})…";
                    LogLine(detectorMessages, detectorStatus.Text);
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), closing.Token);
                    if (detectorStopRequested) { detectorStatus.Text = "Detector stopped."; return; }
                    StopProcess(child);
                    detector = null;
                    child.Dispose();
                    child = null;
                    continue;
                }
                var error = detectorError ?? lastError.Trim();
                if (!IsDisposed)
                    detectorStatus.Text = detectorStopRequested || child.ExitCode == 0 ? "Detector stopped." :
                        error.Length == 0 ? $"Detector exited with code {child.ExitCode}. See the Detector log." : error;
                break;
            }
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed) detectorStatus.Text = error.Message;
            nextAutomaticCheck = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        finally
        {
            if (child != null)
            {
                try { StopProcess(child); }
                catch (Exception error) { if (!IsDisposed) detectorStatus.Text = error.Message; }
                finally
                {
                    child.Dispose();
                    if (detector == child) detector = null;
                }
            }
            activeCapture = null;
            if (!IsDisposed && !closing.IsCancellationRequested)
            {
                UpdateCaptureButtons();
                StartPreview();
            }
        }
    }

    private void StartPreview()
    {
        try
        {
            StopPreview();
            if (scanning || detector != null || closing.IsCancellationRequested || pages.SelectedTab != capturePage ||
                sources.SelectedItem is not Source source || !File.Exists(DetectorPath)) return;
            var mode = source.Mode == "--device" ? "--preview-device" : "--preview-monitor";
            previewWorker = LaunchDetector(mode, source.MonitorId ?? source.Input);
            _ = StartReadingPreview(previewWorker);
            previewStatus.Text = "Waiting for a frame from " + source.Name;
            LogLine(captureMessages, previewStatus.Text);
            previewErrors = ReadPreviewDiagnosticsAsync(previewWorker.StandardError);
        }
        catch (Exception error)
        {
            previewStatus.Text = "Could not start capture preview. See the Capture log.";
            LogLine(captureMessages, $"Could not start capture preview using {DetectorPath}: {error.Message}");
        }
    }

    private bool LogPreviewDiagnostic(string? line)
    {
        const string prefix = "Capture preview: ";
        if (line?.StartsWith(prefix, StringComparison.Ordinal) != true) return false;
        LogLine(captureMessages, line[prefix.Length..]);
        return true;
    }

    private async Task<string> ReadPreviewDiagnosticsAsync(StreamReader stderr)
    {
        var lastError = "";
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (!LogPreviewDiagnostic(line) && !string.IsNullOrWhiteSpace(line)) lastError = line;
        }
        catch (IOException error) { lastError = error.Message; }
        catch (ObjectDisposedException) { }
        return lastError;
    }

    private void StopPreview()
    {
        if (previewWorker != null)
        {
            try { StopProcess(previewWorker); }
            finally { previewWorker.Dispose(); previewWorker = null; previewErrors = null; }
        }
        SetPreview(null);
        previewRead?.Cancel();
        previewRead?.Dispose();
        previewRead = null;
        Interlocked.Exchange(ref pendingPreview, null);
    }

    private Task StartReadingPreview(Process process)
    {
        previewRead = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        return ReadPreviewAsync(process.StandardOutput.BaseStream, previewRead.Token);
    }

    private async Task ReadPreviewAsync(Stream input, CancellationToken cancel)
    {
        var header = new byte[4];
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await input.ReadExactlyAsync(header, cancel).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > 1024 * 1024)
                    throw new IOException("Invalid preview frame length.");
                var frame = new byte[length];
                await input.ReadExactlyAsync(frame, cancel).ConfigureAwait(false);
                if (!cancel.IsCancellationRequested) Interlocked.Exchange(ref pendingPreview, frame);
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (IOException error) { LogLine(captureMessages, error.Message); }
    }

    private void ReadPreview()
    {
        if (previewWorker is { HasExited: true })
        {
            previewStatus.Text = previewErrors is { IsCompletedSuccessfully: true } && previewErrors.Result.Length > 0
                ? previewErrors.Result.Trim() : "Preview stopped.";
            LogLine(captureMessages, previewStatus.Text);
            previewWorker.Dispose();
            previewWorker = null;
            previewErrors = null;
        }
        var frame = Interlocked.Exchange(ref pendingPreview, null);
        if (frame == null) return;
        try
        {
            using var stream = new MemoryStream(frame, writable: false);
            using var image = Image.FromStream(stream);
            SetPreview(new Bitmap(image));
            previewStatus.Text = "";
        }
        catch (ArgumentException) { }
    }

    private void SetPreview(Image? next)
    {
        var previous = preview.Image;
        preview.Image = next;
        previous?.Dispose();
    }

    private void StopDetector()
    {
        if (detector == null) return;
        detectorStopRequested = true;
        StopProcess(detector);
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000)) throw new IOException("The detector did not exit.");
        }
        catch (InvalidOperationException) { }
    }
}
