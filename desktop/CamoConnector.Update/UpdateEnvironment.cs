#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;

namespace CamoConnector.Update;

public static class UpdateEnvironment
{
    public static string? TrustedKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CamoConnector.Update.PublicKey");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static FileStream Lock(string root) =>
        new(Path.Combine(root, ".camo-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public static void HandOff(string root, string operation)
    {
        var copy = Path.Combine(Path.GetTempPath(), "CamoConnector.Updater-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Path.Combine(root, "CamoConnector.Updater.exe"), copy);
        try
        {
            var launch = new ProcessStartInfo(copy) { UseShellExecute = false, WorkingDirectory = root };
            launch.ArgumentList.Add(root);
            launch.ArgumentList.Add(Environment.ProcessId.ToString());
            launch.ArgumentList.Add(operation);
            using var process = Process.Start(launch) ?? throw new IOException("Could not start the updater.");
        }
        catch { File.Delete(copy); throw; }
    }
}
