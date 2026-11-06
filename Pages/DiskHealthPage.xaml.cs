using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public enum HealthTone { Normal, Good, Warn, Bad, Missing }

public sealed record HealthStat(string Label, string Value, string Note, HealthTone Tone = HealthTone.Normal);

public sealed record DriveHealthCard(string Model, string Spec, string Verdict, Brush VerdictBrush, string Reason, string Gap, IReadOnlyList<HealthStat> Stats)
{
    public Visibility GapVisibility => Gap.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public partial class DiskHealthPage : UserControl, IPage
{
    public DiskHealthPage()
    {
        InitializeComponent();
        Note.Text = "NVMe health is read without admin. Admin adds read and write error totals and the highest recorded temperature, if the drive reports them.";
        Load();
    }

    async void Load()
    {
        RefreshButton.IsEnabled = false;
        Subtitle.Text = "Reading drive health…";
        var drives = await Task.Run(DiskHealthReader.ReadAll);
        RefreshButton.IsEnabled = true;
        Cards.ItemsSource = drives.Select(ToCard).ToList();
        Subtitle.Text = $"{drives.Count} {(drives.Count == 1 ? "drive" : "drives")} · checked {DateTime.Now:t}";
        EmptyText.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "Windows didn't list any drives. Its storage management service may be stopped, so try Refresh in a moment.";
        if (drives.Any(d => d.Opened && d.IsAta))
            Note.Text = "SATA drives share little without admin. Admin adds their SMART data: error and bad sector counts, wear, power-on hours and temperature. On every drive it also adds read and write error totals and the highest recorded temperature, if the drive reports them.";
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    static DriveHealthCard ToCard(DriveHealth d)
    {
        var (verdict, brush) = d.Verdict switch
        {
            DriveVerdict.Healthy => ("Healthy", "GoodBrush"),
            DriveVerdict.CheckSoon => ("Check soon", "WarnBrush"),
            DriveVerdict.Failing => ("Failing", "BadBrush"),
            _ => ("Unknown", "SubtleBrush"),
        };
        var gap = d.Verdict == DriveVerdict.Unknown ? "" : d.Gap ?? "";
        return new DriveHealthCard(d.Model.Length > 0 ? d.Model : "Unnamed drive", (d.Number >= 0 ? $"Disk {d.Number} · " : "") + $"{d.Type} · {Fmt.Bytes(d.Size)}", verdict, Brush(brush), d.Reason, gap, Stats(d));
    }

    static List<HealthStat> Stats(DriveHealth d)
    {
        // SATA and SAS drives give these to an administrator; USB bridges and virtual disks don't have them at all.
        bool adminCould = d.Opened && d.Gap == null && !MainWindow.IsAdmin;
        HealthStat Missing(string label, string note = "", bool admin = true) =>
            new(label, admin && adminCould ? "Needs admin" : "Not reported", note, HealthTone.Missing);

        var stats = new List<HealthStat>();

        if (d.TemperatureC is { } temp)
        {
            var notes = new List<string>();
            if (d.TemperatureMaxC is { } max) notes.Add($"Highest recorded {max} °C");
            if (d.TemperatureWarnC is { } warn) notes.Add($"Warning at {warn} °C");
            stats.Add(new HealthStat("Temperature", $"{temp} °C", string.Join(". ", notes), temp >= d.TemperatureWarnC ? HealthTone.Warn : HealthTone.Normal));
        }
        else stats.Add(Missing("Temperature"));

        if (d.IsHdd) stats.Add(new HealthStat("Life remaining", "Doesn't apply", "Hard drives have no write limit", HealthTone.Missing));
        else if (d.WearPct is { } wear)
            stats.Add(new HealthStat("Life remaining", $"{Math.Max(0, 100 - wear)}%", $"{wear}% of rated write life used", wear >= 90 ? HealthTone.Warn : HealthTone.Normal));
        else stats.Add(Missing("Life remaining"));

        if (d.SparePct is { } spare)
            stats.Add(new HealthStat("Spare space", $"{spare}%", d.SpareThresholdPct is > 0 ? $"Replaces worn cells. Warning below {d.SpareThresholdPct}%" : "Replaces worn cells",
                d.SpareThresholdPct is > 0 and var limit && spare < limit + 10 ? HealthTone.Warn : HealthTone.Normal));

        if (d.PowerOnHours is { } hours)
        {
            var note = $"About {Age(hours)} of running time";
            if (d.PowerCycles is { } cycles) note += $". {Fmt.Num(cycles)} power cycles";
            stats.Add(new HealthStat("Power-on time", $"{Fmt.Num(hours)} hours", note));
        }
        else stats.Add(Missing("Power-on time"));

        if (d.BytesWritten is { } written)
            stats.Add(new HealthStat("Data written", Fmt.Bytes(written), d.BytesRead is { } read ? $"{Fmt.Bytes(read)} read" : ""));
        else stats.Add(Missing("Data written", d.IsAta ? "Units differ by maker, so it's left out" : "", admin: false));

        if (d.DataErrors is { } errors)
            stats.Add(new HealthStat("Uncorrected errors", Fmt.Num(errors), "Data the drive couldn't fix", errors > 0 ? HealthTone.Warn : HealthTone.Good));
        else stats.Add(Missing("Uncorrected errors"));

        stats.Add(d.ReadErrors is { } readErrors ? new HealthStat("Read errors", Fmt.Num(readErrors), "All, including corrected ones") : Missing("Read errors"));
        stats.Add(d.WriteErrors is { } writeErrors ? new HealthStat("Write errors", Fmt.Num(writeErrors), "All, including corrected ones") : Missing("Write errors"));

        if (d.ReallocatedSectors is { } moved)
            stats.Add(new HealthStat("Replaced sectors", Fmt.Num(moved), "Bad sectors swapped for spares", moved > 0 ? HealthTone.Warn : HealthTone.Good));
        if (d.PendingSectors is { } pending)
            stats.Add(new HealthStat("Pending sectors", Fmt.Num(pending), "Failed reads waiting to be replaced", pending > 0 ? HealthTone.Warn : HealthTone.Good));
        if (d.ErrorLogEntries is { } log)
            stats.Add(new HealthStat("Error log entries", Fmt.Num(log), "Mostly rejected commands, usually harmless"));
        if (d.UnsafeShutdowns is { } unsafeStops)
            stats.Add(new HealthStat("Unsafe shutdowns", Fmt.Num(unsafeStops), "Power was lost without warning"));
        return stats;
    }

    static string Age(long hours)
    {
        double days = hours / 24.0;
        return days >= 365 ? $"{days / 365:0.#} years" : days >= 2 ? $"{days:0} days" : $"{hours} hours";
    }
}
