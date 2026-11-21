using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private readonly CheckBox showUsage = new() { Text = "Show app usage in sidebar", AutoSize = true };
    private readonly Label usageLabel = new() { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Dictionary<int, (DateTime Start, TimeSpan Cpu)> previousCpu = [];
    private static readonly uint LogicalProcessorCount = Math.Max(1u, GetActiveProcessorCount(ushort.MaxValue));
    private long lastUsageSample;
    private nint gpuQuery, gpuCounter;
    private bool gpuQueryAttempted;

    private void BindUsage()
    {
        var preference = Path.Combine(root, "Data", "show-usage");
        showUsage.Checked = File.Exists(preference);
        usageLabel.Font = ShellFont("Bahnschrift", 8);
        usageLabel.Height = LogicalToDeviceUnits(86);
        usageLabel.Text = "CPU\n—\nRAM\n—\nGPU\n—";
        usageLabel.Visible = showUsage.Checked;
        showUsage.CheckedChanged += (_, _) =>
        {
            usageLabel.Visible = showUsage.Checked;
            previousCpu.Clear();
            lastUsageSample = 0;
            if (!showUsage.Checked) ReleaseUsage();
            try
            {
                if (showUsage.Checked)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(preference)!);
                    File.WriteAllText(preference, "on");
                }
                else File.Delete(preference);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                LogLine(bridgeMessages, "Could not save sidebar usage preference: " + error.Message);
            }
            RefreshUsage();
        };
    }

    private void RefreshUsage()
    {
        if (!showUsage.Checked) return;
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            if (lastUsageSample != 0)
            {
                previousCpu.Clear();
                lastUsageSample = 0;
                ReleaseUsage();
            }
            return;
        }
        var pids = new HashSet<int> { Environment.ProcessId };
        foreach (var process in new[] { bridge, detector, previewWorker, trophyServer })
        {
            if (process != null) pids.Add(process.Id);
        }
        foreach (var webView in new[] { localTrackerView, trophyView })
        {
            try
            {
                if (webView?.CoreWebView2 is { } view)
                    foreach (var process in view.Environment.GetProcessInfos()) pids.Add(process.ProcessId);
            }
            catch (Exception error) when (error is InvalidOperationException or COMException) { }
        }

        IncludeChildProcesses(pids);
        var now = Stopwatch.GetTimestamp();
        var sampledAt = DateTime.UtcNow;
        var elapsed = lastUsageSample == 0 ? 0 : Stopwatch.GetElapsedTime(lastUsageSample, now).TotalSeconds;
        lastUsageSample = now;
        var samples = new Dictionary<int, (DateTime Start, TimeSpan Cpu)>();
        var intervalStart = sampledAt.AddSeconds(-elapsed);
        var cpu = 0d;
        ulong bytes = 0;
        var ramAvailable = true;
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var start = process.StartTime.ToUniversalTime();
                var time = process.TotalProcessorTime;
                samples[pid] = (start, time);
                if (previousCpu.TryGetValue(pid, out var previous) && previous.Start == start && time >= previous.Cpu)
                    cpu += (time - previous.Cpu).TotalSeconds;
                else if (elapsed > 0 && start >= intervalStart)
                    cpu += time.TotalSeconds;

                var memory = new PrivateMemoryCounters { Size = (uint)Marshal.SizeOf<PrivateMemoryCounters>() };
                if (GetProcessMemoryInfo(process.Handle, ref memory, memory.Size)) bytes += memory.PrivateWorkingSet;
                else ramAvailable = false;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        previousCpu.Clear();
        foreach (var (pid, time) in samples) previousCpu.Add(pid, time);

        var cpuText = elapsed > 0 ? $"{Math.Clamp(cpu / elapsed / LogicalProcessorCount, 0, 1):0.0%}" : "—";
        var ramText = !ramAvailable ? "-" : bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0}G" : $"{bytes / (1024d * 1024):0}M";
        var gpu = ReadGpuPercent(pids);
        var gpuText = gpu is double percent ? $"{percent:0.0}%" : "—";
        usageLabel.Text = $"CPU\n{cpuText}\nRAM\n{ramText}\nGPU\n{gpuText}";
        usageLabel.AccessibleName = $"App CPU {cpuText}, private RAM {ramText}, GPU {gpuText}";
    }

    private static void IncludeChildProcesses(HashSet<int> pids)
    {
        const uint processes = 2;
        var snapshot = CreateToolhelp32Snapshot(processes, 0);
        if (snapshot == -1) return;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            var children = new Dictionary<int, List<int>>();
            for (var found = Process32First(snapshot, ref entry); found; found = Process32Next(snapshot, ref entry))
            {
                if (!children.TryGetValue(entry.Parent, out var siblings))
                    children.Add(entry.Parent, siblings = []);
                siblings.Add(entry.Pid);
            }
            var pending = new Queue<int>(pids);
            while (pending.TryDequeue(out var parent))
                if (children.TryGetValue(parent, out var descendants))
                    foreach (var child in descendants)
                        if (pids.Add(child)) pending.Enqueue(child);
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrivateMemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        public nuint PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref PrivateMemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage;
        public int Pid;
        public nuint Heap;
        public uint Module, Threads;
        public int Parent, Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Executable;
    }

    [DllImport("kernel32.dll")]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private double? ReadGpuPercent(HashSet<int> pids)
    {
        if (!gpuQueryAttempted)
        {
            gpuQueryAttempted = true;
            if (PdhOpenQuery(null, 0, out gpuQuery) != 0 ||
                PdhAddEnglishCounter(gpuQuery, @"\GPU Engine(*)\Utilization Percentage", 0, out gpuCounter) != 0)
                return null;
        }
        if (gpuCounter == 0 || PdhCollectQueryData(gpuQuery) != 0) return null;
        uint size = 0;
        const uint formatDouble = 0x200;
        const uint moreData = 0x800007D2;
        if (PdhGetFormattedCounterArray(gpuCounter, formatDouble, ref size, out _, 0) != moreData || size == 0)
            return null;
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (PdhGetFormattedCounterArray(gpuCounter, formatDouble, ref size, out var count, buffer) != 0) return null;
            var engines = new Dictionary<string, double>(StringComparer.Ordinal);
            var stride = Marshal.SizeOf<GpuReading>();
            for (var i = 0; i < count; i++)
            {
                var reading = Marshal.PtrToStructure<GpuReading>(buffer + i * stride);
                var name = Marshal.PtrToStringUni(reading.Name);
                if (reading.Status > 1 || name == null || !name.StartsWith("pid_", StringComparison.Ordinal)) continue;
                var separator = name.IndexOf('_', 4);
                if (separator < 0 || !int.TryParse(name.AsSpan(4, separator - 4), out var pid) || !pids.Contains(pid)) continue;
                var engine = name[(separator + 1)..];
                engines[engine] = engines.GetValueOrDefault(engine) + reading.Value;
            }
            return Math.Clamp(engines.Values.DefaultIfEmpty().Max(), 0, 100);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void ReleaseUsage()
    {
        if (gpuQuery != 0) PdhCloseQuery(gpuQuery);
        gpuQuery = gpuCounter = 0;
        gpuQueryAttempted = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuReading
    {
        public nint Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, nint items);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
