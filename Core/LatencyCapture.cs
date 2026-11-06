using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tman.Core;

/// <summary>What one driver cost during a capture, in microseconds.</summary>
public sealed class DriverLatency
{
    public required string File { get; init; }
    public string Path { get; init; } = "";
    public string Description { get; init; } = "";
    public long DpcCount { get; set; }
    public long IsrCount { get; set; }
    public double DpcTotal { get; set; }
    public double IsrTotal { get; set; }
    public double DpcMax { get; set; }
    public double IsrMax { get; set; }
    /// <summary>Runs of 1,000 µs or more.</summary>
    public long Spikes { get; set; }

    public long Count => DpcCount + IsrCount;
    public double Total => DpcTotal + IsrTotal;
    public double Average => Count == 0 ? 0 : Total / Count;
    public double Max => Math.Max(DpcMax, IsrMax);
    public string DpcCountText => Fmt.Num(DpcCount);
    public string IsrCountText => Fmt.Num(IsrCount);
    public string TotalText => $"{LatencyVerdict.Us(Total)} µs";
    public string AverageText => $"{LatencyVerdict.Us(Average)} µs";
    public string MaxText => $"{LatencyVerdict.Us(Max)} µs";
    public string SpikesText => Fmt.Num(Spikes);
}

public sealed record LatencyResult
{
    /// <summary>Worst first.</summary>
    public required List<DriverLatency> Drivers { get; init; }
    public required double Seconds { get; init; }
    public required int RequestedSeconds { get; init; }
    public long Events { get; init; }
    /// <summary>Events or buffers Windows dropped because they arrived too fast to keep up.</summary>
    public long Lost { get; init; }
    /// <summary>Windows gave no driver addresses, so nothing could be matched to a file.</summary>
    public bool AddressesHidden { get; init; }
    /// <summary>Each processor's share of all DPC and ISR time, in percent.</summary>
    public double[] CpuShare { get; init; } = Array.Empty<double>();

    public DriverLatency? WorstDpc => Drivers.Where(d => d.DpcCount > 0).MaxBy(d => d.DpcMax);
    public DriverLatency? WorstIsr => Drivers.Where(d => d.IsrCount > 0).MaxBy(d => d.IsrMax);
}

/// <summary>Turns a result into plain words. Brush keys come from the theme.</summary>
public sealed record LatencyVerdict(string Brush, string Headline, string Detail)
{
    /// <summary>Low-latency audio stays clean under this; a small buffer has only a few milliseconds to spare.</summary>
    public const double FineUs = 500;
    /// <summary>Above this, default audio buffers and frame pacing start to suffer too.</summary>
    public const double RiskyUs = 2000;
    public const double SpikeUs = 1000;

    public static string Us(double us) => us >= 100 ? us.ToString("#,0") : us.ToString("0.0");

    public static LatencyVerdict From(LatencyResult r)
    {
        if (r.Events == 0)
            return new("WarnBrush", "No DPC or ISR events arrived",
                "The trace ran, but Windows delivered nothing to measure, so this says nothing about your PC. Try again, and use the PC during the capture.");

        double worst = r.Drivers.Max(d => d.Max);
        string lost = r.Lost > 0 ? $"Windows dropped {Fmt.Num(r.Lost)} events or buffers while tracing, so the real worst case could be higher." : "";
        string hidden = r.AddressesHidden ? "Windows hid driver addresses from Tman, so it can't say which driver this was. Timings are still correct." : "";
        string Sentences(params string[] parts) => string.Join(" ", parts.Where(p => p.Length > 0));

        if (worst < FineUs)
            return new("GoodBrush", "Looks good for audio and gaming",
                Sentences($"The longest any driver held up a processor was {Us(worst)} µs. Under {Us(FineUs)} µs is fine, even for low-latency music work.",
                    "If you still hear crackle, capture again while it's happening.", lost, hidden));

        if (worst < RiskyUs)
            return new("WarnBrush", "Fine for gaming, borderline for low-latency audio",
                Sentences($"The longest delay was {Us(worst)} µs. Games and normal audio cope with this, but music work with small buffers can crackle above {Us(FineUs)} µs.",
                    Culprit(r), lost, hidden));

        return new("BadBrush", "Long enough to cause audio crackle and stutter",
            Sentences($"The longest delay was {Us(worst)} µs. Above about {Us(RiskyUs)} µs, audio can drop out and games can hitch, because nothing else can run on that processor meanwhile.",
                Culprit(r), lost, hidden));
    }

