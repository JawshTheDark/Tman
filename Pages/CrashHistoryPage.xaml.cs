using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public enum CrashKind { BlueScreen, Shutdown, AppCrash, AppHang }

/// <summary>What the page needs from one event. Records can't outlive the read.</summary>
public sealed record CrashEvent(string Provider, int Id, DateTime Time, object?[] Values)
{
    public string Str(int i) => i < Values.Length ? Values[i]?.ToString()?.Trim() ?? "" : "";

    public ulong Num(int i)
    {
        try { return i < Values.Length ? Convert.ToUInt64(Values[i]) : 0; }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    public bool Flag(int i) => i < Values.Length && Values[i] is true;
}

/// <summary>One line of the timeline: one failure of the whole PC, or one app's repeats of the same fault on one day.</summary>
public sealed class CrashRow
{
    public required CrashKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Explain { get; init; }
    public required string SearchText { get; init; }
    public string App { get; init; } = "";
    public string? File { get; init; }
    public List<(string Label, string Value)> Facts { get; init; } = new();
    public List<DateTime> Times { get; } = new();

    public DateTime Time => Times[0];
    public int Count => Times.Count;
    public string TimeText => Time.ToString("g");
    public string KindText => Kind switch
    {
        CrashKind.BlueScreen => "Blue screen",
        CrashKind.Shutdown => "Unexpected shutdown",
        CrashKind.AppCrash => "App crash",
        _ => "App hang",
    };
    public Brush KindBrush => (Brush)Application.Current.FindResource(Kind switch
    {
        CrashKind.BlueScreen or CrashKind.Shutdown => "BadBrush",
        CrashKind.AppCrash => "WarnBrush",
        _ => "SubtleBrush",
    });
}

public sealed record TopApp(string App, int Total, string Breakdown);

/// <summary>Rows plus a line about anything that limited them. <paramref name="Failed"/> means a log couldn't be read, so an empty list proves nothing.</summary>
public sealed record CrashData(List<CrashRow> Rows, string? Note, bool Failed);

public partial class CrashHistoryPage : UserControl, IPage
{
    const int Cap = 5000;

    /// <summary>Windows logs a failure when it next starts, in up to three events spread over a few minutes.</summary>
    static readonly TimeSpan SameFailure = TimeSpan.FromMinutes(10);

    const string SystemFilter = "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008)"
                                + " or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)";
    const string AppFilter = "(Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)";

    List<CrashRow> _rows = new();
    ICollectionView? _view;
    CancellationTokenSource? _load;
    string _filter = "";
    DateTime _readAt;
    bool _started, _failed;

    public CrashHistoryPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        SizeChanged += (_, _) => FitLayout();
    }

    /// <summary>A narrow window loses the side pane. A short one swaps the tiles for a line, and the headline makes way for an open details pane.</summary>
    void FitLayout()
    {
        bool wide = ActualWidth >= 760, tall = ActualHeight >= 560;
        SideColumn.Width = wide ? new GridLength(250) : new GridLength(0);
        SidePane.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        TopLine.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        Tiles.Visibility = tall ? Visibility.Visible : Visibility.Collapsed;
        CountLine.Visibility = tall ? Visibility.Collapsed : Visibility.Visible;
        HeadPanel.Visibility = !tall && Selected != null ? Visibility.Collapsed : Visibility.Visible;
        DetailsPane.MaxHeight = Math.Max(130, ActualHeight * 0.33);
    }

    /// <summary>People open this page right after something went wrong, so a read from before that is no use.</summary>
    public void OnShown()
    {
        _started = true;
        if (DateTime.Now - _readAt > TimeSpan.FromMinutes(1)) Load();
    }

    public void FocusSearch() => Search.FocusBox();

    DateTime? Since() => RangeBox.SelectedIndex switch
    {
        0 => DateTime.Now.AddDays(-7),
        1 => DateTime.Now.AddDays(-30),
        2 => DateTime.Now.AddDays(-90),
        _ => null,
    };

