using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Tman.Controls;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public sealed class StatItem : ObservableObject
{
    string _key = "", _value = "";
    public string Key { get => _key; set => Set(ref _key, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed class GraphSpec
{
    public required string Label { get; init; }
    public required History Data { get; init; }
    public History? Data2 { get; init; }
    public required Brush Brush { get; init; }
    public double Max { get; init; } = 100;
    public GraphScale Scale { get; init; } = GraphScale.Percent;
    public double Height { get; init; } = 72;
    public bool ShowScaleLabel => Max <= 0;
    /// <summary>Label for the top of the scale; defaults to 100% for percentage graphs.</summary>
    public string? MaxText { get; init; }
    public string MaxLabel => MaxText ?? (Max > 0 ? "100%" : "");
}

/// <summary>One entry in the Performance device list, and the view-model for its detail panel.</summary>
public abstract class PerfDevice : ObservableObject
{
    protected static SystemMonitor Monitor => SystemMonitor.Instance;

    protected PerfDevice(string key, string brushKey)
    {
        Key = key;
        Brush = (Brush)Application.Current.FindResource(brushKey);
    }

    public string Key { get; }
    public Brush Brush { get; }

    string _title = "", _model = "", _subtitle = "", _subtitle2 = "";
    public string Title { get => _title; protected set => Set(ref _title, value); }
    public string Model { get => _model; protected set => Set(ref _model, value); }
    public string Subtitle { get => _subtitle; protected set => Set(ref _subtitle, value); }
    public string Subtitle2 { get => _subtitle2; protected set => Set(ref _subtitle2, value); }

    public History? Data { get; protected init; }
    public History? Data2 { get; protected init; }
    public double Max { get; protected init; } = 100;
    public GraphScale Scale { get; protected init; } = GraphScale.Percent;
    public string GraphLabel { get; protected init; } = "% Utilization over 60 seconds";
    public bool ShowScaleLabel => Max <= 0;
    public string MaxLabel => Max > 0 ? "100%" : "";

    public ObservableCollection<StatItem> Stats { get; } = new();
    public ObservableCollection<GraphSpec> Extras { get; } = new();

    int _extraColumns = 1;
    public int ExtraColumns { get => _extraColumns; protected set => Set(ref _extraColumns, value); }

    List<KeyValuePair<string, string>> _info = new();
    public List<KeyValuePair<string, string>> Info { get => _info; protected set => Set(ref _info, value); }

    // CPU only: per-core view
    public virtual bool HasCores => false;
    bool _showCores;
    public bool ShowCores
    {
        get => _showCores;
        set { if (Set(ref _showCores, value)) Raise(nameof(ShowOverall)); }
    }
    public bool ShowOverall
    {
        get => !_showCores;
        set => ShowCores = !value;
    }

    public abstract void Update(Snapshot s);

    protected void Stat(int index, string key, string value)
    {
        while (Stats.Count <= index) Stats.Add(new StatItem());
        Stats[index].Key = key;
        Stats[index].Value = value;
    }

    protected static List<KeyValuePair<string, string>> Rows(params (string Key, string Value)[] rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).Select(r => new KeyValuePair<string, string>(r.Key, r.Value)).ToList();
}

public sealed class CpuDevice : PerfDevice
{
    public CpuDevice() : base("cpu", "CpuBrush")
    {
        Title = "CPU";
        Data = Monitor.Cpu;
        ShowCores = false;
    }

    public override bool HasCores => true;

    public List<History> Cores => Monitor.Cores;

    int _coreColumns = 4;
    public int CoreColumns { get => _coreColumns; private set => Set(ref _coreColumns, value); }

    int _coreCount;

    public override void Update(Snapshot s)
    {
        var info = StaticInfo.Current;
        Model = info.CpuName;
        var speed = s.Cpu.SpeedGhz > 0 ? $"{s.Cpu.SpeedGhz:0.00} GHz" : "";
        Subtitle = $"{s.Cpu.Total:0}%  {speed}";

        if (Monitor.Cores.Count != _coreCount)
        {
            _coreCount = Monitor.Cores.Count;
            CoreColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_coreCount * 1.6)));
            Raise(nameof(Cores));
        }

        Stat(0, "Utilization", $"{s.Cpu.Total:0}%");
        Stat(1, "Speed", speed == "" ? "—" : speed);
        Stat(2, "Processes", Fmt.Num(s.ProcessCount));
        Stat(3, "Threads", Fmt.Num(s.ThreadCount));
        Stat(4, "Handles", Fmt.Num(s.HandleCount));
        Stat(5, "Up time", Fmt.Uptime(s.Uptime));

        if (Info.Count == 0 && StaticInfo.IsLoaded)
            Info = Rows(
                ("Base speed", info.BaseMhz > 0 ? Fmt.Mhz(info.BaseMhz) : ""),
                ("Sockets", info.Sockets.ToString()),
                ("Cores", info.Cores > 0 ? info.Cores.ToString() : ""),
                ("Logical processors", info.Logical.ToString()),
                ("Virtualization", info.Virtualization),
                ("L1 cache", info.L1),
                ("L2 cache", info.L2),
                ("L3 cache", info.L3));
    }
}