    static string Culprit(LatencyResult r)
    {
        if (r.AddressesHidden) return "";
        // Windows' own routines run on behalf of everything else, so a real driver near the top is the better lead.
        var d = r.Drivers.FirstOrDefault(x => x.Path.Length > 0 && !IsWindowsCore(x.File) && x.Max >= FineUs);
        if (d == null)
            return r.Drivers[0].Path.Length == 0
                ? "Tman couldn't match the worst run to a loaded driver."
                : $"The worst run was in {r.Drivers[0].File}, which is part of Windows itself, so no single driver stands out. Firmware and power-saving delays can look like this.";

        string what = d.Description.Length > 0 ? $"{d.File} ({d.Description})" : d.File;
        string area = Area(d.File) is { } a ? $" It's part of your {a}." : "";
        string spikes = d.Spikes > 0 ? $" It ran for over {Us(SpikeUs)} µs {Fmt.Num(d.Spikes)} {(d.Spikes == 1 ? "time" : "times")}." : "";
        return $"The likely culprit is {what}, with a longest run of {Us(d.Max)} µs.{spikes}{area} Try updating that driver first.";
    }

    static bool IsWindowsCore(string file) => Area(file) == "Windows";

    /// <summary>The part of the PC a driver belongs to, for the drivers that most often turn up here.</summary>
    static string? Area(string file)
    {
        var f = file.ToLowerInvariant();
        return f switch
        {
            "ntoskrnl.exe" or "hal.dll" or "wdf01000.sys" => "Windows",
            "nvlddmkm.sys" or "dxgkrnl.sys" or "dxgmms2.sys" or "watchdog.sys" => "graphics",
            "ndis.sys" or "tcpip.sys" or "netio.sys" or "wfplwfs.sys" or "afd.sys" => "networking",
            "storport.sys" or "stornvme.sys" or "storahci.sys" or "ataport.sys" or "ntfs.sys" or "volmgr.sys" or "disk.sys" or "classpnp.sys" => "storage",
            "usbxhci.sys" or "ucx01000.sys" or "usbport.sys" or "usbhub3.sys" or "usbehci.sys" or "hidclass.sys" or "hidusb.sys" or "dwc3.sys" => "USB",
            "hdaudbus.sys" or "hdaudio.sys" or "portcls.sys" or "ks.sys" or "ksthunk.sys" or "usbaudio.sys" or "usbaudio2.sys" => "audio",
            "acpi.sys" or "intelppm.sys" or "amdppm.sys" or "processr.sys" => "power management",
            _ when f.StartsWith("igdkm") || f.StartsWith("amdkmd") || f.StartsWith("atikm") => "graphics",
            _ when f.StartsWith("rtkvhd") || f.StartsWith("intcaudio") || f.StartsWith("nvhda") => "audio",
            _ when f.StartsWith("netwtw") || f.StartsWith("netwbw") || f.StartsWith("rtwlan") || f.StartsWith("rt640") || f.StartsWith("e1d") || f.StartsWith("mtkwl") => "networking",
            _ when f.StartsWith("iastor") || f.StartsWith("rstor") || f.StartsWith("nvstor") => "storage",
            _ => null,
        };
    }
}

/// <summary>A kernel module and where it starts in memory.</summary>
public readonly record struct KernelModule(ulong Base, string Path);

