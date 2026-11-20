#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CamoConnector.Update;

public sealed class PortableReplacement
{
    private static readonly string[] Managed =
    {
        "CamoConnector.exe", "Runtime/Bridge/CamoConnector.Bridge.exe",
        "Runtime/Bridge/special-catalog.json", "Runtime/Detector/CamoConnector.Detector.exe", "CamoConnector.Updater.exe"
    };
    private readonly string root;
    private readonly string work;
    private readonly string journal;
    private readonly string commit;
    private sealed record RecoveryRecord(string[] Paths, string[] Existing);

    public static bool IsAppFile(string path) => Allowed(path);

    public PortableReplacement(string directory)
    {
        root = Path.GetFullPath(directory);
        work = Path.Combine(root, ".camo-update");
        journal = Path.Combine(work, "rollback.json");
        commit = Path.Combine(work, "committed");
    }

    public void Apply(Stream archive, ReleaseVerification.Manifest manifest, Action<int>? afterReplacement = null, Action<string>? report = null)
    {
        ValidateDirectory(root);
        ValidateDirectory(work);
        ValidateDirectory(Path.Combine(root, "Runtime"));
        ValidateDirectory(Path.Combine(root, "Runtime", "Bridge"));
        ValidateDirectory(Path.Combine(root, "Runtime", "Detector"));
        if (Directory.Exists(work)) throw new IOException("Recover the existing update transaction first.");
        ReleaseVerification.VerifyArchive(archive, manifest);
        Directory.CreateDirectory(work);
        var ready = Path.Combine(work, "new");
        var previous = Path.Combine(work, "previous");
        Directory.CreateDirectory(ready);
        Directory.CreateDirectory(previous);
        try
        {
            report?.Invoke("Extracting update files…");
            var paths = Extract(archive, ready);
            report?.Invoke("Backing up installed files…");
            var existing = new List<string>();
            foreach (var path in paths)
            {
                var destination = ValidateFilePath(root, path);
                if (Directory.Exists(destination)) throw new IOException("An application file path is a directory: " + path);
                if (!File.Exists(destination)) continue;
                CopyDurably(destination, In(previous, path));
                existing.Add(path);
            }
            WriteMarker(journal, JsonSerializer.SerializeToUtf8Bytes(new RecoveryRecord(paths, existing.ToArray())));
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                report?.Invoke($"Replacing files ({index + 1}/{paths.Length})…");
                var destination = ValidateFilePath(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(In(ready, path), destination, true);
                afterReplacement?.Invoke(index + 1);
            }
            WriteMarker(commit, new byte[] { 1 });
        }
        catch
        {
            Recover();
            throw;
        }
        Recover();
    }

