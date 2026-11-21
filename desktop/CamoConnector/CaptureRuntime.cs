using CamoConnector.Update;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly Button enableCaptureDevices = new() { Text = "Download capture support", AutoSize = true };
    private bool installingRuntime;
    private bool OcrReady => File.Exists(Path.Combine(OcrDirectory, "tesseract.exe")) &&
        File.Exists(Path.Combine(OcrDirectory, "tessdata", "eng.traineddata"));
    private bool BundledCaptureReady => File.Exists(Path.Combine(root, "Runtime", "Capture", "ffmpeg.exe"));

    private bool CaptureReady => BundledCaptureReady || (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Any(directory => Path.IsPathFullyQualified(directory) &&
            File.Exists(Path.Combine(directory, "ffmpeg.exe")));

    private async Task InstallRuntimeAsync(string component)
    {
        var kind = component == "Ocr" ? "OCR" : "capture support";
        if (updateKey == null)
            throw new IOException($"Bundled {kind} is missing. Download the offline ZIP or use a signed standard release.");
        installingRuntime = true;
        UpdateCaptureButtons();
        detectorStatus.Text = $"Downloading {kind}…";
        RefreshCaptureBanner();
        try
        {
            using var http = new HttpClient();
            var release = new GitHubRelease(http);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var version = new Version(desktopVersion.Major, desktopVersion.Minor, desktopVersion.Build);
            var manifest = await release.RuntimeAsync(version, component.ToLowerInvariant(), updateKey, deadline.Token);
            var path = Path.Combine(Path.GetTempPath(), "CoDStreamingTools-runtime-" + Guid.NewGuid().ToString("N") + ".zip");
            await using var archive = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                65536, FileOptions.DeleteOnClose);
            await release.DownloadArchiveAsync(manifest, archive, deadline.Token);
            await Task.Run(() => PortableReplacement.InstallRuntime(archive, manifest, root, component), deadline.Token);
            detectorStatus.Text = $"{kind} is ready.";
        }
        catch (OperationCanceledException) when (!closing.IsCancellationRequested)
        {
            throw new IOException($"The {kind} download timed out. Try again or use the offline ZIP.");
        }
        finally
        {
            installingRuntime = false;
            if (!IsDisposed && !closing.IsCancellationRequested) UpdateCaptureButtons();
        }
    }
}
