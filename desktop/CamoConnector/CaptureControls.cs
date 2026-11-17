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
    private readonly ComboBox sources = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Button refreshSources = new() { Text = "Refresh sources", AutoSize = true };
    private readonly Button startDetection = new() { Text = "Start detection", AutoSize = true };
    private readonly Button stopDetection = new() { Text = "Stop", AutoSize = true };
    private readonly Label detectorStatus = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly PictureBox preview = new() { Width = 510, Height = 230, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label previewStatus = new() { AutoSize = true, MaximumSize = new Size(510, 0) };
    private readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 66 };
    private byte[]? pendingPreview;
    private CancellationTokenSource? previewRead;
    private Process? previewWorker;
    private Task<string>? previewErrors;
    private string DetectorPath => Path.Combine(root, "Runtime", "Detector", "CamoConnector.Detector.exe");
    private string OcrDirectory => Path.Combine(root, "Runtime", "Ocr");
    private string? MissingDetectionComponent => !File.Exists(DetectorPath) ? "Detector executable" :
        !File.Exists(Path.Combine(OcrDirectory, "tesseract.exe")) ? "Tesseract OCR executable" :
        !File.Exists(Path.Combine(OcrDirectory, "tessdata", "eng.traineddata")) ? "English OCR model" : null;
    private string DataDirectory => Path.Combine(root, "Data");
    private string SourceSettings => Path.Combine(DataDirectory, "capture-source.json");
    private Process? detector;
    private bool scanning, bridgeReady;

    private void AddCaptureControls(FlowLayoutPanel layout)
    {
        layout.Controls.Add(new Label { Text = "Capture source", AutoSize = true });
        var picker = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        picker.Controls.AddRange([sources, refreshSources]);
        layout.Controls.Add(picker);
        var controls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        controls.Controls.AddRange([startDetection, stopDetection]);
        layout.Controls.Add(controls);
        layout.Controls.Add(detectorStatus);
        layout.Controls.Add(preview);
        layout.Controls.Add(previewStatus);
        sources.SelectedIndexChanged += (_, _) => { UpdateCaptureButtons(); StartPreview(); };
        refreshSources.Click += async (_, _) => await RefreshSourcesAsync();
        startDetection.Click += async (_, _) => await RunDetectorAsync();
        stopDetection.Click += (_, _) =>
        {
            try { StopDetector(); }
            catch (Exception error) { detectorStatus.Text = "Could not stop the detector: " + error.Message; }
        };
        previewTimer.Tick += (_, _) => ReadPreview();
        previewTimer.Start();
        UpdateCaptureButtons();
    }

    private void UpdateCaptureButtons()
    {
        sources.Enabled = detector == null && !scanning;
        refreshSources.Enabled = detector == null && !scanning;
        startDetection.Enabled = bridgeReady && detector == null && !scanning && sources.SelectedItem != null &&
            MissingDetectionComponent == null;
        stopDetection.Enabled = detector != null;
    }

    private async Task RefreshSourcesAsync()
    {
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
                try { selected = JsonSerializer.Deserialize<Source>(File.ReadAllText(SourceSettings)); }
                catch (JsonException) { }
            }
            detectorStatus.Text = MissingDetectionComponent is { } missing
                ? missing + " missing. Extract the complete desktop preview ZIP."
                : "";
            if (File.Exists(DetectorPath))
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
            if (!IsDisposed && MissingDetectionComponent == null)
                detectorStatus.Text = "Capture device scan failed: " + error.Message;
            LogLine(captureMessages, "Capture device scan failed: " + error.Message);
        }
        finally
        {
            scanning = false;
            if (!IsDisposed && !closing.IsCancellationRequested)
            {
                sources.Items.Clear();
                sources.Items.AddRange(found.ToArray());
                var preferred = found.FindIndex(source => selected != null && source.Mode == selected.Mode &&
                    (selected.MonitorId != null ? source.MonitorId == selected.MonitorId : source.Input == selected.Input));
                if (found.Count > 0) sources.SelectedIndex = Math.Max(0, preferred);
                UpdateCaptureButtons();
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

    private async Task RunDetectorAsync()
    {
        if (!bridgeReady || detector != null || sources.SelectedItem is not Source source) return;
        Process? child = null;
        try
        {
            StopPreview();
            Directory.CreateDirectory(DataDirectory);
            var staging = SourceSettings + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, JsonSerializer.Serialize(source));
                File.Move(staging, SourceSettings, true);
            }
            finally { File.Delete(staging); }
            child = detector = LaunchDetector(source.Mode, source.MonitorId ?? source.Input, DataDirectory, "--preview");
            var lastError = "";
            child.ErrorDataReceived += (_, item) =>
            {
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
            var error = lastError.Trim();
            if (!IsDisposed)
                detectorStatus.Text = child.ExitCode == 0 || error.Length == 0 ? "Detector stopped." : error;
        }
        catch (Exception error) { if (!IsDisposed) detectorStatus.Text = error.Message; }
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
            if (scanning || detector != null || closing.IsCancellationRequested ||
                sources.SelectedItem is not Source source || !File.Exists(DetectorPath)) return;
            var mode = source.Mode == "--device" ? "--preview-device" : "--preview-monitor";
            previewWorker = LaunchDetector(mode, source.MonitorId ?? source.Input);
            _ = StartReadingPreview(previewWorker);
            previewErrors = previewWorker.StandardError.ReadToEndAsync();
            previewStatus.Text = "Waiting for a frame from " + source.Name;
            LogLine(captureMessages, previewStatus.Text);
        }
        catch (Exception error)
        {
            previewStatus.Text = "Could not start capture preview. See the Capture log.";
            LogLine(captureMessages, $"Could not start capture preview using {DetectorPath}: {error.Message}");
        }
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
        if (detector != null) StopProcess(detector);
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
