using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tman.Core;

public sealed record HandleHolder(int Pid, string Name, int ProcessHandles, int AllHandles);
public sealed record ThirdPartyDriver(string File, string Company, string Description, string Why);
public sealed record ZombieState(long KernelProcesses, int Running, long ProcessHandles, List<HandleHolder> Holders)
{
    public long Zombies => Math.Max(0, KernelProcesses - Running);
}

/// <param name="Started">Processes this test started, counting the console each one gets.</param>
/// <param name="Stayed">How many more process objects there were afterward, allowing for what else was going on.</param>
public sealed record ExitTestResult(int Started, long Stayed)
{
    public bool Leaking => Stayed >= Started / 2;
}

/// <summary>
/// Finds zombie processes: ones that have exited but whose kernel objects are still in memory, because something still
/// refers to them. Each can hold a few hundred KB, so a steady stream adds up to gigabytes that belong to no app. A handle
/// is one way to hold on, and shows up here by owner. A driver's private reference doesn't show up anywhere, so if handles
/// can't explain the count, a driver is the likely cause.
/// </summary>
public static unsafe class ZombieCheck
{
    const int SystemExtendedHandleInformation = 64, MostHolders = 12;
    const int StartingBuffer = 16 * 1024 * 1024, LargestBuffer = 512 * 1024 * 1024;

    /// <summary>Tags in the kernel's process and thread allocations.</summary>
    public const string ProcessTag = "Proc";

    [StructLayout(LayoutKind.Sequential)]
    struct HandleEntry
    {
        public nuint Object, Pid, Handle;
        public uint Access;
        public ushort Trace, Type;
        public uint Attributes, Reserved;
    }

    /// <summary>Process objects in the kernel, or null if Windows won't say.</summary>
    public static long? KernelProcesses()
    {
        var buffer = new byte[256 * 1024];
        return MemoryMapReader.ReadAllPoolTags(ref buffer)?.FirstOrDefault(t => t.Tag == ProcessTag)?.Blocks;
    }

    /// <summary>The current picture, or null if the kernel's count isn't available.</summary>
    public static ZombieState? Read()
    {
        if (KernelProcesses() is not { } kernel) return null;
        var (total, holders) = CountProcessHandles();
        return new ZombieState(kernel, Process.GetProcesses().Length, total, holders);
    }

    /// <summary>Counts handles to processes, by owner. The type number varies by Windows build, so it's read from a handle of our own.</summary>
    static (long Total, List<HandleHolder> Holders) CountProcessHandles()
    {
        using var self = Process.GetCurrentProcess();
        long mine = self.Handle.ToInt64();
        int size = StartingBuffer;
        while (size <= LargestBuffer)
        {
            var buffer = new byte[size];
            fixed (byte* b = buffer)
            {
                int status = Native.NtQuerySystemInformation(SystemExtendedHandleInformation, b, size, out _);
                if (status == Native.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size *= 2;
                    continue;
                }
                if (status != 0) return (0, new());

                long count = (long)*(nuint*)b;
                var entries = (HandleEntry*)(b + 2 * sizeof(nuint));
                ushort type = 0;
                for (long i = 0; i < count; i++)
                {
                    if ((long)entries[i].Pid == Environment.ProcessId && (long)entries[i].Handle == mine)
                    {
                        type = entries[i].Type;
                        break;
                    }
                }
                var all = new Dictionary<int, int>();
                var process = new Dictionary<int, int>();
                long total = 0;
                for (long i = 0; i < count; i++)
                {
                    int pid = (int)entries[i].Pid;
                    all[pid] = all.GetValueOrDefault(pid) + 1;
                    if (entries[i].Type != type) continue;
                    process[pid] = process.GetValueOrDefault(pid) + 1;
                    total++;
                }
                var holders = process.OrderByDescending(e => e.Value).Take(MostHolders)
                    .Select(e => new HandleHolder(e.Key, NameOf(e.Key), e.Value, all[e.Key])).ToList();
                return (total, holders);
            }
        }
        return (0, new());
    }

    static string NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "(exited)";
        }
    }

    /// <summary>
    /// Starts short processes that do nothing, then counts whether they left the kernel. Compares with an equal stretch of
    /// doing nothing first, because other programs start and stop all the time. Blocks for several seconds.
    /// </summary>
    public static ExitTestResult? TestExits(int runs = 25)
    {
        var settle = TimeSpan.FromSeconds(3);
        if (KernelProcesses() is not { } first) return null;
        Thread.Sleep(settle);
        if (KernelProcesses() is not { } before) return null;
        long background = before - first;

        var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        for (int i = 0; i < runs; i++)
        {
            using var process = Process.Start(new ProcessStartInfo(shell, "/c exit") { UseShellExecute = false, CreateNoWindow = true });
            process?.WaitForExit(5000);
        }
        Thread.Sleep(settle);
        if (KernelProcesses() is not { } after) return null;
        // Each run is two processes, because a hidden console host starts alongside it.
        return new ExitTestResult(runs * 2, Math.Clamp(after - before - Math.Max(0, background), 0, runs * 2));
    }

    /// <summary>Loaded drivers from outside Microsoft, the likeliest to be holding on to processes, with any known reason to look at one first.</summary>
    public static List<ThirdPartyDriver> ThirdPartyDrivers()
    {
        var drivers = new List<ThirdPartyDriver>();
        foreach (var path in Wmi.Query("SELECT PathName FROM Win32_SystemDriver WHERE State='Running'").Select(o => PoolTagFinder.Normalize(o.Str("PathName"))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var info = FileVersionInfo.GetVersionInfo(path);
            if ((info.CompanyName ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.GetFileName(path);
            drivers.Add(new ThirdPartyDriver(file, (info.CompanyName ?? "").Trim(), (info.FileDescription ?? "").Trim(), Lead(file)));
        }
        return drivers.OrderByDescending(d => d.Why.Length > 0).ThenBy(d => d.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Kinds of driver with a reason to watch processes. A note here is a place to start, not a finding.</summary>
    static string Lead(string file)
    {
        var name = file.ToLowerInvariant();
        if (name.Contains("split-tunnel") || name.Contains("splittunnel")) return "Tracks every process start and exit";
        if (name.Contains("vpn") || name.Contains("wg") || name.Contains("tap")) return "Network tools often watch processes";
        if (name.Contains("vgk") || name.Contains("easyanticheat") || name.Contains("battleye") || name.Contains("bedaisy")) return "Anti-cheat drivers watch every process";
        if (name.Contains("winfsp")) return "Runs mounted drives such as sshfs";
        return "";
    }
}