public sealed class MemoryDevice : PerfDevice
{
    public MemoryDevice() : base("mem", "MemBrush")
    {
        Title = "Memory";
        Data = Monitor.Memory;
        GraphLabel = "Memory usage";
    }

    public override void Update(Snapshot s)
    {
        var m = s.Mem;
        var info = StaticInfo.Current;
        Model = Fmt.Bytes(info.InstalledMemory > 0 ? info.InstalledMemory : m.Total) + (info.MemorySpeed != "" ? $"  ·  {info.MemorySpeed}" : "");
        Subtitle = $"{Fmt.Bytes(m.Used)} / {Fmt.Bytes(m.Total)} ({m.UsedPct:0}%)";

        Stat(0, "In use", Fmt.Bytes(m.Used));
        Stat(1, "Available", Fmt.Bytes(m.Available));
        Stat(2, "Committed", $"{Fmt.Bytes(m.CommitTotal)} / {Fmt.Bytes(m.CommitLimit)}");
        Stat(3, "Cached", Fmt.Bytes(m.Cached));
        Stat(4, "Paged pool", Fmt.Bytes(m.Paged));
        Stat(5, "Non-paged pool", Fmt.Bytes(m.NonPaged));

        if (Info.Count == 0 && StaticInfo.IsLoaded)
            Info = Rows(
                ("Speed", info.MemorySpeed),
                ("Slots used", info.Slots),
                ("Form factor", info.FormFactor),
                ("Hardware reserved", info.InstalledMemory > m.Total ? Fmt.Bytes(info.InstalledMemory - m.Total) : ""));
    }
}

public sealed class DiskDevice : PerfDevice
{
    readonly int _index;

    public DiskDevice(int index) : base($"disk{index}", "DiskBrush")
    {
        _index = index;
        Data = Monitor.Series($"disk{index}");
        GraphLabel = "Active time";
        Extras.Add(new GraphSpec
        {
            Label = "Disk transfer rate (solid = read, dashed = write)",
            Data = Monitor.Series($"disk{index}.r"),
            Data2 = Monitor.Series($"disk{index}.w"),
            Brush = Brush,
            Max = 0,
            Scale = GraphScale.Bytes,
            Height = 96,
        });
    }

    public override void Update(Snapshot s)
    {
        var d = s.Disks.FirstOrDefault(x => x.Index == _index);
        if (d == null) return;
        StaticInfo.Current.Disks.TryGetValue(_index, out var info);
        Title = d.Letters.Length > 0 ? $"Disk {_index} ({d.Letters})" : $"Disk {_index}";
        Model = info?.Model ?? "";
        Subtitle = info?.Type ?? "";
        Subtitle2 = $"{d.Active:0}%";

        Stat(0, "Active time", $"{d.Active:0}%");
        Stat(1, "Average response time", $"{d.ResponseMs:0.0} ms");
        Stat(2, "Read speed", Fmt.Rate(d.ReadBps));
        Stat(3, "Write speed", Fmt.Rate(d.WriteBps));

        if (Info.Count == 0 && info != null)
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows)[..2];
            Info = Rows(
                ("Capacity", info.Size > 0 ? Fmt.Bytes(info.Size) : ""),
                ("Type", info.Type),
                ("System disk", d.Letters.Contains(sys, StringComparison.OrdinalIgnoreCase) ? "Yes" : "No"),
                ("Model", info.Model));
        }
    }
}

public sealed class NetDevice : PerfDevice
{
    readonly string _id;

    public NetDevice(string id) : base("net" + id, "NetBrush")
    {
        _id = id;
        Data = Monitor.Series($"net{id}.r");
        Data2 = Monitor.Series($"net{id}.s");
        Max = 0;
        Scale = GraphScale.Bits;
        GraphLabel = "Throughput (solid = receive, dashed = send)";
    }

    public override void Update(Snapshot s)
    {
        var n = s.Nets.FirstOrDefault(x => x.Id == _id);
        if (n == null) return;
        Title = n.Name;
        Model = n.Description;
        Subtitle = n.Type;
        Subtitle2 = $"S: {Fmt.Bits(n.SendBps)}  R: {Fmt.Bits(n.RecvBps)}";

        Stat(0, "Send", Fmt.Bits(n.SendBps));
        Stat(1, "Receive", Fmt.Bits(n.RecvBps));

        Info = Rows(
            ("Adapter name", n.Description),
            ("Connection type", n.Type),
            ("Link speed", n.Speed > 0 ? Fmt.Bits(n.Speed / 8.0) : ""),
            ("IPv4 address", n.IPv4),
            ("IPv6 address", n.IPv6));
    }
}

public sealed class GpuDevice : PerfDevice
{
    static readonly string[] PreferredEngines = { "3D", "Copy", "VideoDecode", "VideoEncode", "Compute_0", "Compute_1", "VideoProcessing", "Graphics_1" };

    readonly string _luid;
    readonly int _number;
    int _engineCount = -1;

