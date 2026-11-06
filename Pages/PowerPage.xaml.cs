using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tman.Controls;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

/// <summary>Live clocks per logical processor, throttling, core parking, RAPL power, and the power plan.</summary>
public partial class PowerPage : UserControl, IPage
{
    static SystemMonitor Monitor => SystemMonitor.Instance;

    readonly List<CoreTile> _cores = new();
    readonly List<ChannelRow> _channels = new();
    double _peakMhz, _peakChannel, _peakPackage;
    bool _visible, _loadingPlans;
    int _tick;

    public PowerPage()
    {
        InitializeComponent();
        ClockGraph.Data = Monitor.Series("cpu.mhz");
        ClockGraph.Data2 = Monitor.Series("cpu.mhz.max");
        PowerGraph.Data = Monitor.Series("power.pkg");
        PowerGraph.Data2 = Monitor.Series("power.cores");
        foreach (var (_, name) in PowerPlans.Modes) ModeBox.Items.Add(name);
        SizeChanged += (_, e) => Tiles.Columns = e.NewSize.Width > 900 ? 4 : 2;
        Monitor.Updated += OnUpdated;
    }

    public void OnShown()
    {
        _visible = true;
        LoadPlans();
        SleepWake.Refresh();
        if (Monitor.Latest is { } s) OnUpdated(s);
    }

    public void OnHidden() => _visible = false;

    void OnUpdated(Snapshot s)
    {
        if (!_visible) return;
        var cpu = s.Cpu;
        var info = StaticInfo.Current;
        Subtitle.Text = string.Join("   ·   ", new[]
        {
            info.CpuName,
            info.BaseMhz > 0 ? $"Base {Fmt.Mhz(info.BaseMhz)}" : "",
            info.Cores > 0 ? $"{info.Cores} cores, {info.Logical} logical processors" : "",
        }.Where(x => x != ""));

        // Clock
        double avg = cpu.AvgMhz > 0 ? cpu.AvgMhz : cpu.SpeedGhz * 1000;
        double fastest = cpu.CoreMhz.Length > 0 ? cpu.CoreMhz.Max() : avg;
        _peakMhz = Math.Max(_peakMhz, Math.Max(fastest, info.BaseMhz));
        ClockValue.Text = avg > 0 ? Fmt.Mhz(avg) : "—";
        ClockSub.Text = $"Fastest {Fmt.Mhz(fastest)}" + (info.BaseMhz > 0 ? $" · base {Fmt.Mhz(info.BaseMhz)}" : "");
        double clockTop = Math.Max(1000, Math.Ceiling(Math.Max(Monitor.Series("cpu.mhz.max").Max(), info.BaseMhz) * 1.05 / 1000) * 1000);
        ClockGraph.Maximum = clockTop;
        ClockPanel.HeaderRight = $"0 – {clockTop / 1000:0} GHz";

        // Power
        if (s.Power.Package is { } pkg)
        {
            PowerValue.Text = Fmt.Watts(pkg);
            PowerSub.Text = s.Power.Cores is { } c ? $"Cores {Fmt.Watts(c)} · uncore & I/O {Fmt.Watts(Math.Max(0, pkg - c))}" : "Whole processor package";
            double powerTop = Math.Max(25, Math.Ceiling(Monitor.Series("power.pkg").Max() * 1.1 / 25) * 25);
            PowerGraph.Maximum = powerTop;
            PowerGraphCard.HeaderRight = $"0 – {powerTop:0} W";
            PowerLegend.Text = s.Power.Cores != null ? "Solid: whole package · dashed: cores only" : "Whole package";
            PowerGraphCard.Visibility = Visibility.Visible;
        }
        else
        {
            PowerValue.Text = "—";
            PowerSub.Text = "This PC doesn't expose an energy meter";
            PowerGraphCard.Visibility = Visibility.Hidden;
        }

        // Throttling: % Performance Limit drops below 100 when heat, power or firmware caps the clock.
        bool limited = cpu.PerfLimit < 99.5 || cpu.LimitFlags != 0;
        LimitValue.Text = limited ? $"Capped at {cpu.PerfLimit:0}%" : "None";
        LimitValue.Foreground = (Brush)FindResource(limited ? "WarnBrush" : "GoodBrush");
        LimitSub.Text = limited
            ? "Heat, power or firmware is holding the clock back" + (cpu.LimitFlags != 0 ? $" (flags 0x{cpu.LimitFlags:X})" : "")
            : "Nothing is holding the clock back";

        int total = Math.Max(cpu.Cores.Length, cpu.Parked.Length);
        int parked = cpu.Parked.Count(p => p);
        ParkedValue.Text = $"{parked} of {total}";
        ParkedSub.Text = parked > 0 ? "Parked processors sleep to save power" : "Every logical processor is available";

        SyncCores(cpu);
        SyncChannels(s.Power);
        if (_tick++ % 5 == 0) UpdatePolicy();
    }

