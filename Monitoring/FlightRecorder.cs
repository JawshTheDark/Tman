using System.Windows.Media;
using Tman.Core;

namespace Tman.Monitoring;

/// <summary>A process as it was at one recorded moment.</summary>
public sealed record TopProc(int Pid, string Name, string Description, double Cpu, ulong Memory, double Io, double Gpu, ImageSource? Icon)
{
    public string CpuText => Fmt.Pct(Cpu);
    public string MemoryText => Fmt.Mb(Memory);
    public string IoText => Fmt.MbRate(Io);
    public string GpuText => Gpu < 0.05 ? "" : Fmt.Pct(Gpu);
}

/// <summary>One recorded sample: system totals plus the processes that stood out.</summary>
public sealed class FlightFrame
{
    public required DateTime Time { get; init; }
    public float Cpu { get; init; }
    public float Memory { get; init; }
    public float Gpu { get; init; }
    public float Ghz { get; init; }
    public float Watts { get; init; }
    public double DiskBps { get; init; }
    public double NetBps { get; init; }
    public ulong MemoryUsed { get; init; }
    public TopProc[] Top { get; init; } = Array.Empty<TopProc>();
}

public enum FlightEventKind { Started, Exited, HighCpu, HighMemory }

public sealed record FlightEvent(long Seq, DateTime Time, FlightEventKind Kind, string Title, string Detail, ImageSource? Icon, DateTime? End = null)
{
    public string TimeText => Time.ToString("T");
    public string KindText => Kind switch
    {
        FlightEventKind.Started => "Started",
        FlightEventKind.Exited => "Exited",
        FlightEventKind.HighCpu => "High CPU",
        _ => "High memory",
    };
    public bool IsSpike => Kind is FlightEventKind.HighCpu or FlightEventKind.HighMemory;
}

/// <summary>
/// Keeps the last hour of system activity in memory so you can look back at what happened:
/// totals for every sample, the top processes at each moment, process starts/exits, and CPU/memory spikes.
/// Fed on the UI thread from <see cref="SystemMonitor.Updated"/>.
/// </summary>
public sealed class FlightRecorder
{
    public static FlightRecorder Instance { get; } = new();
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    const int MaxEvents = 5000;
    const double CpuSpikePct = 85, MemSpikePct = 90;
    static readonly TimeSpan MinSpike = TimeSpan.FromSeconds(5);

    sealed class Spike
    {
        public DateTime Start;
        public double Peak;
        public int Samples;
        public readonly Dictionary<string, double> ByName = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly List<FlightFrame> _frames = new();
    readonly List<FlightEvent> _events = new();
    Dictionary<(int, long), ProcSample>? _prev;
    Spike? _cpuSpike, _memSpike;
    long _seq;

    public IReadOnlyList<FlightFrame> Frames => _frames;
    public IReadOnlyList<FlightEvent> Events => _events;
    public DateTime? Started { get; private set; }

    public void Start() => SystemMonitor.Instance.Updated += OnUpdated;

    void OnUpdated(Snapshot s)
    {
        Started ??= s.Time;
        var procs = s.Processes.Where(p => p.Pid != 0).ToList();
        var frame = new FlightFrame
        {
            Time = s.Time,
            Cpu = (float)s.Cpu.Total,
            Memory = (float)s.Mem.UsedPct,
            MemoryUsed = s.Mem.Used,
            Gpu = (float)(s.Gpus.Count > 0 ? s.Gpus.Max(g => g.Usage) : 0),
            DiskBps = s.Disks.Sum(d => d.ReadBps + d.WriteBps),
            NetBps = s.Nets.Sum(n => n.SendBps + n.RecvBps),
            Ghz = (float)(s.Cpu.AvgMhz > 0 ? s.Cpu.AvgMhz / 1000 : s.Cpu.SpeedGhz),
            Watts = (float)(s.Power.Package ?? 0),
            Top = PickTop(procs),
        };
        _frames.Add(frame);

        TrackProcesses(s.Time, procs);
        TrackSpike(ref _cpuSpike, s.Cpu.Total >= CpuSpikePct, s.Cpu.Total, s.Time, FlightEventKind.HighCpu,
            procs.OrderByDescending(p => p.Cpu).Take(3).Select(p => (p.Name, p.Cpu)), sum: true);
        TrackSpike(ref _memSpike, s.Mem.UsedPct >= MemSpikePct, s.Mem.UsedPct, s.Time, FlightEventKind.HighMemory,
            procs.OrderByDescending(p => p.PrivateWs).Take(3).Select(p => (p.Name, (double)p.PrivateWs)), sum: false);

        var cutoff = s.Time - Window;
        int drop = 0;
        while (drop < _frames.Count && _frames[drop].Time < cutoff) drop++;
        if (drop > 0) _frames.RemoveRange(0, drop);
        drop = 0;
        while (drop < _events.Count && (_events[drop].Time < cutoff || _events.Count - drop > MaxEvents)) drop++;
        if (drop > 0) _events.RemoveRange(0, drop);
    }