/// <summary>
/// Times every DPC and ISR for a fixed period through a kernel trace, and adds the runs up per driver. PerfInfo events
/// are written as each routine returns, so its run time is the event's timestamp minus the InitialTime in its payload.
/// </summary>
internal sealed unsafe class LatencyCapture
{
    const uint EVENT_TRACE_FLAG_DPC = 0x20, EVENT_TRACE_FLAG_INTERRUPT = 0x40;
    static readonly Guid PerfInfo = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

    // Opcodes from the DPC, ISR and ISR_MSI classes in root\wmi. All start with InitialTime (8 bytes), then Routine (a pointer).
    const byte ThreadedDpc = 66, Dpc = 68, TimerDpc = 69, IoTimer = 70, Isr = 67, IsrPassive = 95, IsrMsi = 50;
    const int MinPayload = 16;

    sealed class Routine
    {
        public long DpcCount, DpcTicks, DpcMax, IsrCount, IsrTicks, IsrMax, Spikes;
    }

    readonly Dictionary<ulong, Routine> _routines = new();
    readonly long[] _cpuTicks = new long[256];
    readonly long _longest = Stopwatch.Frequency * 10;
    readonly long _spike = (long)(Stopwatch.Frequency * LatencyVerdict.SpikeUs / 1e6);
    long _events;

