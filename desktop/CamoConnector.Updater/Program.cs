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
        ApplicationConfiguration.Initialize();
        using var window = new Form
        {
            Text = recovery ? "Recovering Camo Connector" : $"Updating Camo Connector to v{expected}",
            ClientSize = new Size(440, 115),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            ControlBox = false
        };
        var status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16), Text = "Waiting for Camo Connector to close…" };
        window.Controls.Add(status);
        var finished = false;
        window.FormClosing += (_, e) => e.Cancel = !finished && e.CloseReason == CloseReason.UserClosing;
        window.Shown += async (_, _) =>
        {
            IProgress<string> progress = new Progress<string>(message => status.Text = message);
            try
            {
                await Task.Run(() => Run(root, parentId, expected, recovery, progress.Report,
                    (message, title) => window.Invoke(() =>
                    {
                        status.Text = title;
                        MessageBox.Show(window, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    })));
            }
            finally
            {
                finished = true;
                window.Close();
            }
        };
        Application.Run(window);
    }

    private static void Run(string root, int parentId, Version? expected, bool recovery, Action<string> report, Action<string, string> showError)
    {
        var restart = false;
        try
        {
            using var parent = FindProcess(parentId);
            if (parent != null && !parent.WaitForExit(20000))
                throw new IOException("Camo Connector did not exit. Close it and try again.");
            using var lease = UpdateEnvironment.Lock(root);
            var transaction = new PortableReplacement(root);
            report("Checking for an interrupted update…");
            transaction.Recover();
            restart = true;
            if (!recovery)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var http = new HttpClient();
                var release = new GitHubRelease(http);
                var key = UpdateEnvironment.TrustedKey() ?? throw new InvalidOperationException("This updater has no release verification key.");
                var version = Assembly.GetExecutingAssembly().GetName().Version!;
                report("Checking the release signature…");
                var manifest = release.CheckAsync(new Version(version.Major, version.Minor, version.Build), key, deadline.Token)
                    .GetAwaiter().GetResult();
                if (manifest == null || new Version(manifest.Version) != expected)
                    throw new IOException("The available release changed. Check again from the app.");
                var staged = Path.Combine(Path.GetTempPath(), "CamoConnector-" + Guid.NewGuid().ToString("N") + ".zip");
                using var archive = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    65536, FileOptions.DeleteOnClose);
                report("Downloading update (0%)…");
                var lastPercent = 0;
                release.DownloadArchiveAsync(manifest, archive, deadline.Token, bytes =>
                {
                    var percent = (int)(bytes * 100 / manifest.Size);
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    report(percent == 100 ? "Verifying downloaded files…" : $"Downloading update ({percent}%)…");
                }).GetAwaiter().GetResult();
                transaction.Apply(archive, manifest, report: report);
            }
        }
        catch (Exception error)
        {
            showError(error.Message, "Camo Connector update failed");
        }
        if (restart && !Directory.Exists(Path.Combine(root, ".camo-update")))
        {
            try
            {
                report("Restarting Camo Connector…");
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
            catch (Exception error) { showError(error.Message, "Could not restart Camo Connector"); }
        }
    }

    private static Process? FindProcess(int id)
    {
        try { return Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
    }
}
