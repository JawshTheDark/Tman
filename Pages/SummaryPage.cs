using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Tman.Controls;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

/// <summary>
/// Instrument-panel dashboard: a band of LED gauges, the processor trace with a per-core LED spectrum,
/// the busiest processes, memory, and a meter for every disk and network adapter.
/// </summary>
public sealed class SummaryPage : UserControl, IPage
{
    static SystemMonitor Monitor => SystemMonitor.Instance;
    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    readonly TextBlock _subtitle = new() { Margin = new Thickness(0, 2, 0, 14) };
    readonly UniformGrid _gaugeGrid = new() { Rows = 1 };
    readonly ArcGauge _cpu, _mem, _gpu, _disk, _net, _power;
    readonly NeonPanel _cpuPanel, _memPanel;
    readonly UniformGrid _spectrum = new() { Rows = 1, Height = 58 };
    readonly List<LedBar> _coreBars = new();
    readonly TopList _top;
    readonly LedBar _memBar = new() { Orientation = Orientation.Horizontal, Height = 12, Segment = 4, Gap = 2, Margin = new Thickness(0, 0, 0, 10) };
    readonly TextBlock _memStats = new() { FontSize = 11, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _ioRows = new();
    readonly Dictionary<string, MeterRow> _io = new();
    bool _visible;

    public SummaryPage()
    {
        _subtitle.Style = (Style)FindResource("Subtle");

        _cpu = Gauge("CpuBrush");
        _mem = Gauge("MemBrush");
        _gpu = Gauge("GpuBrush");
        _disk = Gauge("DiskBrush");
        _net = Gauge("NetBrush");
        _power = Gauge("PowerBrush");
        foreach (var g in new[] { _cpu, _mem, _gpu, _disk, _net, _power }) _gaugeGrid.Children.Add(g);
        var vitals = new NeonPanel { Header = "Vitals", Child = _gaugeGrid, Margin = new Thickness(0, 0, 0, 14) };

        // Processor: live trace with an LED bar per logical processor underneath, like a spectrum analyzer.
        var cpuGraph = new LineGraph { Data = Monitor.Cpu, Stroke = Res("CpuBrush"), Maximum = 100, MinHeight = 110 };
        var spectrumLabel = new TextBlock { Text = "LOGICAL PROCESSORS", Style = (Style)FindResource("PanelLabel"), Margin = new Thickness(0, 10, 0, 5) };
        var cpuBody = new DockPanel();
        DockPanel.SetDock(_spectrum, Dock.Bottom);
        DockPanel.SetDock(spectrumLabel, Dock.Bottom);
        cpuBody.Children.Add(_spectrum);
        cpuBody.Children.Add(spectrumLabel);
        cpuBody.Children.Add(cpuGraph);
        _cpuPanel = new NeonPanel { Header = "Processor", Accent = Res("CpuBrush"), Child = cpuBody, Margin = new Thickness(0, 0, 14, 14) };

        _top = new TopList();
        var topPanel = new NeonPanel { Header = "Top processes", HeaderRight = "CPU · MEMORY", Child = _top.Root, Margin = new Thickness(0, 0, 0, 14) };

        var middle = new Grid();
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 330 });
        Grid.SetColumn(topPanel, 1);
        middle.Children.Add(_cpuPanel);
        middle.Children.Add(topPanel);

        // Memory: fill meter, trace and the numbers behind it.
        _memBar.Fill = Res("MemBrush");
        _memStats.Foreground = Res("SubtleBrush");
        var memBody = new StackPanel();
        memBody.Children.Add(_memBar);
        memBody.Children.Add(new LineGraph { Data = Monitor.Memory, Stroke = Res("MemBrush"), Maximum = 100, Height = 64 });
        memBody.Children.Add(_memStats);
        _memPanel = new NeonPanel { Header = "Memory", Accent = Res("MemBrush"), Child = memBody, Margin = new Thickness(0, 0, 14, 14) };