    public void Recover()
    {
        ValidateDirectory(root);
        ValidateDirectory(work);
        if (!Directory.Exists(work)) return;
        ValidateDirectory(Path.Combine(root, "Runtime"));
        ValidateDirectory(Path.Combine(root, "Runtime", "Bridge"));
        ValidateDirectory(Path.Combine(root, "Runtime", "Detector"));
        ValidateDirectory(Path.Combine(work, "previous"));
        ValidateDirectory(Path.Combine(work, "new"));
        RejectLink(journal);
        RejectLink(commit);
        if (File.Exists(journal) && !File.Exists(commit))
        {
            var record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllBytes(journal))
                ?? throw new InvalidDataException("The recovery record is empty.");
            if (record.Paths == null || record.Existing == null ||
                record.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != record.Paths.Length ||
                record.Paths.Any(path => !Allowed(path)) ||
                !Managed.All(required => record.Paths.Contains(required, StringComparer.Ordinal)) ||
                record.Existing.Distinct(StringComparer.Ordinal).Count() != record.Existing.Length ||
                record.Existing.Any(path => !record.Paths.Contains(path, StringComparer.Ordinal)))
                throw new InvalidDataException("The recovery record contains unexpected files.");
            foreach (var path in record.Paths)
            {
                var destination = ValidateFilePath(root, path);
                if (!record.Existing.Contains(path, StringComparer.Ordinal))
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    continue;
                }
                var source = ValidateFilePath(Path.Combine(work, "previous"), path);
                var restore = ValidateFilePath(Path.Combine(work, "new"), path + ".restore");
                File.Delete(restore);
                CopyDurably(source, restore);
                File.Move(restore, destination, true);
            }
            WriteMarker(commit, new byte[] { 1 });
        }
        File.Delete(journal);
        Directory.Delete(work, recursive: true);
    }

    public static void InstallRuntime(Stream archive, ReleaseVerification.Manifest manifest, string directory, string component)
    {
        var required = component switch
        {
            "Ocr" => new[] { "Runtime/Ocr/tesseract.exe", "Runtime/Ocr/tessdata/eng.traineddata" },
            "Capture" => new[] { "Runtime/Capture/ffmpeg.exe" },
            _ => throw new ArgumentOutOfRangeException(nameof(component))
        };
        var root = Path.GetFullPath(directory);
        var runtime = Path.Combine(root, "Runtime");
        var target = Path.Combine(runtime, component);
        ValidateDirectory(root);
        ValidateDirectory(runtime);
        ValidateDirectory(target);
        ReleaseVerification.VerifyArchive(archive, manifest);

        var stage = Directory.CreateDirectory(Path.Combine(runtime, ".download-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Extract(archive, stage, required, "Runtime/" + component + "/");
            var backup = Path.Combine(runtime, component + ".previous-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(target)) Directory.Move(target, backup);
            try { Directory.Move(Path.Combine(stage, "Runtime", component), target); }
            catch
            {
                if (Directory.Exists(backup)) Directory.Move(backup, target);
                throw;
            }
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    private static string[] Extract(Stream archive, string output, string[]? required = null, string? prefix = null)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName;
            if (!Allowed(path) || (prefix != null && !path.StartsWith(prefix, StringComparison.Ordinal)) || !seen.Add(path) || seen.Count > 512)
                throw new InvalidDataException("The update contains a duplicate or unmanaged path: " + path);
            paths.Add(path);
            if (entry.Length <= 0 || entry.Length > 1024L * 1024 * 1024 - total)
                throw new InvalidDataException("The update exceeds its extraction limit.");
            total += entry.Length;
            var target = In(output, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[65536];
            long written = 0;
            int size;
            while ((size = input.Read(buffer)) > 0)
            {
                written += size;
                if (written > entry.Length) throw new InvalidDataException("ZIP entry exceeded its declared length.");
                destination.Write(buffer, 0, size);
            }
            if (written != entry.Length) throw new InvalidDataException("ZIP entry was incomplete.");
            destination.Flush(true);
        }
        if (!(required ?? Managed).All(file => paths.Contains(file, StringComparer.Ordinal)))
            throw new InvalidDataException("The update is missing application files.");
        return paths.ToArray();
    }

    private static bool Allowed(string? path)
    {
        if (path == null) return false;
        if (Managed.Contains(path, StringComparer.Ordinal) || path == "Runtime/Ocr/tessdata/eng.traineddata") return true;
        if (!Regex.IsMatch(path, @"\ARuntime/(?:Ocr|Capture)/(?:tesseract\.exe|ffmpeg\.exe|[A-Za-z0-9_+\-]+(?:\.[A-Za-z0-9_+\-]+)*\.dll|(?:LICENSE|COPYING|NOTICE)[A-Za-z0-9_.-]*)\z", RegexOptions.CultureInvariant))
            return false;
        var leaf = path.Split('/')[2];
        if (leaf.EndsWith('.') || Regex.IsMatch(leaf, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase))
            return false;
        return (leaf != "tesseract.exe" || path == "Runtime/Ocr/tesseract.exe") &&
            (leaf != "ffmpeg.exe" || path == "Runtime/Capture/ffmpeg.exe");
    }

    private static string ValidateFilePath(string directory, string relative)
    {
        var parts = relative.Split('/');
        foreach (var part in parts.SkipLast(1))
        {
            directory = Path.Combine(directory, part);
            ValidateDirectory(directory);
        }
        var target = Path.Combine(directory, parts[^1]);
        RejectLink(target);
        return target;
    }

    private static string In(string directory, string path) => Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths must not be symbolic links or junctions: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void ValidateDirectory(string directory)
    {
        RejectLink(directory);
        if (File.Exists(directory)) throw new IOException("Expected a directory: " + directory);
    }

    private static void CopyDurably(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        input.CopyTo(output);
        output.Flush(true);
    }

    private static void WriteMarker(string target, byte[] bytes)
    {
        var temporary = target + ".tmp";
        RejectLink(temporary);
        File.Delete(temporary);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, target);
    }
}
