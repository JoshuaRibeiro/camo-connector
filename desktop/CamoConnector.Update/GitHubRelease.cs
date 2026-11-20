#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CamoConnector.Update;

public sealed class GitHubRelease(HttpClient http)
{
    private const string Api = "https://api.github.com/repos/JoshuaRibeiro/camo-connector/releases/latest";
    private const string AssetPrefix = "https://github.com/JoshuaRibeiro/camo-connector/releases/download/";

#if SLIM_PORTABLE
    private const string UpdatePackage = "win-x64-slim";
    private const string UpdateManifest = "portable-slim-update";
#else
    private const string UpdatePackage = "win-x64";
    private const string UpdateManifest = "portable-update";
#endif

    public sealed record SignedRelease(ReleaseVerification.Manifest Manifest, byte[] Metadata, byte[] Signature);

    public async Task<ReleaseVerification.Manifest?> CheckAsync(Version installed, string publicKey, CancellationToken cancel) =>
        (await CheckSignedAsync(installed, publicKey, cancel))?.Manifest;

    public async Task<SignedRelease?> CheckSignedAsync(Version installed, string publicKey, CancellationToken cancel)
    {
        using var metadata = new MemoryStream();
        await DownloadAsync(Api, metadata, 1024 * 1024, cancel);
        using var json = JsonDocument.Parse(metadata.ToArray());
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision != -1 || tag != "v" + version.ToString(3))
            throw new InvalidDataException("The latest desktop release has an invalid version tag.");
        if (version <= installed) return null;
        var prefix = AssetPrefix + tag + "/";
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        string Locate(string name)
        {
            var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1 || matches[0].GetProperty("browser_download_url").GetString() != prefix + name)
                throw new InvalidDataException("The release is missing the expected asset: " + name);
            return prefix + name;
        }
        using var manifest = new MemoryStream();
        using var signature = new MemoryStream();
        await DownloadAsync(Locate(UpdateManifest + ".json"), manifest, 16384, cancel);
        await DownloadAsync(Locate(UpdateManifest + ".sig"), signature, 1024, cancel);
        var verified = ReleaseVerification.VerifyManifest(manifest.ToArray(), signature.ToArray(), publicKey, tag, installed, UpdatePackage);
        Locate(verified.Asset);
        return new(verified, manifest.ToArray(), signature.ToArray());
    }

    public async Task<ReleaseVerification.Manifest> RuntimeAsync(Version version, string component, string publicKey, CancellationToken cancel)
    {
        if (component is not ("ocr" or "capture")) throw new ArgumentOutOfRangeException(nameof(component));
        var tag = "v" + version.ToString(3);
        var prefix = AssetPrefix + tag + "/runtime-" + component;
        using var manifest = new MemoryStream();
        using var signature = new MemoryStream();
        await DownloadAsync(prefix + ".json", manifest, 16384, cancel);
        await DownloadAsync(prefix + ".sig", signature, 1024, cancel);
        return ReleaseVerification.VerifyManifest(manifest.ToArray(), signature.ToArray(), publicKey, tag,
            new Version(0, 0, 0), "win-x64-" + component);
    }

    public async Task DownloadArchiveAsync(ReleaseVerification.Manifest manifest, Stream target, CancellationToken cancel, Action<long>? onBytes = null)
    {
        if (!target.CanRead || !target.CanWrite || !target.CanSeek || target.Length != 0 || target.Position != 0)
            throw new ArgumentException("Use a new empty staging file for the update.", nameof(target));
        var idleTimeout = TimeSpan.FromSeconds(60);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        idle.CancelAfter(idleTimeout);
        try
        {
            await DownloadAsync(AssetPrefix + "v" + manifest.Version + "/" + manifest.Asset, target, manifest.Size, idle.Token, bytes =>
            {
                idle.CancelAfter(idleTimeout);
                onBytes?.Invoke(bytes);
            });
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            throw new TimeoutException("The update download received no data for 60 seconds. Try again or download the Windows ZIP from GitHub Releases.");
        }
        ReleaseVerification.VerifyArchive(target, manifest);
    }

    private async Task DownloadAsync(string url, Stream destination, long limit, CancellationToken cancel, Action<long>? onBytes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CamoConnector");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Update download exceeds its expected size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[65536];
        long bytes = 0;
        int size;
        while ((size = await source.ReadAsync(buffer.AsMemory(), cancel)) != 0)
        {
            bytes += size;
            if (bytes > limit) throw new InvalidDataException("Update download exceeds its expected size.");
            await destination.WriteAsync(buffer.AsMemory(0, size), cancel);
            onBytes?.Invoke(bytes);
        }
    }
}