    void SyncCores(CpuStat cpu)
    {
        int n = cpu.Cores.Length;
        if (_cores.Count != n)
        {
            CoreGrid.Children.Clear();
            _cores.Clear();
            for (int i = 0; i < n; i++)
            {
                var t = new CoreTile(i);
                _cores.Add(t);
                CoreGrid.Children.Add(t.Root);
            }
            CoreGrid.Columns = Math.Clamp(n, 1, 8);
        }
        for (int i = 0; i < n; i++)
            _cores[i].Set(i < cpu.CoreMhz.Length ? cpu.CoreMhz[i] : 0, cpu.Cores[i], i < cpu.Parked.Length && cpu.Parked[i], _peakMhz);
        CoresNote.Text = cpu.CoreMhz.Length > 0 ? $"Measured clock while running · bars relative to {Fmt.Mhz(_peakMhz)}" : "Windows doesn't report per-processor clocks here";
    }

    void SyncChannels(PowerStat power)
    {
        var list = power.Channels;
        if (list.Count == 0)
        {
            if (_channels.Count == 0 && Channels.Children.Count == 0)
                Channels.Children.Add(new TextBlock
                {
                    Text = "Windows has no energy meter for this processor. Most AMD Ryzen and Intel Core desktops expose one (RAPL); many laptops and VMs don't.",
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)FindResource("Subtle"),
                });
            MeterNote.Text = "";
            return;
        }
        if (_channels.Count != list.Count)
        {
            Channels.Children.Clear();
            _channels.Clear();
            foreach (var _ in list)
            {
                var r = new ChannelRow();
                _channels.Add(r);
                Channels.Children.Add(r.Root);
            }
        }
        double pkg = power.Package ?? 0;
        _peakPackage = Math.Max(_peakPackage, pkg);
        static bool IsTotal(PowerChannel c) => c.Name.StartsWith("Package") || c.Name == "Platform";
        foreach (var c in list) if (!IsTotal(c)) _peakChannel = Math.Max(_peakChannel, c.Watts);
        // Totals scale against the highest package draw seen, parts against the busiest part, so idle cores still show.
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            double scale = IsTotal(c) ? _peakPackage : _peakChannel;
            _channels[i].Set(c.Name, c.Watts, scale > 0 ? c.Watts / scale : 0, IsTotal(c));
        }
        MeterNote.Text = pkg > 0 ? "RAPL energy counters, via Windows" : "Windows energy meter";
    }

    void LoadPlans()
    {
        _loadingPlans = true;
        try
        {
            var plans = PowerPlans.All();
            var active = PowerPlans.Active();
            PlanBox.ItemsSource = plans;
            PlanBox.SelectedItem = plans.FirstOrDefault(p => p.Id == active);

            var mode = PowerPlans.Mode();
            ModePanel.Visibility = mode == null ? Visibility.Collapsed : Visibility.Visible;
            bool balanced = active == PowerPlans.Balanced;
            ModeBox.IsEnabled = balanced;
            ModeBox.SelectedIndex = balanced && mode is { } m ? Array.FindIndex(PowerPlans.Modes, x => x.Id == m) : -1;
            ModeNote.Text = balanced ? "" : "Windows only applies power modes on the Balanced plan.";
        }
        finally
        {
            _loadingPlans = false;
        }
        UpdatePolicy();
    }

    void UpdatePolicy()
    {
        var src = PowerPlans.Source();
        var lines = new List<string>();
        if (src.HasBattery)
        {
            var left = src.Remaining is { } r && !src.OnAc ? $", {Fmt.Span(r)} left" : "";
            lines.Add($"Power source: {(src.OnAc ? "plugged in" : "battery")} · {src.Percent}% charged{left}{(src.Saver ? " · battery saver on" : "")}");
        }
        else
        {
            lines.Add("Power source: AC power (no battery)");
        }
        if (PowerPlans.Active() is { } active)
        {
            var p = PowerPlans.Policy(active, src.OnAc);
            if (p.MinState is { } min && p.MaxState is { } max) lines.Add($"Processor state: {min}% minimum, {max}% maximum");
            if (p.Boost != null) lines.Add($"Boost mode: {p.Boost}");
            if (p.MinUnparked is { } u) lines.Add($"Cores kept unparked: at least {u}%");
        }
        PolicyText.Text = string.Join("\n", lines);
    }

    void PlanBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlans || PlanBox.SelectedItem is not PowerPlans.Plan plan) return;
        if (!PowerPlans.SetActive(plan.Id)) Ui.Error("Windows didn't allow switching the power plan.");
        LoadPlans();
    }

    void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPlans || ModeBox.SelectedIndex < 0) return;
        if (!PowerPlans.SetMode(PowerPlans.Modes[ModeBox.SelectedIndex].Id)) Ui.Error("Windows didn't allow changing the power mode.");
        LoadPlans();
    }

    void PowerOptions_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "powercfg.cpl") { UseShellExecute = true }); } catch { }
    }

    sealed class CoreTile
    {
        public readonly Border Root;
        readonly TextBlock _value, _load, _parked;
        readonly UsageBar _bar;

        public CoreTile(int index)
        {
            var app = Application.Current;
            var subtle = (Brush)app.FindResource("SubtleBrush");
            var name = new TextBlock { Text = $"CPU {index}", FontSize = 12, Foreground = subtle };
            _parked = new TextBlock { Text = "Parked", FontSize = 11, Foreground = (Brush)app.FindResource("WarnBrush"), HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
            var top = new Grid();
            top.Children.Add(name);
            top.Children.Add(_parked);
            _value = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 6) };
            _bar = new UsageBar { Height = 4, Fill = (Brush)app.FindResource("CpuBrush") };
            _load = new TextBlock { FontSize = 11, Foreground = subtle, Margin = new Thickness(0, 5, 0, 0) };
            var stack = new StackPanel();
            stack.Children.Add(top);
            stack.Children.Add(_value);
            stack.Children.Add(_bar);
            stack.Children.Add(_load);
            Root = new Border
            {
                Background = (Brush)app.FindResource("CardBrush"),
                BorderBrush = (Brush)app.FindResource("CardStrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 6, 6),
                Child = stack,
            };
        }

        public void Set(double mhz, double load, bool parked, double scale)
        {
            _value.Text = mhz > 0 ? Fmt.Mhz(mhz) : "—";
            _bar.Value = scale > 0 ? mhz / scale : 0;
            _load.Text = $"{load:0}% load";
            _parked.Visibility = parked ? Visibility.Visible : Visibility.Collapsed;
            Root.Opacity = parked ? 0.55 : 1;
        }
    }

    sealed class ChannelRow
    {
        public readonly Grid Root = new() { Margin = new Thickness(0, 3, 0, 3) };
        readonly TextBlock _name, _value;
        readonly UsageBar _bar;

        public ChannelRow()
        {
            var app = Application.Current;
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            _name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _bar = new UsageBar { Height = 6, Fill = (Brush)app.FindResource("PowerBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            _value = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_bar, 1);
            Grid.SetColumn(_value, 2);
            Root.Children.Add(_name);
            Root.Children.Add(_bar);
            Root.Children.Add(_value);
        }

        public void Set(string name, double watts, double fraction, bool emphasis)
        {
            _name.Text = name;
            _name.FontWeight = emphasis ? FontWeights.SemiBold : FontWeights.Normal;
            _value.Text = Fmt.Watts(watts);
            _bar.Value = fraction;
        }
    }
}
