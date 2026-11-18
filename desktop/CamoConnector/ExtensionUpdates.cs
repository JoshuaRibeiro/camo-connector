using System.IO.Compression;
using System.Text.Json;
using CamoConnector.Update;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private static readonly HashSet<string> ExtensionFiles = new(StringComparer.Ordinal)
    {
        "manifest.json", "background.js", "tracker.js", "backup.js", "pairing.html", "pairing.css", "pairing.js",
        "icon-16.png", "icon-32.png", "icon-48.png", "icon-128.png"
    };

    private async Task UpdateBrowserExtensionsAsync(GitHubRelease releases, CancellationToken cancel)
    {
        var extensions = Path.Combine(root, "Data", "Extensions");
        var changed = new List<string>();
        var results = new List<string>();
        foreach (var browser in new[] { "Firefox", "Opera" })
        {
            if (cancel.IsCancellationRequested || IsDisposed) return;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                var package = await releases.FindExtensionAsync(browser, deadline.Token);
                if (package == null)
                {
                    results.Add($"{browser}: package unavailable.");
                    continue;
                }
                var folder = Path.Combine(extensions, browser);
                var previous = folder + ".previous";
                if (!Directory.Exists(folder) && Directory.Exists(previous)) Directory.Move(previous, folder);
                if (ReadExtensionVersion(folder) is { } installed && installed >= package.Version)
                {
                    results.Add($"{browser}: v{installed} ready.");
                    continue;
                }
                if (IsDisposed || cancel.IsCancellationRequested) return;
                extensionStatus.Text = $"Downloading {browser} extension v{package.Version}.";
                using var zip = new MemoryStream();
                await releases.DownloadExtensionAsync(package, zip, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                zip.Position = 0;
                InstallExtension(zip, package.Version, folder, previous);
                changed.Add(browser);
                results.Add($"{browser}: v{package.Version} downloaded.");
            }
            catch (Exception error)
            {
                results.Add($"{browser}: {error.Message}");
            }
        }

        if (IsDisposed || cancel.IsCancellationRequested) return;
        extensionStatus.Text = string.Join(Environment.NewLine, results);
        if (changed.Count == 0) return;
        var instructions = new List<string>();
        if (changed.Contains("Firefox"))
            instructions.Add("Firefox: Open about:debugging#/runtime/this-firefox and click Reload. " +
                "First time or after restarting Firefox, choose Load Temporary Add-on and select:" +
                Environment.NewLine + Path.Combine(extensions, "Firefox", "manifest.json"));
        if (changed.Contains("Opera"))
            instructions.Add("Opera: Open opera://extensions, enable Developer mode, click Load unpacked, and select:" +
                Environment.NewLine + Path.Combine(extensions, "Opera"));
        MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, instructions),
            "Reload browser extensions", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static Version? ReadExtensionVersion(string folder)
    {
        var manifest = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return Version.TryParse(document.RootElement.GetProperty("version").GetString(), out var version) ? version : null;
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static void InstallExtension(Stream zip, Version version, string folder, string previous)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (names.Length != ExtensionFiles.Count || !ExtensionFiles.SetEquals(names) ||
            archive.Entries.Any(entry => entry.Length is <= 0 or > 8 * 1024 * 1024))
            throw new InvalidDataException("Unexpected files in the extension ZIP.");

        var staging = Directory.CreateDirectory(folder + "." + Guid.NewGuid().ToString("N")).FullName;
        try
        {
            foreach (var entry in archive.Entries)
                entry.ExtractToFile(Path.Combine(staging, entry.FullName));
            if (ReadExtensionVersion(staging) != version)
                throw new InvalidDataException("The extension manifest version does not match the release.");
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            if (Directory.Exists(folder)) Directory.Move(folder, previous);
            try { Directory.Move(staging, folder); }
            catch
            {
                if (!Directory.Exists(folder) && Directory.Exists(previous)) Directory.Move(previous, folder);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
