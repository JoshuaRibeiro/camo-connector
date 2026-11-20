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
    private static string ArchivePath(string root) => Path.Combine(Folder(root), "update.zip");

    public static async Task DownloadAsync(string root, GitHubRelease releases, GitHubRelease.SignedRelease release,
        CancellationToken cancel)
    {
        var directory = Directory.CreateDirectory(Folder(root)).FullName;
        var archive = ArchivePath(root);
        var partial = archive + ".partial";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true))
                await releases.DownloadArchiveAsync(release.Manifest, stream, cancel);
            cancel.ThrowIfCancellationRequested();
            File.WriteAllBytes(Path.Combine(directory, "manifest.json"), release.Metadata);
            File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), release.Signature);
            File.Move(partial, archive, overwrite: true);
        }
        finally { File.Delete(partial); }
    }

    public static FileStream? Open(string root, Version installed, string publicKey,
        out ReleaseVerification.Manifest? manifest)
    {
        manifest = null;
        if (!File.Exists(ArchivePath(root))) return null;
        var directory = Folder(root);
        try
        {
            var metadata = ReadBounded(Path.Combine(directory, "manifest.json"), 16384);
            var signature = ReadBounded(Path.Combine(directory, "manifest.sig"), 1024);
            using var json = JsonDocument.Parse(metadata);
            if (!json.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                return null;
            var verified = ReleaseVerification.VerifyManifest(metadata, signature, publicKey, "v" + version.GetString(), installed, Package);
            var archive = File.OpenRead(ArchivePath(root));
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

    public static void Discard(string root) => File.Delete(ArchivePath(root));

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("Cached release metadata exceeds its size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