    string RangeText => RangeBox.SelectedIndex switch
    {
        0 => "the last 7 days",
        1 => "the last 30 days",
        2 => "the last 90 days",
        _ => "the event logs",
    };

    static CrashEvent ToEvent(EventRecord r) =>
        new(r.ProviderName ?? "", r.Id, r.TimeCreated ?? DateTime.MinValue, r.Properties.Select(p => p.Value).ToArray());

    async void Load()
    {
        _load?.Cancel();
        var cancel = (_load = new CancellationTokenSource()).Token;
        var since = Since();
        var data = await Task.Run(() =>
        {
            var system = EventLogs.Read("System", SystemFilter, since, Cap, ToEvent, cancel);
            var apps = EventLogs.Read("Application", AppFilter, since, Cap, ToEvent, cancel);
            string? note = system.Error != null ? $"System log: {system.Error}" : apps.Error != null ? $"Application log: {apps.Error}" : null;
            if (note == null && (system.Capped || apps.Capped)) note = $"Stopped at the newest {Cap:#,0} events in a log, so older ones aren't counted here.";
            return new CrashData(BuildRows(system.Rows, apps.Rows), note, system.Error != null || apps.Error != null);
        });
        if (cancel.IsCancellationRequested) return;
        _readAt = DateTime.Now;
        Show(data);
    }