    public GpuDevice(string luid, int number) : base("gpu" + luid, "GpuBrush")
    {
        _luid = luid;
        _number = number;
        Title = $"GPU {number}";
        Data = Monitor.Series($"gpu{luid}");
        GraphLabel = "Utilization (busiest engine)";
        ExtraColumns = 3;
    }

    public override void Update(Snapshot s)
    {
        var g = s.Gpus.FirstOrDefault(x => x.Luid == _luid);
        if (g == null) return;
        var sensors = g.Sensors;
        Model = g.Name;
        Subtitle = g.Name;
        Subtitle2 = sensors?.TempC is { } temp ? $"{g.Usage:0}%  ({temp:0} °C)" : $"{g.Usage:0}%";

        var types = Monitor.GpuEngineTypes(_luid);
        bool hasTemp = sensors?.TempC != null, hasPower = sensors?.PowerW != null || sensors?.PowerPct != null;
        int shape = types.Count * 4 + (hasTemp ? 1 : 0) + (hasPower ? 2 : 0);
        if (shape != _engineCount)
        {
            _engineCount = shape;
            Extras.Clear();
            var engines = PreferredEngines.Where(types.Contains).Concat(types.Except(PreferredEngines).OrderBy(t => t)).Take(4);
            foreach (var t in engines)
                Extras.Add(new GraphSpec { Label = Pretty(t), Data = Monitor.Series($"gpu{_luid}.e.{t}"), Brush = Brush });
            if (hasTemp)
                Extras.Add(new GraphSpec { Label = "Temperature", Data = Monitor.Series($"gpu{_luid}.temp"), Brush = Brush, Max = 100, MaxText = "100 °C" });
            if (hasPower && sensors!.PowerW != null)
            {
                double limit = sensors.PowerLimitW is > 0 and var l ? l : 300;
                Extras.Add(new GraphSpec { Label = "Board power", Data = Monitor.Series($"gpu{_luid}.power"), Brush = Brush, Max = limit, MaxText = $"{limit:0} W" });
            }
            else if (hasPower)
                Extras.Add(new GraphSpec { Label = "Power (% of limit)", Data = Monitor.Series($"gpu{_luid}.powerpct"), Brush = Brush });
            Extras.Add(new GraphSpec { Label = "Dedicated GPU memory", Data = Monitor.Series($"gpu{_luid}.ded"), Brush = Brush });
            Extras.Add(new GraphSpec { Label = "Shared GPU memory", Data = Monitor.Series($"gpu{_luid}.shared"), Brush = Brush });
            ExtraColumns = Extras.Count > 6 ? 4 : 3;
        }

        var stats = new List<(string, string)>
        {
            ("Utilization", $"{g.Usage:0}%"),
            ("Dedicated GPU memory", $"{Fmt.Bytes(g.DedUsed)} / {Fmt.Bytes(g.DedTotal)}"),
            ("Shared GPU memory", $"{Fmt.Bytes(g.SharedUsed)} / {Fmt.Bytes(g.SharedTotal)}"),
            ("GPU memory", $"{Fmt.Bytes(g.DedUsed + g.SharedUsed)} / {Fmt.Bytes(g.DedTotal + g.SharedTotal)}"),
        };
        if (sensors != null)
        {
            if (sensors.TempC is { } t) stats.Add(("Temperature", $"{t:0} °C"));
            if (sensors.PowerW is { } w) stats.Add(("Power", sensors.PowerLimitW is { } lim ? $"{w:0} / {lim:0} W" : $"{w:0} W"));
            else if (sensors.PowerPct is { } pct) stats.Add(("Power", sensors.PowerLimitW is { } lim ? $"{pct:0}% of {lim:0} W limit" : $"{pct:0}% of limit"));
            if (sensors.FanPct is { } fan) stats.Add(("Fan", $"{fan:0}%"));
            else if (sensors.FanRpm is { } rpm) stats.Add(("Fan", $"{rpm:0} RPM"));
            if (sensors.CoreMhz is { } core) stats.Add(("GPU clock", Fmt.Mhz(core)));
            if (sensors.MemMhz is { } mem) stats.Add(("Memory clock", Fmt.Mhz(mem)));
        }
        for (int i = 0; i < stats.Count; i++) Stat(i, stats[i].Item1, stats[i].Item2);
        while (Stats.Count > stats.Count) Stats.RemoveAt(Stats.Count - 1);

        if (Info.Count == 0 && StaticInfo.IsLoaded)
        {
            StaticInfo.Current.GpuDrivers.TryGetValue(g.Name, out var drv);
            Info = Rows(
                ("Driver version", drv.Version ?? ""),
                ("Driver date", drv.Date ?? ""),
                ("Dedicated memory", Fmt.Bytes(g.DedTotal)),
                ("Shared memory", Fmt.Bytes(g.SharedTotal)));
        }
    }

    static string Pretty(string engine) => engine switch
    {
        "VideoDecode" => "Video Decode",
        "VideoEncode" => "Video Encode",
        "VideoProcessing" => "Video Processing",
        _ => engine.Replace('_', ' '),
    };
}
