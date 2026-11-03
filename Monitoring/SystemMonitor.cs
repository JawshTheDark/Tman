using System.Windows.Threading;

namespace Tman.Monitoring;

/// <summary>
/// Owns the sampler thread and the rolling histories. <see cref="Updated"/> always fires on the UI thread,
/// and histories are only mutated there, so pages can read them without locking.
/// </summary>
public sealed class SystemMonitor
{
    public static SystemMonitor Instance { get; } = new();
    public const int HistoryLength = 60;

    readonly AutoResetEvent _wake = new(false);
    readonly Dictionary<string, History> _series = new();
    readonly Dictionary<string, HashSet<string>> _gpuEngineTypes = new();
    Dispatcher? _ui;
    volatile int _intervalMs = 1000;
    volatile bool _refreshRequested;

    public Snapshot? Latest { get; private set; }
    public event Action<Snapshot>? Updated;

    public History Cpu { get; } = new(HistoryLength);
    public History Memory { get; } = new(HistoryLength);
    public List<History> Cores { get; } = new();

    /// <summary>Sample interval in ms; 0 pauses updates.</summary>
    public int IntervalMs
    {
        get => _intervalMs;
        set { _intervalMs = value; _wake.Set(); }
    }

    public History Series(string key)
    {
        if (!_series.TryGetValue(key, out var h)) _series[key] = h = new History(HistoryLength);
        return h;
    }

    public IReadOnlyCollection<string> GpuEngineTypes(string luid) =>
        _gpuEngineTypes.TryGetValue(luid, out var s) ? s : (IReadOnlyCollection<string>)Array.Empty<string>();

    public void Start(Dispatcher ui)
    {
        _ui = ui;
        new Thread(Loop) { IsBackground = true, Name = "Tman sampler" }.Start();
    }

    public void RefreshNow()
    {
        _refreshRequested = true;
        _wake.Set();
    }

    void Loop()
    {
        Sampler sampler;
        try { sampler = new Sampler(); }
        catch { return; }

        _wake.WaitOne(400);
        while (true)
        {
            if (_intervalMs > 0 || _refreshRequested)
            {
                _refreshRequested = false;
                Snapshot snap;
                try { snap = sampler.Sample(); }
                catch
                {
                    _wake.WaitOne(1000);
                    continue;
                }
                try { _ui!.Invoke(() => Publish(snap)); }
                catch
                {
                    if (_ui!.HasShutdownStarted) return;
                }
            }
            _wake.WaitOne(_intervalMs > 0 ? _intervalMs : Timeout.Infinite);
        }
    }

    void Publish(Snapshot s)
    {
        Latest = s;
        Cpu.Add(s.Cpu.Total);
        while (Cores.Count < s.Cpu.Cores.Length) Cores.Add(new History(HistoryLength));
        for (int i = 0; i < s.Cpu.Cores.Length; i++) Cores[i].Add(s.Cpu.Cores[i]);
        Memory.Add(s.Mem.UsedPct);
        Series("cpu.mhz").Add(s.Cpu.AvgMhz);
        Series("cpu.mhz.max").Add(s.Cpu.CoreMhz.DefaultIfEmpty().Max());
        if (s.Power.Package is { } pkg) Series("power.pkg").Add(pkg);
        if (s.Power.Cores is { } cores) Series("power.cores").Add(cores);

        foreach (var d in s.Disks)
        {
            Series($"disk{d.Index}").Add(d.Active);
            Series($"disk{d.Index}.r").Add(d.ReadBps);
            Series($"disk{d.Index}.w").Add(d.WriteBps);
        }

        double send = 0, recv = 0;
        foreach (var n in s.Nets)
        {
            Series($"net{n.Id}.s").Add(n.SendBps);
            Series($"net{n.Id}.r").Add(n.RecvBps);
            send += n.SendBps;
            recv += n.RecvBps;
        }
        Series("net.s").Add(send);
        Series("net.r").Add(recv);

        foreach (var g in s.Gpus)
        {
            Series($"gpu{g.Luid}").Add(g.Usage);
            Series($"gpu{g.Luid}.ded").Add(g.DedTotal > 0 ? g.DedUsed * 100.0 / g.DedTotal : 0);
            Series($"gpu{g.Luid}.shared").Add(g.SharedTotal > 0 ? g.SharedUsed * 100.0 / g.SharedTotal : 0);
            if (g.Sensors?.TempC is { } temp) Series($"gpu{g.Luid}.temp").Add(temp);
            if (g.Sensors?.PowerW is { } watts) Series($"gpu{g.Luid}.power").Add(watts);
            else if (g.Sensors?.PowerPct is { } pct) Series($"gpu{g.Luid}.powerpct").Add(pct);
            if (!_gpuEngineTypes.TryGetValue(g.Luid, out var types)) _gpuEngineTypes[g.Luid] = types = new HashSet<string>();
            foreach (var t in g.Engines.Keys) types.Add(t);
            foreach (var t in types) Series($"gpu{g.Luid}.e.{t}").Add(g.Engines.GetValueOrDefault(t));
        }

        Updated?.Invoke(s);
    }
}
