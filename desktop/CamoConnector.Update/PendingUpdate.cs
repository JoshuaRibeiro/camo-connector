#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CamoConnector.Update;

public static class PendingUpdate
{
#if SLIM_PORTABLE
    private const string Package = "win-x64-slim";
#else
    private const string Package = "win-x64";
#endif

    private static string Folder(string root) => Path.Combine(root, "Data", "PendingUpdate");
    // Existing installations use the base directory. Switch slots only after all
    // three files are ready so an interrupted refresh preserves the previous release.
    private static string ActiveFolder(string root) => File.Exists(Path.Combine(Folder(root), "alternate.active"))
        ? Path.Combine(Folder(root), "alternate") : Folder(root);

    public static async Task DownloadAsync(string root, GitHubRelease releases, GitHubRelease.SignedRelease release,
        CancellationToken cancel)
    {
        var folder = Directory.CreateDirectory(Folder(root)).FullName;
        using var lease = new FileStream(Path.Combine(folder, "download.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var alternate = ActiveFolder(root) == Folder(root);
        var directory = alternate ? Directory.CreateDirectory(Path.Combine(folder, "alternate")).FullName : folder;
        var archive = Path.Combine(directory, "update.zip");
        var marker = Path.Combine(folder, "alternate.active");
        var partial = archive + ".partial";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true))
                await releases.DownloadArchiveAsync(release.Manifest, stream, cancel);
            cancel.ThrowIfCancellationRequested();
            File.WriteAllBytes(Path.Combine(directory, "manifest.json"), release.Metadata);
            File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), release.Signature);
            File.Move(partial, archive, overwrite: true);
            if (alternate)
            {
                using var active = File.Create(marker);
            }
            else File.Delete(marker);
        }
        finally { File.Delete(partial); }
    }

    public static FileStream? Open(string root, Version installed, string publicKey,
        out ReleaseVerification.Manifest? manifest)
    {
        manifest = null;
        var directory = ActiveFolder(root);
        var path = Path.Combine(directory, "update.zip");
        if (!File.Exists(path)) return null;
        try
        {
            var metadata = ReadBounded(Path.Combine(directory, "manifest.json"), 16384);
            var signature = ReadBounded(Path.Combine(directory, "manifest.sig"), 1024);
            using var json = JsonDocument.Parse(metadata);
            if (!json.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                return null;
            var verified = ReleaseVerification.VerifyManifest(metadata, signature, publicKey, "v" + version.GetString(), installed, Package);
            var archive = File.OpenRead(path);
            try { ReleaseVerification.VerifyArchive(archive, verified); }
            catch { archive.Dispose(); throw; }
            manifest = verified;
            return archive;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static void Discard(string root)
    {
        foreach (var directory in new[] { Folder(root), Path.Combine(Folder(root), "alternate") })
            if (Directory.Exists(directory)) File.Delete(Path.Combine(directory, "update.zip"));
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("Cached release metadata exceeds its size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
