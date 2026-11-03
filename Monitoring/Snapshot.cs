using System.Windows.Media;

namespace Tman.Monitoring;

public sealed class ProcSample
{
    public int Pid;
    public int ParentPid;
    public long CreateTime;
    public string Name = "";
    public int Threads;
    public int Handles;
    public int SessionId;
    public int BasePriority;
    public double Cpu;
    public ulong PrivateWs;
    public ulong WorkingSet;
    public ulong Commit;
    public double IoBps;
    public double Gpu;
    public bool Suspended;
    /// <summary>Lifetime user + kernel CPU time, in 100 ns units.</summary>
    public long CpuTime;
    /// <summary>Lifetime read + write transfer, in bytes.</summary>
    public long IoBytes;
    public ProcessDetails Details = ProcessDetails.Empty;
}

/// <summary>Per-process facts that don't change over its lifetime. Immutable once built.</summary>
public sealed class ProcessDetails
{
    public static readonly ProcessDetails Empty = new();
    public string? Path { get; init; }
    public string? Description { get; init; }
    public string? Company { get; init; }
    public string? User { get; init; }
    public string? CommandLine { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed record CpuStat(double Total, double[] Cores, double SpeedGhz)
{
    /// <summary>Measured clock per logical processor in MHz, same order as <see cref="Cores"/>; empty if Windows doesn't report it.</summary>
    public double[] CoreMhz { get; init; } = Array.Empty<double>();
    public bool[] Parked { get; init; } = Array.Empty<bool>();
    /// <summary>Measured clock averaged over all logical processors, in MHz (0 if unknown).</summary>
    public double AvgMhz { get; init; }
    /// <summary>% Performance Limit: 100 means nothing (heat, power, firmware) is capping the clock.</summary>
    public double PerfLimit { get; init; } = 100;
    public uint LimitFlags { get; init; }
}

public sealed record PowerChannel(string Name, double Watts);

/// <summary>Energy Meter readings (RAPL on most desktops). Package is null when the PC has no meter Windows can read.</summary>
public sealed record PowerStat(double? Package, double? Cores, List<PowerChannel> Channels);

public sealed record MemStat(ulong Total, ulong Available, ulong CommitTotal, ulong CommitLimit,
    ulong Cached, ulong Paged, ulong NonPaged)
{
    public ulong Used => Total > Available ? Total - Available : 0;
    public double UsedPct => Total == 0 ? 0 : Used * 100.0 / Total;
}

public sealed record DiskStat(int Index, string Letters, double Active, double ReadBps, double WriteBps, double ResponseMs);

public sealed record NetStat(string Id, string Name, string Description, string Type,
    double SendBps, double RecvBps, long Speed, string IPv4, string IPv6);

public sealed record GpuStat(string Luid, string Name, double Usage, Dictionary<string, double> Engines,
    ulong DedUsed, ulong DedTotal, ulong SharedUsed, ulong SharedTotal)
{
    /// <summary>Temperature, fan, power and clocks when the driver exposes them.</summary>
    public GpuReading? Sensors { get; init; }
}

public sealed class Snapshot
{
    public DateTime Time;
    public TimeSpan Uptime;
    public List<ProcSample> Processes = new();
    public CpuStat Cpu = new(0, Array.Empty<double>(), 0);
    public MemStat Mem = new(0, 0, 0, 0, 0, 0, 0);
    public List<DiskStat> Disks = new();
    public List<NetStat> Nets = new();
    public List<GpuStat> Gpus = new();
    public PowerStat Power = new(null, null, new());
    public int ProcessCount;
    public int ThreadCount;
    public int HandleCount;
}
