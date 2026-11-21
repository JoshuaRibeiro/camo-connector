#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamoConnector.Update;

public sealed class ReleaseVerification
{
    public sealed record Manifest(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("asset")] string Asset,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha256")] string Sha256);

    public static Manifest VerifyManifest(byte[] bytes, byte[] signature, string trustedPublicKey,
        string releaseTag, Version installedVersion, string package = "win-x64", bool legacy = false)
    {
        if (bytes.Length is 0 or > 16384 || signature.Length is 0 or > 1024)
            throw new InvalidDataException("Update manifest or signature has an invalid size.");
        using var key = RSA.Create();
        key.ImportFromPem(trustedPublicKey);
        if (key.KeySize < 2048 || !key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The update manifest was not signed by a trusted release key.");
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes)
            ?? throw new InvalidDataException("Update manifest is empty.");
        if (!Version.TryParse(manifest.Version, out var version) || version.Build < 0 || version.Revision != -1 ||
            manifest.Version != version.ToString(3) || version <= installedVersion || releaseTag != "v" + manifest.Version ||
            manifest.Asset != (legacy ? "CamoConnector-v" : "CoDStreamingTools-v") + manifest.Version + "-" + package + ".zip" ||
            manifest.Size is <= 0 or > 512L * 1024 * 1024 ||
            manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Update manifest does not match an eligible Windows release.");
        return manifest;
    }

    public static void VerifyArchive(Stream archive, Manifest manifest)
    {
        if (archive.Length != manifest.Size)
            throw new InvalidDataException("Downloaded update has an unexpected size.");
        archive.Position = 0;
        var checksum = SHA256.HashData(archive);
        if (!CryptographicOperations.FixedTimeEquals(checksum, Convert.FromHexString(manifest.Sha256)))
            throw new CryptographicException("Downloaded update does not match the signed manifest.");
        archive.Position = 0;
    }
}
