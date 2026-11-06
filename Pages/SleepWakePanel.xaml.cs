using System.Diagnostics.Eventing.Reader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public sealed record SleepRow(DateTime Slept, DateTime Woke, string Source)
{
    public string SleptText => Slept.ToString("g");
    public string WokeText => Woke.ToString("g");
    public string AsleepText => Fmt.Span(Woke - Slept);
}

/// <summary>Read-only sleep and wake facts for the Power page: what woke the PC, what can wake it, and what's stopping it sleeping.</summary>
public partial class SleepWakePanel : UserControl
{
    const int HistoryRows = 10;
    const int RefreshSeconds = 30;
    const string AdminLine = "Needs administrator rights.";
    const string Filter = "Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1";

    sealed record Data(string? LastWake, EventLogs.Result<SleepRow> History, Cmd.Result? Timers, Cmd.Result? Requests, Cmd.Result Devices);

    DateTime _loaded;
    bool _loading;

    public SleepWakePanel() => InitializeComponent();

    /// <summary>Called when the Power page is shown. Shelling out to powercfg isn't free, so a quick return to the page reuses what's there.</summary>
    public async void Refresh()
    {
        if (_loading || (DateTime.Now - _loaded).TotalSeconds < RefreshSeconds) return;
        _loading = true;
        var data = await Task.Run(Read);
        _loading = false;
        _loaded = DateTime.Now;
        Show(data);
    }

    static Data Read()
    {
        var powercfg = Cmd.System("powercfg.exe");
        var lastWake = Task.Run(() => Cmd.Run(powercfg, "/lastwake", 10000));
        var devices = Task.Run(() => Cmd.Run(powercfg, "/devicequery wake_armed", 10000));
        var timers = MainWindow.IsAdmin ? Task.Run(() => Cmd.Run(powercfg, "/waketimers", 10000)) : null;
        var requests = MainWindow.IsAdmin ? Task.Run(() => Cmd.Run(powercfg, "/requests", 10000)) : null;
        var history = EventLogs.Read("System", Filter, DateTime.Now.AddDays(-180), HistoryRows, ToRow);
        return new Data(ParseLastWake(lastWake.Result.Output), history, timers?.Result, requests?.Result, devices.Result);
    }

    static SleepRow? ToRow(EventRecord r)
    {
        var values = r.Properties.Select(p => p.Value).ToArray();
        if (values.Length < 2 || values[0] is not DateTime slept || values[1] is not DateTime woke) return null;
        return new SleepRow(slept, woke, SourceFrom(EventLogs.Message(r)));
    }

    /// <summary>The event's last line is "Wake Source: Power Button", in the system language. The number in its data has no published meaning.</summary>
    static string SourceFrom(string message)
    {
        var last = Lines(message).LastOrDefault() ?? "";
        var colon = last.IndexOf(':');
        return colon >= 0 && colon + 1 < last.Length ? last[(colon + 1)..].Trim() : "Unknown";
    }

    static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>
    /// Wake history from powercfg ends with the first source's facts, one per line after "Wake Source [0]". They differ
    /// by source (device, button, timer), and the labels are in the system language, so this keeps the useful values.
    /// Null when Windows has no wake recorded since it started.
    /// </summary>
    public static string? ParseLastWake(string output)
    {
        var lines = Lines(output).ToList();
        var start = lines.FindLastIndex(l => l.Contains("[0]"));
        if (start < 0) return null;
        var values = lines.Skip(start + 1)
            .Where(l => !l.Contains('\\'))
            .Where(l => !l.EndsWith(':'))
            .Select(l => l.Contains(": ") ? l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim() : l)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count > 0 ? string.Join(" · ", values) : null;
    }

    /// <summary>
    /// powercfg /requests lists sections such as "DISPLAY:", each with "None." or entries that start with "[DRIVER]",
    /// "[PROCESS]" or "[SERVICE]", each followed by its reason. "None." is translated; the brackets aren't.
    /// </summary>
    public static List<string> ParseRequests(string output)
    {
        var found = new List<string>();
        string section = "";
        bool inEntry = false;
        foreach (var line in Lines(output))
        {
            if (line.EndsWith(':') && !line.Contains(' '))
            {
                section = line.TrimEnd(':');
                inEntry = false;
            }
            else if (line.StartsWith('['))
            {
                found.Add($"{SectionName(section)}: {line}");
                inEntry = true;
            }
            else if (inEntry)
            {
                found[^1] += $" ({line})";
            }
        }
        return found;
    }

    static string SectionName(string section) => section.ToUpperInvariant() switch
    {
        "DISPLAY" => "Keeping the screen on",
        "SYSTEM" => "Stopping sleep",
        "AWAYMODE" => "Away mode",
        "EXECUTION" => "Keeping a program running",
        "PERFBOOST" => "Asking for extra speed",
        "ACTIVELOCKSCREEN" => "Keeping the lock screen on",
        _ => section,
    };

    void Show(Data d)
    {
        var rows = d.History.Rows;
        var latest = rows.FirstOrDefault();
        ShowLastWake(d.LastWake, latest);

        History.ItemsSource = rows;
        HistoryHeader.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryNote.Text = rows.Count > 0 ? (d.History.Capped ? $"Newest {rows.Count}" : "Last 180 days") : "";
        HistoryEmpty.Text = d.History.Error ?? "This PC hasn't slept in the last 180 days, or Windows has cleared the record.";
        HistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Fill(TimerList, d.Timers == null ? new[] { AdminLine } : Lines(d.Timers.Output).ToArray(), "Windows reported no wake timers.");
        if (d.Requests == null) Fill(AwakeList, new[] { AdminLine }, "");
        else if (!d.Requests.Ok) Fill(AwakeList, Lines(d.Requests.Output).Take(1).ToArray(), "Windows didn't answer.");
        else Fill(AwakeList, ParseRequests(d.Requests.Output).ToArray(), "Nothing is keeping the PC awake.");

        // powercfg prints NONE when no device is armed; it's only ever that one word, so there's no language to worry about here.
        var devices = Lines(d.Devices.Output).Where(l => !l.Equals("NONE", StringComparison.OrdinalIgnoreCase)).ToArray();
        Fill(DeviceList, d.Devices.Ok ? devices : Lines(d.Devices.Output).Take(1).ToArray(), "No devices are allowed to wake this PC.");
    }

    void ShowLastWake(string? lastWake, SleepRow? latest)
    {
        WakeSource.Text = lastWake ?? latest?.Source ?? "No wake on record";
        WakeWhen.Text = latest != null
            ? $"Woke {latest.Woke:F}, after {latest.AsleepText} asleep."
            : "Windows records this when the PC wakes from sleep, and it hasn't in the last 180 days.";
    }

    void Fill(StackPanel list, string[] lines, string whenEmpty)
    {
        list.Children.Clear();
        if (lines.Length == 0)
        {
            list.Children.Add(Line(whenEmpty, subtle: true));
            return;
        }
        foreach (var l in lines) list.Children.Add(Line(l, subtle: l == AdminLine));
    }

    TextBlock Line(string text, bool subtle)
    {
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        if (subtle) line.Foreground = (Brush)FindResource("SubtleBrush");
        return line;
    }
}