    public void Show(CrashData data)
    {
        _rows = data.Rows;
        _failed = data.Failed;
        List.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Matches;
        Ui.SortBy(List, nameof(CrashRow.Time), ListSortDirection.Descending);

        int Total(CrashKind k) => _rows.Where(r => r.Kind == k).Sum(r => r.Count);
        SetCount(BlueCount, Total(CrashKind.BlueScreen), "BadBrush");
        SetCount(ShutdownCount, Total(CrashKind.Shutdown), "BadBrush");
        SetCount(CrashCount, Total(CrashKind.AppCrash), "WarnBrush");
        SetCount(HangCount, Total(CrashKind.AppHang), "WarnBrush");
        CountLine.Text = $"{Total(CrashKind.BlueScreen):#,0} blue screens · {Total(CrashKind.Shutdown):#,0} unexpected shutdowns · {Total(CrashKind.AppCrash):#,0} app crashes · {Total(CrashKind.AppHang):#,0} app hangs";

        var top = _rows.Where(r => r.App.Length > 0)
            .GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.Key, Crashes: g.Where(r => r.Kind == CrashKind.AppCrash).Sum(r => r.Count), Hangs: g.Where(r => r.Kind == CrashKind.AppHang).Sum(r => r.Count)))
            .OrderByDescending(a => a.Crashes + a.Hangs).ThenBy(a => a.App, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(a => new TopApp(a.App, a.Crashes + a.Hangs, Breakdown(a.Crashes, a.Hangs)))
            .ToList();
        TopApps.ItemsSource = top;
        TopEmpty.Text = data.Failed ? "Not known, because a log couldn't be read." : "No app crashes or hangs in this period.";
        TopEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var latest = _rows.OrderByDescending(r => r.Time).FirstOrDefault();
        var pcDown = _rows.Where(r => r.Kind is CrashKind.BlueScreen or CrashKind.Shutdown).OrderByDescending(r => r.Time).FirstOrDefault();
        Headline.Text = latest != null ? $"{latest.Title} {When(latest.Time)}."
            : data.Failed ? "Couldn't read the event logs, so this page may be missing problems."
            : $"Nothing went wrong in {RangeText}.";
        Headline.Foreground = latest?.KindBrush ?? (Brush)FindResource(data.Failed ? "WarnBrush" : "GoodBrush");
        var sub = latest == null ? "" : pcDown == null
            ? $"No blue screens or unexpected shutdowns in {RangeText}."
            : pcDown == latest ? "" : $"The whole PC last went down {When(pcDown.Time)}: {pcDown.KindText.ToLowerInvariant()}.";
        SubHeadline.Text = (sub + " " + data.Note).Trim();
        TopLine.Text = top.Count > 0 ? $"Top crashing app: {top[0].App}, {top[0].Breakdown}." : "";
        ApplyFilter();
    }

    void SetCount(TextBlock block, int count, string brush)
    {
        // A zero from a log that couldn't be read would be a false all-clear.
        block.Text = count == 0 && _failed ? "—" : count.ToString("#,0");
        block.Foreground = (Brush)FindResource(count > 0 ? brush : _failed ? "SubtleBrush" : "GoodBrush");
    }

    static string Breakdown(int crashes, int hangs)
    {
        var parts = new List<string>();
        if (crashes > 0) parts.Add(crashes == 1 ? "1 crash" : $"{crashes} crashes");
        if (hangs > 0) parts.Add(hangs == 1 ? "1 hang" : $"{hangs} hangs");
        return string.Join(", ", parts);
    }

    /// <summary>"today at 8:59 PM", or the full date for anything older than yesterday.</summary>
    static string When(DateTime t)
    {
        var day = t.Date == DateTime.Today ? "today" : t.Date == DateTime.Today.AddDays(-1) ? "yesterday" : "on " + t.ToString("D");
        return $"{day} at {t:t}";
    }

    bool Matches(object o)
    {
        var r = (CrashRow)o;
        int kind = KindBox.SelectedIndex;
        if (kind > 0 && (int)r.Kind != kind - 1) return false;
        return _filter.Length == 0 || Ui.Contains(r.Title, _filter) || Ui.Contains(r.Summary, _filter) || Ui.Contains(r.App, _filter);
    }

    void ApplyFilter()
    {
        if (_view == null) return;
        _view.Refresh();
        bool empty = _view.IsEmpty;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        var what = ((ComboBoxItem)KindBox.SelectedItem).Content.ToString()!;
        EmptyText.Text = _filter.Length > 0 ? "Nothing here matches this search."
            : _failed ? "Nothing to show, because Windows wouldn't give Tman the logs."
            : KindBox.SelectedIndex == 0 ? $"No crashes, hangs or unexpected shutdowns in {RangeText}."
            : $"No {what.ToLowerInvariant()} in {RangeText}.";
    }

    /// <summary>Turns raw events into timeline rows, newest first.</summary>
    public static List<CrashRow> BuildRows(List<CrashEvent> system, List<CrashEvent> apps)
    {
        var rows = new List<CrashRow>();
        var dumps = Minidumps();
        foreach (var failure in Failures(system)) rows.Add(PcRow(failure, dumps));

        var repeats = new Dictionary<string, CrashRow>();
        foreach (var e in apps.OrderByDescending(e => e.Time))
        {
            var row = AppRow(e, out var fault);
            if (row == null) continue;
            var key = $"{row.Kind}|{row.App}|{fault}|{e.Time.Date:d}".ToLowerInvariant();
            if (!repeats.TryGetValue(key, out var first))
            {
                repeats[key] = first = row;
                rows.Add(first);
            }
            first.Times.Add(e.Time);
        }
        return rows.OrderByDescending(r => r.Time).ToList();
    }

    /// <summary>Puts together the events one failure left behind. Two events of one type mean two failures.</summary>
    static List<List<CrashEvent>> Failures(List<CrashEvent> system)
    {
        var failures = new List<List<CrashEvent>>();
        foreach (var e in system.OrderBy(e => e.Time))
        {
            var last = failures.LastOrDefault();
            if (last != null && e.Time - last[0].Time < SameFailure && last.All(x => x.Id != e.Id)) last.Add(e);
            else failures.Add(new List<CrashEvent> { e });
        }
        return failures;
    }

    static List<FileInfo> Minidumps()
    {
        try
        {
            var folder = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump"));
            return folder.Exists ? folder.GetFiles("*.dmp").ToList() : new List<FileInfo>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<FileInfo>();
        }
    }

    static CrashRow PcRow(List<CrashEvent> failure, List<FileInfo> dumps)
    {
        var bugCheck = failure.FirstOrDefault(e => e.Id == 1001);
        var power = failure.FirstOrDefault(e => e.Id == 41);
        var dirty = failure.FirstOrDefault(e => e.Id == 6008);
        var restarted = failure.Min(e => e.Time);
        var lastRunning = dirty != null ? LastRunning(dirty) : null;
        var time = lastRunning ?? restarted;

        var facts = new List<(string, string)>();
        if (lastRunning != null) facts.Add(("Windows last running", $"{lastRunning:F}"));
        facts.Add(("Windows started again", $"{restarted:F}"
            + (lastRunning < restarted ? $", {Fmt.Span(restarted - lastRunning.Value)} later" : "")));

        uint stop = 0;
        string parameters = "", reportedDump = "";
        if (bugCheck != null)
        {
            var m = Regex.Match(bugCheck.Str(0), @"0x([0-9a-fA-F]+)\s*(?:\((.*)\))?");
            if (m.Success && ulong.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out var code)) stop = (uint)code;
            parameters = m.Groups[2].Value;
            reportedDump = bugCheck.Str(1);
        }
        else if (power != null)
        {
            stop = (uint)power.Num(0);
        }

        if (stop != 0 || bugCheck != null)
        {
            var (name, meaning) = CrashCodes.Stop(stop);
            var code = stop != 0 ? $"0x{stop:X}" : "not recorded";
            facts.Insert(0, ("Stop code", $"{name} ({code})"));
            if (parameters.Length > 0) facts.Add(("Parameters", parameters));
            var dump = dumps.Where(f => f.LastWriteTime >= time - SameFailure && f.LastWriteTime <= restarted + SameFailure)
                .OrderBy(f => f.LastWriteTime).FirstOrDefault()?.FullName;
            if (dump == null && File.Exists(reportedDump)) dump = reportedDump;
            facts.Add(("Memory dump", dump ?? (reportedDump.Length > 0 ? $"Windows saved one at {reportedDump}, but it's no longer there." : "None found in the Minidump folder.")));
            facts.Add(("Recorded by", string.Join(", ", failure.Select(Source))));
            return new CrashRow
            {
                Kind = CrashKind.BlueScreen,
                Title = $"Blue screen: {name}",
                Summary = $"{name} ({code})",
                Explain = meaning,
                SearchText = stop != 0 ? $"{name} stop code 0x{stop:X} Windows 11" : "blue screen Windows 11",
                File = dump,
                Facts = facts,
                Times = { time },
            };
        }

        string cause;
        if (power != null && (power.Num(6) != 0 || power.Flag(15)))
            cause = "The power button was held down to force the PC off, usually because it had frozen.";
        else if (power != null && power.Num(5) != 0)
            cause = "The PC stopped while it was going to sleep or asleep. Common causes are a driver that failed the change, or a power fault.";
        else
            cause = "Windows stopped with no warning and left no blue screen. Common causes are a power cut, a failing power supply, overheating, or a freeze that ended in a forced restart.";
        facts.Add(("Recorded by", string.Join(", ", failure.Select(Source))));
        return new CrashRow
        {
            Kind = CrashKind.Shutdown,
            Title = "The PC shut down unexpectedly",
            Summary = cause[..(cause.IndexOf('.') + 1)],
            Explain = cause,
            SearchText = "Kernel-Power 41 unexpected shutdown Windows 11",
            Facts = facts,
            Times = { time },
        };
    }

    static string Source(CrashEvent e) => e.Id switch
    {
        41 => "Kernel-Power 41",
        6008 => "EventLog 6008",
        _ => "BugCheck 1001",
    };

    /// <summary>Event 6008 carries the last time Windows was known to be running as a local SYSTEMTIME, which avoids parsing its localized text.</summary>
    static DateTime? LastRunning(CrashEvent dirty)
    {
        if (dirty.Values.OfType<byte[]>().FirstOrDefault() is not { Length: >= 16 } b) return null;
        int At(int i) => BitConverter.ToUInt16(b, i);
        try { return new DateTime(At(0), At(2), At(6), At(8), At(10), At(12), At(14)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static CrashRow? AppRow(CrashEvent e, out string fault)
    {
        fault = "";
        var app = e.Str(0);
        if (app.Length == 0) return null;
        if (e.Id == 1002)
        {
            var path = e.Str(5);
            return new CrashRow
            {
                Kind = CrashKind.AppHang,
                App = app,
                Title = $"{app} stopped responding",
                Summary = $"{app} stopped responding and was closed",
                Explain = "The app stopped responding to Windows and was closed, by you or by Windows.",
                SearchText = $"{app} not responding",
                File = path.Length > 0 ? path : null,
                Facts = new() { ("App version", e.Str(1)), ("App path", path), ("Report ID", e.Str(6)) },
            };
        }

        string module = e.Str(3), rawCode = e.Str(6);
        uint code = uint.TryParse(rawCode.Replace("0x", ""), NumberStyles.HexNumber, null, out var parsed) ? parsed : 0;
        fault = $"{module}|{rawCode}";
        var (name, meaning) = CrashCodes.Exception(code);
        var moduleNote = CrashCodes.Module(module, app);
        var appPath = e.Str(10);
        return new CrashRow
        {
            Kind = CrashKind.AppCrash,
            App = app,
            Title = $"{app} crashed",
            Summary = $"{app}: {name} in {(module.Length > 0 && module != "unknown" ? module : "an unknown module")}",
            Explain = moduleNote.Length > 0 ? $"{meaning} {moduleNote}" : meaning,
            SearchText = $"{app} crash {module} 0x{code:X8}",
            File = appPath.Length > 0 ? appPath : null,
            Facts = new()
            {
                ("Exception", $"{name} (0x{code:X8})"),
                ("Faulting module", $"{module} {e.Str(4)}".Trim()),
                ("Module path", e.Str(11)),
                ("App version", e.Str(1)),
                ("App path", appPath),
                ("Report ID", e.Str(12)),
            },
        };
    }

    CrashRow? Selected => List.SelectedItem as CrashRow;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var s = Selected;
        DetailsPane.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        FitLayout();
        if (s == null) return;
        DetailTitle.Text = s.Title;
        DetailWhen.Text = WhenText(s);
        DetailExplain.Text = s.Explain;
        OpenButton.IsEnabled = File.Exists(s.File);
        DetailFacts.Children.Clear();
        foreach (var (label, value) in s.Facts.Where(f => f.Value.Length > 0))
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            line.Inlines.Add(new Run(label + ": ") { Foreground = (Brush)FindResource("SubtleBrush") });
            line.Inlines.Add(new Run(value));
            DetailFacts.Children.Add(line);
        }
    }

    static string WhenText(CrashRow r)
    {
        if (r.Count == 1) return r.Time.ToString("F");
        var times = r.Times.OrderBy(t => t).ToList();
        var text = $"{r.Count} times on {r.Time:D}: ";
        return text + string.Join(", ", times.Take(30).Select(t => t.ToString("t"))) + (times.Count > 30 ? $" and {times.Count - 30} more" : "");
    }

    void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    void RangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_started) Load();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Ui.SearchOnline(s.SearchText);
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(Selected?.File);

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var text = new StringBuilder($"{s.Title}\n{WhenText(s)}\n{s.Explain}\n");
        foreach (var (label, value) in s.Facts.Where(f => f.Value.Length > 0)) text.Append($"{label}: {value}\n");
        Ui.Copy(text.ToString());
    }
}
