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

    public async Task<ReleaseVerification.Manifest?> CheckAsync(Version installed, string publicKey, CancellationToken cancel)
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
        await DownloadAsync(Locate("portable-update.json"), manifest, 16384, cancel);
        await DownloadAsync(Locate("portable-update.sig"), signature, 1024, cancel);
        var verified = ReleaseVerification.VerifyManifest(manifest.ToArray(), signature.ToArray(), publicKey, tag, installed);
        Locate(verified.Asset);
        return verified;
    }

    public sealed record ExtensionPackage(Version Version, string Name, long Size);

    public async Task<ExtensionPackage?> FindExtensionAsync(string browser, CancellationToken cancel)
    {
        if (browser is not ("Chrome" or "Edge" or "Firefox" or "Opera"))
            throw new ArgumentException("Select a supported browser.", nameof(browser));
        for (var page = 1; ; page++)
        {
            using var metadata = new MemoryStream();
            await DownloadAsync($"https://api.github.com/repos/JoshuaRibeiro/camo-connector/releases?per_page=30&page={page}",
                metadata, 4 * 1024 * 1024, cancel);
            using var json = JsonDocument.Parse(metadata.ToArray());
            ExtensionPackage? latest = null;
            foreach (var release in json.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString();
                const string prefix = "extension-v";
                if (release.GetProperty("draft").GetBoolean() || tag == null || !tag.StartsWith(prefix, StringComparison.Ordinal) ||
                    !Version.TryParse(tag[prefix.Length..], out var version) || version.Build < 0 || version.Revision != -1 ||
                    tag != prefix + version.ToString(3) || (latest != null && version <= latest.Version)) continue;
                var name = $"CamoConnector-{browser}-v{version}.zip";
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() != name ||
                        asset.GetProperty("browser_download_url").GetString() != AssetPrefix + tag + "/" + name) continue;
                    var size = asset.GetProperty("size").GetInt64();
                    if (size is > 0 and <= 32 * 1024 * 1024)
                        latest = new ExtensionPackage(version, name, size);
                }
            }
            if (latest != null || json.RootElement.GetArrayLength() < 30) return latest;
        }
    }

    public async Task DownloadExtensionAsync(ExtensionPackage package, Stream target, CancellationToken cancel)
    {
        await DownloadAsync(AssetPrefix + "extension-v" + package.Version + "/" + package.Name, target, package.Size, cancel);
        if (target.Length != package.Size) throw new InvalidDataException("The extension download is incomplete.");
    }

    public async Task DownloadArchiveAsync(ReleaseVerification.Manifest manifest, Stream target, CancellationToken cancel, Action<long>? onBytes = null)
    {
        if (!target.CanRead || !target.CanWrite || !target.CanSeek || target.Length != 0 || target.Position != 0)
            throw new ArgumentException("Use a new empty staging file for the update.", nameof(target));
        await DownloadAsync(AssetPrefix + "v" + manifest.Version + "/" + manifest.Asset, target, manifest.Size, cancel, onBytes);
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