    static TopProc[] PickTop(List<ProcSample> procs)
    {
        var picked = new Dictionary<int, ProcSample>();
        void Take(IEnumerable<ProcSample> ordered, int n, Func<ProcSample, double> value)
        {
            foreach (var p in ordered.Take(n))
                if (value(p) > 0) picked.TryAdd(p.Pid, p);
        }
        Take(procs.OrderByDescending(p => p.Cpu), 6, p => p.Cpu);
        Take(procs.OrderByDescending(p => p.PrivateWs), 4, p => p.PrivateWs);
        Take(procs.OrderByDescending(p => p.IoBps), 3, p => p.IoBps);
        Take(procs.OrderByDescending(p => p.Gpu), 2, p => p.Gpu);
        return picked.Values
            .Select(p => new TopProc(p.Pid, p.Name, p.Details.Description ?? "", p.Cpu, p.PrivateWs, p.IoBps, p.Gpu, p.Details.Icon))
            .ToArray();
    }

    void TrackProcesses(DateTime now, List<ProcSample> procs)
    {
        var cur = new Dictionary<(int, long), ProcSample>(procs.Count);
        foreach (var p in procs) cur[(p.Pid, p.CreateTime)] = p;
        if (_prev != null)
        {
            Dictionary<int, string>? names = null;
            foreach (var (key, p) in cur)
            {
                if (_prev.ContainsKey(key)) continue;
                names ??= procs.GroupBy(x => x.Pid).ToDictionary(g => g.Key, g => g.First().Name);
                var parts = new List<string> { $"PID {p.Pid}" };
                if (names.TryGetValue(p.ParentPid, out var parent)) parts.Add($"from {parent}");
                if (p.Details.CommandLine is { Length: > 0 } cmd) parts.Add(cmd);
                Add(now, FlightEventKind.Started, p.Name, string.Join(" · ", parts), p.Details.Icon);
            }
            foreach (var (key, p) in _prev)
            {
                if (cur.ContainsKey(key)) continue;
                var ran = p.CreateTime > 0 ? now - DateTime.FromFileTime(p.CreateTime) : TimeSpan.Zero;
                Add(now, FlightEventKind.Exited, p.Name,
                    $"PID {p.Pid} · ran {Fmt.Span(ran)} · CPU time {Fmt.Duration(TimeSpan.FromTicks(p.CpuTime))}", p.Details.Icon);
            }
        }
        _prev = cur;
    }

    /// <summary>Opens a spike while the condition holds and logs it once it ends, if it lasted long enough.</summary>
    void TrackSpike(ref Spike? spike, bool active, double value, DateTime now, FlightEventKind kind,
        IEnumerable<(string Name, double Value)> contributors, bool sum)
    {
        if (active)
        {
            spike ??= new Spike { Start = now };
            spike.Peak = Math.Max(spike.Peak, value);
            spike.Samples++;
            foreach (var (name, v) in contributors)
                spike.ByName[name] = sum ? spike.ByName.GetValueOrDefault(name) + v : Math.Max(spike.ByName.GetValueOrDefault(name), v);
            return;
        }
        if (spike == null) return;
        var done = spike;
        spike = null;
        var length = now - done.Start;
        if (length < MinSpike) return;

        var top = done.ByName.OrderByDescending(kv => kv.Value).Take(2).ToList();
        string Culprit((string Name, double Value) c) => kind == FlightEventKind.HighCpu
            ? $"{c.Name} ({c.Value / done.Samples:0}% avg)"
            : $"{c.Name} ({Fmt.Bytes(c.Value)})";
        var who = top.Count == 0 ? "" : " · mostly " + string.Join(", ", top.Select(kv => Culprit((kv.Key, kv.Value))));
        var title = kind == FlightEventKind.HighCpu ? $"CPU at {done.Peak:0}%" : $"Memory at {done.Peak:0}%";
        Add(done.Start, kind, title, $"for {Fmt.Span(length)}{who}", null, now);
    }

    void Add(DateTime time, FlightEventKind kind, string title, string detail, ImageSource? icon, DateTime? end = null) =>
        _events.Add(new FlightEvent(++_seq, time, kind, title, detail, icon, end));

    /// <summary>The recorded frame closest to a moment, or null if nothing is recorded.</summary>
    public FlightFrame? At(DateTime t)
    {
        if (_frames.Count == 0) return null;
        int lo = 0, hi = _frames.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_frames[mid].Time < t) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && (t - _frames[lo - 1].Time) < (_frames[lo].Time - t)) lo--;
        return _frames[lo];
    }
}
