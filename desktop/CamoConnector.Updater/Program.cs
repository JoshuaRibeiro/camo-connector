using System.Diagnostics;
using System.Reflection;
using CamoConnector.Update;

namespace CamoConnector.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out var parentId)) return;
        var recovery = args[2] == "recover";
        if (!Version.TryParse(args[2], out var expected) && !recovery) return;
        var root = Path.GetFullPath(args[0]);
        var restart = false;
        try
        {
            using var parent = FindProcess(parentId);
            if (parent != null && !parent.WaitForExit(20000))
                throw new IOException("Camo Connector did not exit. Close it and try again.");
            using var lease = UpdateEnvironment.Lock(root);
            var transaction = new PortableReplacement(root);
            transaction.Recover();
            restart = true;
            if (!recovery)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var http = new HttpClient();
                var release = new GitHubRelease(http);
                var key = UpdateEnvironment.TrustedKey() ?? throw new InvalidOperationException("This updater has no release verification key.");
                var version = Assembly.GetExecutingAssembly().GetName().Version!;
                var manifest = release.CheckAsync(new Version(version.Major, version.Minor, version.Build), key, deadline.Token)
                    .GetAwaiter().GetResult();
                if (manifest == null || new Version(manifest.Version) != expected)
                    throw new IOException("The available release changed. Check again from the app.");
                var staged = Path.Combine(Path.GetTempPath(), "CamoConnector-" + Guid.NewGuid().ToString("N") + ".zip");
                using var archive = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    65536, FileOptions.DeleteOnClose);
                release.DownloadArchiveAsync(manifest, archive, deadline.Token).GetAwaiter().GetResult();
                transaction.Apply(archive, manifest);
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Camo Connector update", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        if (restart && !Directory.Exists(Path.Combine(root, ".camo-update")))
        {
            try
            {
                var launch = new ProcessStartInfo(Path.Combine(root, "CamoConnector.exe")) { WorkingDirectory = root };
                var copy = Environment.ProcessPath;
                if (copy != null && Path.GetDirectoryName(copy) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                    Path.GetFileName(copy).StartsWith("CamoConnector.Updater-", StringComparison.Ordinal))
                {
                    launch.ArgumentList.Add("--cleanup-updater");
                    launch.ArgumentList.Add(copy);
                    launch.ArgumentList.Add(Environment.ProcessId.ToString());
                }
                Process.Start(launch);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "Could not restart Camo Connector"); }
        }
    }

    private static Process? FindProcess(int id)
    {
        try { return Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
    }
}