    /// <summary>Runs on the trace's own thread, once per event; nothing else touches this data until the trace stops.</summary>
    internal void Add(EtwEvent* e)
    {
        bool isr;
        switch (e->Opcode)
        {
            case ThreadedDpc or Dpc or TimerDpc or IoTimer: isr = false; break;
            case Isr or IsrPassive or IsrMsi: isr = true; break;
            default: return;
        }
        if (e->UserDataLength < MinPayload || e->ProviderId != PerfInfo) return;

        long started = Unsafe.ReadUnaligned<long>(e->UserData);
        ulong address = Unsafe.ReadUnaligned<ulong>(e->UserData + 8);
        long ticks = e->TimeStamp - started;
        if (ticks < 0 || ticks > _longest) return;

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_routines, address, out _);
        var r = slot ??= new Routine();
        if (isr)
        {
            r.IsrCount++;
            r.IsrTicks += ticks;
            if (ticks > r.IsrMax) r.IsrMax = ticks;
        }
        else
        {
            r.DpcCount++;
            r.DpcTicks += ticks;
            if (ticks > r.DpcMax) r.DpcMax = ticks;
        }
        if (ticks >= _spike) r.Spikes++;
        _cpuTicks[e->Processor] += ticks;
        _events++;
    }

    /// <summary>Runs a capture. Blocks, so call it off the UI thread. Throws <see cref="EtwFailure"/> with a plain message.</summary>
    public static LatencyResult Run(int seconds, CancellationToken cancel)
    {
        var capture = new LatencyCapture();
        var clock = Stopwatch.StartNew();
        var trace = EtwTrace.StartKernel("Latency", EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT, capture.Add);
        uint lost;
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds && trace.Running && !cancel.IsCancellationRequested)
                cancel.WaitHandle.WaitOne(100);
        }
        finally
        {
            lost = trace.Stop();
        }
        double elapsed = clock.Elapsed.TotalSeconds;
        if (!trace.Finished) throw new EtwFailure("The trace didn't stop in time, so its numbers can't be trusted. Try again.");
        if (capture._events == 0 && trace.PumpError is { } error) throw new EtwFailure(error);
        var modules = KernelModules();
        return capture.Build(modules, elapsed, seconds, lost);
    }

    internal LatencyResult Build(List<KernelModule> modules, double seconds, int requested, uint lost)
    {
        double toUs = 1e6 / Stopwatch.Frequency;
        bool hidden = modules.Count == 0;
        var bases = modules.Select(m => m.Base).ToArray();
        var perDriver = new Dictionary<string, (string Path, Routine Sum, long DpcMax, long IsrMax)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, r) in _routines)
        {
            int at = Array.BinarySearch(bases, address);
            if (at < 0) at = ~at - 1;
            string path = at >= 0 ? modules[at].Path : "";
            string key = path.Length > 0 ? path : "Unknown driver";
            if (!perDriver.TryGetValue(key, out var d)) d = (path, new Routine(), 0, 0);
            d.Sum.DpcCount += r.DpcCount;
            d.Sum.DpcTicks += r.DpcTicks;
            d.Sum.IsrCount += r.IsrCount;
            d.Sum.IsrTicks += r.IsrTicks;
            d.Sum.Spikes += r.Spikes;
            d.DpcMax = Math.Max(d.DpcMax, r.DpcMax);
            d.IsrMax = Math.Max(d.IsrMax, r.IsrMax);
            perDriver[key] = d;
        }

        var drivers = perDriver.Select(kv => new DriverLatency
        {
            File = kv.Value.Path.Length > 0 ? Path.GetFileName(kv.Value.Path) : kv.Key,
            Path = kv.Value.Path,
            Description = DescriptionOf(kv.Value.Path),
            DpcCount = kv.Value.Sum.DpcCount,
            IsrCount = kv.Value.Sum.IsrCount,
            DpcTotal = kv.Value.Sum.DpcTicks * toUs,
            IsrTotal = kv.Value.Sum.IsrTicks * toUs,
            DpcMax = kv.Value.DpcMax * toUs,
            IsrMax = kv.Value.IsrMax * toUs,
            Spikes = kv.Value.Sum.Spikes,
        }).OrderByDescending(d => d.Max).ToList();

        long allTicks = _cpuTicks.Sum();
        int cpus = Math.Max(Environment.ProcessorCount, Array.FindLastIndex(_cpuTicks, t => t > 0) + 1);
        return new LatencyResult
        {
            Drivers = drivers,
            Seconds = seconds,
            RequestedSeconds = requested,
            Events = _events,
            Lost = lost,
            AddressesHidden = hidden,
            CpuShare = allTicks == 0 ? Array.Empty<double>() : _cpuTicks.Take(cpus).Select(t => t * 100.0 / allTicks).ToArray(),
        };
    }

    static string DescriptionOf(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return "";
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
        catch { return ""; }
    }

    [DllImport("kernel32.dll", EntryPoint = "K32EnumDeviceDrivers", SetLastError = true)]
    static extern bool EnumDeviceDrivers(ulong* imageBases, uint bytes, out uint needed);

    [DllImport("kernel32.dll", EntryPoint = "K32GetDeviceDriverFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetDeviceDriverFileNameW(ulong imageBase, char* name, uint size);

    /// <summary>
    /// Loaded drivers sorted by address, or an empty list when Windows hides their addresses. Since Windows 11 24H2 it
    /// returns zeros unless SeDebugPrivilege is switched on, so admin alone isn't enough.
    /// </summary>
    internal static List<KernelModule> KernelModules()
    {
        TokenPrivileges.Enable(TokenPrivileges.Debug);
        var bases = new ulong[8192];
        uint needed;
        fixed (ulong* p = bases)
            if (!EnumDeviceDrivers(p, (uint)(bases.Length * sizeof(ulong)), out needed)) return new();

        var list = new List<KernelModule>();
        char* name = stackalloc char[1024];
        foreach (var address in bases.Take((int)Math.Min(needed / sizeof(ulong), (uint)bases.Length)))
        {
            if (address == 0) continue;
            uint length = GetDeviceDriverFileNameW(address, name, 1024);
            list.Add(new KernelModule(address, length > 0 ? DosPath(new string(name, 0, (int)length)) : ""));
        }
        list.Sort((a, b) => a.Base.CompareTo(b.Base));
        return list;
    }

    internal static string DosPath(string path)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, path[12..]);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) return path[4..];
        if (path.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, path[9..]);
        return path;
    }
}