        var ioPanel = new NeonPanel { Header = "Disks & network", Accent = Res("DiskBrush"), Child = _ioRows, Margin = new Thickness(0, 0, 0, 14) };

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(ioPanel, 1);
        bottom.Children.Add(_memPanel);
        bottom.Children.Add(ioPanel);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Summary", Style = (Style)FindResource("PageTitle") });
        stack.Children.Add(_subtitle);
        stack.Children.Add(vitals);
        stack.Children.Add(middle);
        stack.Children.Add(bottom);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 0, 8, 0) };

        Monitor.Updated += OnUpdated;
    }

    static ArcGauge Gauge(string brush) => new() { Fill = Res(brush), Margin = new Thickness(4, 0, 4, 0) };

    public void OnShown()
    {
        _visible = true;
        if (Monitor.Latest is { } s) OnUpdated(s);
    }

    public void OnHidden() => _visible = false;

    void OnUpdated(Snapshot s)
    {
        if (!_visible) return;
        var info = StaticInfo.Current;
        var os = info.Sections.FirstOrDefault()?.Rows;
        string Row(string k) => os?.FirstOrDefault(r => r.Key == k).Value ?? "";
        _subtitle.Text = string.Join("   ·   ", new[] { Environment.MachineName, $"{Row("Edition")} {Row("Version")}".Trim(), $"up {Fmt.Uptime(s.Uptime)}" }.Where(x => x != ""));

        double ghz = s.Cpu.AvgMhz > 0 ? s.Cpu.AvgMhz / 1000 : s.Cpu.SpeedGhz;
        Set(_cpu, s.Cpu.Total / 100, $"{s.Cpu.Total:0}%", ghz > 0 ? $"{ghz:0.00} GHz" : "", "CPU",
            info.Cores > 0 ? $"{info.Cores} cores · {info.Logical} threads" : $"{Environment.ProcessorCount} threads");

        Set(_mem, s.Mem.UsedPct / 100, $"{s.Mem.UsedPct:0}%", $"{Fmt.Bytes(s.Mem.Used)} / {Fmt.Bytes(s.Mem.Total)}", "MEMORY",
            $"{Fmt.Bytes(s.Mem.Available)} available");

        var gpu = s.Gpus.OrderByDescending(g => g.DedTotal).FirstOrDefault();
        _gpu.Visibility = gpu == null ? Visibility.Collapsed : Visibility.Visible;
        if (gpu != null)
        {
            var sensors = gpu.Sensors;
            var unit = sensors?.TempC is { } temp ? $"{temp:0} °C · {Fmt.Bytes(gpu.DedUsed)}" : $"{Fmt.Bytes(gpu.DedUsed)} VRAM";
            var sub = sensors?.PowerW is { } watts ? $"{watts:0} W · {gpu.Name}"
                : sensors?.PowerPct is { } pct ? $"{pct:0}% power · {gpu.Name}"
                : gpu.Name;
            Set(_gpu, gpu.Usage / 100, $"{gpu.Usage:0}%", unit, "GPU", sub);
        }

        var sysDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows)[..2];
        var disk = s.Disks.FirstOrDefault(d => d.Letters.Contains(sysDrive, StringComparison.OrdinalIgnoreCase)) ?? s.Disks.FirstOrDefault();
        _disk.Visibility = disk == null ? Visibility.Collapsed : Visibility.Visible;
        if (disk != null)
            Set(_disk, disk.Active / 100, $"{disk.Active:0}%", "active", $"DISK {disk.Letters}".Trim(),
                $"R {Fmt.Rate(disk.ReadBps)} · W {Fmt.Rate(disk.WriteBps)}");

        // Network has no natural 100%, so the gauge scales to the busiest moment of the last minute.
        double send = s.Nets.Sum(n => n.SendBps), recv = s.Nets.Sum(n => n.RecvBps);
        double netScale = Math.Max(125_000, Math.Max(Monitor.Series("net.r").Max(), Monitor.Series("net.s").Max()));
        var down = Fmt.Bits(recv).Split(' ');
        Set(_net, Math.Max(send, recv) / netScale, down[0], $"{down.ElementAtOrDefault(1)} down", "NETWORK", $"Up {Fmt.Bits(send)}");

        if (s.Power.Package is { } pkg)
        {
            double powerScale = Math.Max(50, Math.Ceiling(Monitor.Series("power.pkg").Max() * 1.25 / 25) * 25);
            Set(_power, pkg / powerScale, $"{pkg:0}", "watts", "CPU POWER", s.Power.Cores is { } c ? $"Cores {Fmt.Watts(c)}" : "Package");
        }
        else
        {
            double commit = s.Mem.CommitLimit > 0 ? (double)s.Mem.CommitTotal / s.Mem.CommitLimit : 0;
            Set(_power, commit, $"{commit * 100:0}%", $"{Fmt.Bytes(s.Mem.CommitTotal)} / {Fmt.Bytes(s.Mem.CommitLimit)}", "COMMIT", "Memory promised to apps");
        }
        _gaugeGrid.Columns = _gaugeGrid.Children.OfType<UIElement>().Count(e => e.Visibility == Visibility.Visible);

        // Processor panel
        _cpuPanel.HeaderRight = string.Join("  ·  ", new[]
        {
            $"{s.Cpu.Total:0.0}%",
            ghz > 0 ? $"{ghz:0.00} GHz" : "",
            s.Power.Package is { } w ? Fmt.Watts(w) : "",
            $"{Fmt.Num(s.ThreadCount)} threads",
        }.Where(x => x != ""));
        var cores = s.Cpu.Cores;
        if (_coreBars.Count != cores.Length)
        {
            _spectrum.Children.Clear();
            _coreBars.Clear();
            foreach (var _ in cores)
            {
                var bar = new LedBar { UseScale = true, Segment = 2, Gap = 1, Margin = new Thickness(1.5, 0, 1.5, 0) };
                _coreBars.Add(bar);
                _spectrum.Children.Add(bar);
            }
            _spectrum.Columns = Math.Max(1, cores.Length);
        }
        for (int i = 0; i < cores.Length; i++)
        {
            _coreBars[i].Value = cores[i] / 100;
            _coreBars[i].ToolTip = $"CPU {i}: {cores[i]:0}%" + (i < s.Cpu.CoreMhz.Length ? $" at {Fmt.Mhz(s.Cpu.CoreMhz[i])}" : "");
        }

        var procs = s.Processes.Where(p => p.Pid != 0).OrderByDescending(p => p.Cpu).ThenByDescending(p => p.PrivateWs).Take(TopList.Rows).ToList();
        _top.Set(procs);

        // Memory panel
        _memPanel.HeaderRight = $"{Fmt.Bytes(s.Mem.Used)} / {Fmt.Bytes(s.Mem.Total)}";
        _memBar.Value = s.Mem.UsedPct / 100;
        _memStats.Text =
            $"Available {Fmt.Bytes(s.Mem.Available)}   ·   System cache {Fmt.Bytes(s.Mem.Cached)}   ·   Committed {Fmt.Bytes(s.Mem.CommitTotal)} of {Fmt.Bytes(s.Mem.CommitLimit)}\n" +
            $"Paged pool {Fmt.Bytes(s.Mem.Paged)}   ·   Non-paged pool {Fmt.Bytes(s.Mem.NonPaged)}   ·   {Fmt.Num(s.HandleCount)} handles";

        SyncIo(s);
    }

    static void Set(ArcGauge g, double value, string text, string unit, string label, string sub)
    {
        g.Value = value;
        g.Text = text;
        g.Unit = unit;
        g.Label = label;
        g.Sub = sub;
        g.ToolTip = $"{label}: {text} {unit}\n{sub}";
    }

    void SyncIo(Snapshot s)
    {
        var seen = new HashSet<string>();
        foreach (var d in s.Disks)
        {
            var key = $"disk{d.Index}";
            seen.Add(key);
            Row(key, "DiskBrush").Set(d.Letters.Length > 0 ? $"DISK {d.Index} ({d.Letters})" : $"DISK {d.Index}", d.Active / 100,
                $"{d.Active:0}%  ·  R {Fmt.Rate(d.ReadBps)}  ·  W {Fmt.Rate(d.WriteBps)}");
        }
        foreach (var n in s.Nets)
        {
            var key = $"net{n.Id}";
            seen.Add(key);
            double scale = Math.Max(125_000, Math.Max(Monitor.Series($"net{n.Id}.r").Max(), Monitor.Series($"net{n.Id}.s").Max()));
            Row(key, "NetBrush").Set(n.Name.ToUpperInvariant(), Math.Max(n.SendBps, n.RecvBps) / scale,
                $"Down {Fmt.Bits(n.RecvBps)}  ·  Up {Fmt.Bits(n.SendBps)}");
        }
        foreach (var gone in _io.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _ioRows.Children.Remove(_io[gone].Root);
            _io.Remove(gone);
        }
    }

    MeterRow Row(string key, string brush)
    {
        if (_io.TryGetValue(key, out var row)) return row;
        row = new MeterRow(Res(brush));
        _io[key] = row;
        // Disks first, then adapters, each in the order they're first seen.
        int at = key.StartsWith("disk") ? _io.Keys.Count(k => k.StartsWith("disk")) - 1 : _ioRows.Children.Count;
        _ioRows.Children.Insert(Math.Min(at, _ioRows.Children.Count), row.Root);
        return row;
    }

    /// <summary>Label, LED meter and numbers on one line.</summary>
    sealed class MeterRow
    {
        public readonly Grid Root = new() { Margin = new Thickness(0, 4, 0, 6) };
        readonly TextBlock _name, _text;
        readonly LedBar _bar;

        public MeterRow(Brush brush)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.RowDefinitions.Add(new RowDefinition());
            Root.RowDefinitions.Add(new RowDefinition());
            _name = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            _bar = new LedBar { Orientation = Orientation.Horizontal, Height = 9, Segment = 3, Gap = 1, Fill = brush, VerticalAlignment = VerticalAlignment.Center };
            _text = new TextBlock { FontSize = 11, Foreground = Res("SubtleBrush"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(_bar, 1);
            Grid.SetRow(_text, 1);
            Grid.SetColumn(_text, 1);
            Root.Children.Add(_name);
            Root.Children.Add(_bar);
            Root.Children.Add(_text);
        }

        public void Set(string name, double value, string text)
        {
            _name.Text = name;
            _name.ToolTip = name;
            _bar.Value = value;
            _text.Text = text;
        }
    }

    sealed class TopList
    {
        public const int Rows = 11;
        public readonly StackPanel Root = new();
        readonly (Image Icon, TextBlock Name, LedBar Bar, TextBlock Cpu, TextBlock Mem, Grid Row)[] _rows;

        public TopList()
        {
            _rows = new (Image, TextBlock, LedBar, TextBlock, TextBlock, Grid)[Rows];
            for (int i = 0; i < Rows; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Height = 20 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
                var icon = new Image { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var bar = new LedBar { Orientation = Orientation.Horizontal, Height = 7, Segment = 3, Gap = 1, Fill = Res("CpuBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                var cpu = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("CpuBrush") };
                var mem = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("MemBrush") };
                Grid.SetColumn(name, 1);
                Grid.SetColumn(bar, 2);
                Grid.SetColumn(cpu, 3);
                Grid.SetColumn(mem, 4);
                foreach (var e in new UIElement[] { icon, name, bar, cpu, mem }) row.Children.Add(e);
                Root.Children.Add(row);
                _rows[i] = (icon, name, bar, cpu, mem, row);
            }
        }

        public void Set(List<ProcSample> items)
        {
            // The busiest process fills the meter; the rest are relative to it, so small loads still show.
            double scale = Math.Max(1, items.Count > 0 ? items[0].Cpu : 1);
            for (int i = 0; i < Rows; i++)
            {
                var r = _rows[i];
                if (i >= items.Count)
                {
                    r.Row.Visibility = Visibility.Hidden;
                    continue;
                }
                var p = items[i];
                r.Row.Visibility = Visibility.Visible;
                r.Icon.Source = p.Details.Icon;
                r.Name.Text = p.Details.Description is { Length: > 0 } d ? d : p.Name;
                r.Row.ToolTip = $"{p.Name} (PID {p.Pid})";
                r.Bar.Value = p.Cpu / scale;
                r.Cpu.Text = Fmt.Pct(p.Cpu);
                r.Mem.Text = Fmt.Bytes(p.PrivateWs);
            }
        }
    }
}
