using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

public sealed record BatteryCard(string Title, string Charge, double ChargeFraction, Brush ChargeBrush, string State, IReadOnlyList<HealthStat> Live,
    string Verdict, string VerdictText, Brush VerdictBrush, double HeldFraction, IReadOnlyList<HealthStat> Capacity);

public partial class BatteryPage : UserControl, IPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    bool _reading, _loaded;

    public BatteryPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
    }

    public void OnShown()
    {
        _timer.Start();
        Refresh();
    }

    public void OnHidden() => _timer.Stop();

    async void Refresh()
    {
        if (_reading) return;
        _reading = true;
        if (!_loaded) Subtitle.Text = "Reading the battery…";
        List<BatteryReading> batteries;
        try
        {
            batteries = await Task.Run(BatteryReader.ReadAll);
        }
        catch (Exception ex)
        {
            Subtitle.Text = $"Couldn't read the battery: {ex.Message}";
            return;
        }
        finally
        {
            _reading = false;
        }
        _loaded = true;

        // No battery, nothing to watch; opening the page again checks once more.
        if (batteries.Count == 0) _timer.Stop();
        NoBattery.Visibility = batteries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Cards.ItemsSource = batteries.Select(ToCard).ToList();
        Subtitle.Text = batteries.Count == 0 ? "" : "Updates every 5 seconds while this page is open.";
    }

    static string Title(BatteryReading b)
    {
        var parts = new[] { b.Name, b.Maker }.Where(s => s.Length > 0).ToList();
        return parts.Count > 0 ? string.Join(" · ", parts) : "Battery";
    }

    static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    static HealthStat Missing(string label, string note = "") => new(label, "Not reported", note, HealthTone.Missing);

    static BatteryCard ToCard(BatteryReading b)
    {
        var charge = b.ChargePct;
        var chargeBrush = charge is < 10 ? "BadBrush" : charge is < 25 ? "WarnBrush" : "GoodBrush";

        var live = new List<HealthStat>
        {
            b.Watts is { } watts ? RateStat(b, watts) : Missing("Charge rate"),
            b.VoltageMv is { } mv ? new HealthStat("Voltage", $"{mv / 1000.0:0.00} V", "") : Missing("Voltage"),
            TimeStat(b),
        };

        var (verdict, text, brush) = Judge(b);
        var capacity = new List<HealthStat>
        {
            b.DesignMwh is { } design ? new HealthStat("Design capacity", $"{Fmt.Num(design)} mWh", "What it held when new") : Missing("Design capacity"),
            b.FullMwh is { } full ? new HealthStat("Full charge capacity", $"{Fmt.Num(full)} mWh", "What it holds now") : Missing("Full charge capacity"),
            b.WearPct is { } wear ? new HealthStat("Wear", Fmt.Pct(wear), "Capacity lost so far", wear >= 40 ? HealthTone.Bad : wear >= 20 ? HealthTone.Warn : HealthTone.Normal) : Missing("Wear"),
            b.Cycles is { } cycles ? new HealthStat("Cycle count", Fmt.Num(cycles), "Full charges used so far") : Missing("Cycle count", "Many laptops don't report this"),
        };

        return new BatteryCard(Title(b), charge is { } c ? $"{c:0}%" : "Charge not reported", (charge ?? 0) / 100, Brush(chargeBrush), b.State, live,
            verdict, text, Brush(brush), (b.HeldPct ?? 0) / 100, capacity);
    }

    static HealthStat RateStat(BatteryReading b, double watts)
    {
        if (b.Charging == true) return new HealthStat("Charging at", Fmt.Watts(watts), "Power going into the battery");
        if (b.Discharging == true) return new HealthStat("Draining at", Fmt.Watts(watts), "Power the PC is drawing from it");
        return new HealthStat("Charge rate", Fmt.Watts(watts), "Not charging or draining");
    }

    static HealthStat TimeStat(BatteryReading b)
    {
        bool charging = b.Charging == true;
        var label = charging ? "Time to full" : "Time remaining";
        if (b.TimeLeft is { } left) return new HealthStat(label, Fmt.Span(left), charging ? "Estimate. Slows near the end" : "Estimate at the current drain");
        return b.OnAc == true && !charging ? new HealthStat(label, "Plugged in", "Running from the charger", HealthTone.Missing) : Missing(label);
    }

    static (string Verdict, string Text, string Brush) Judge(BatteryReading b)
    {
        var held = $"{Math.Min(100, b.HeldPct ?? 0):0}%";
        return b.Condition switch
        {
            BatteryCondition.Good => ("Good", $"It holds {held} of the charge it held when new.", "GoodBrush"),
            BatteryCondition.Fair => ("Normal wear", $"It holds {held} of the charge it held when new, which is normal for a battery in regular use.", "GoodBrush"),
            BatteryCondition.Worn => ("Worn", $"It holds {held} of the charge it held when new, so you'll notice shorter run times.", "WarnBrush"),
            BatteryCondition.Replace => ("Replace soon", $"It holds only {held} of the charge it held when new. A new battery would bring back most of the lost run time.", "BadBrush"),
            _ => ("Unknown", "This battery doesn't report its capacity, so its wear can't be worked out.", "SubtleBrush"),
        };
    }
}
