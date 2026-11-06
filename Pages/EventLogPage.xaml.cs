using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public sealed record EventRow(DateTime Time, int Level, string Source, int Id, string Message)
{
    public string TimeText => Time.ToString("g");
    public string LevelText => Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", 5 => "Verbose", _ => "Information" };
    public Brush LevelBrush => (Brush)Application.Current.FindResource(Level switch { 1 or 2 => "BadBrush", 3 => "WarnBrush", _ => "SubtleBrush" });

    /// <summary>The first line, for the list; the details pane has the rest.</summary>
    public string Summary
    {
        get
        {
            var end = Message.IndexOfAny(new[] { '\r', '\n' });
            return end > 0 ? Message[..end] : Message;
        }
    }
}

public partial class EventLogPage : UserControl, IPage
{
    const int Cap = 10000;
    static readonly string[] FirstLogs = { "System", "Application", "Setup", "Security" };

    readonly ObservableCollection<EventRow> _items = new();
    readonly ICollectionView _view;
    CancellationTokenSource? _load;
    string _filter = "";
    bool _started, _loading;

    public EventLogPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = Matches;
        Ui.SortBy(List, nameof(EventRow.Time), ListSortDirection.Descending);
        foreach (var log in FirstLogs)
            LogBox.Items.Add(new ComboBoxItem { Content = log == "Security" && !MainWindow.IsAdmin ? "Security (needs administrator)" : log, Tag = log });
        LogBox.SelectedIndex = 0;
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
            UpdateCount();
        };
        SizeChanged += (_, _) => FitHeight();
    }

    public void OnShown()
    {
        if (_started) return;
        _started = true;
        Load();
        LoadLogNames();
    }

    public void FocusSearch() => Search.FocusBox();

    /// <summary>In a short window, an open details pane takes the room the note would use.</summary>
    void FitHeight()
    {
        if (MainWindow.IsAdmin) return;
        AdminStrip.Visibility = ActualHeight < 480 && Selected != null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void LoadLogNames()
    {
        var logs = await Task.Run(EventLogs.ReadableLogs);
        foreach (var log in logs.Where(n => !FirstLogs.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            LogBox.Items.Add(new ComboBoxItem { Content = log, Tag = log });
    }

    bool Matches(object o)
    {
        var e = (EventRow)o;
        return _filter.Length == 0 || Ui.Contains(e.Source, _filter) || Ui.Contains(e.Message, _filter) || e.Id.ToString() == _filter;
    }

    /// <summary>Empty when every level is on, which keeps the query simplest. Information also covers level 0, used by classic and audit events, and Verbose.</summary>
    string LevelFilter()
    {
        var levels = new List<string>();
        if (CriticalBox.IsChecked == true) levels.Add("Level=1");
        if (ErrorBox.IsChecked == true) levels.Add("Level=2");
        if (WarningBox.IsChecked == true) levels.Add("Level=3");
        if (InfoBox.IsChecked == true) levels.AddRange(new[] { "Level=0", "Level=4", "Level=5" });
        return levels.Count == 6 ? "" : string.Join(" or ", levels);
    }

    bool AnyLevel => CriticalBox.IsChecked == true || ErrorBox.IsChecked == true || WarningBox.IsChecked == true || InfoBox.IsChecked == true;

    DateTime? Since() => RangeBox.SelectedIndex switch
    {
        0 => DateTime.Now.AddHours(-1),
        1 => DateTime.Now.AddDays(-1),
        2 => DateTime.Now.AddDays(-7),
        3 => DateTime.Now.AddDays(-30),
        _ => null,
    };

    static EventRow ToRow(EventRecord r) =>
        new(r.TimeCreated ?? DateTime.MinValue, r.Level ?? 0, (r.ProviderName ?? "").Replace("Microsoft-Windows-", ""), r.Id, EventLogs.Message(r));

    async void Load()
    {
        _load?.Cancel();
        var cancel = (_load = new CancellationTokenSource()).Token;
        _items.Clear();
        NoteText.Visibility = Visibility.Collapsed;
        _loading = false;
        EmptyText.Visibility = Visibility.Collapsed;
        if (!AnyLevel)
        {
            UpdateCount();
            ShowEmpty("Choose at least one level.");
            return;
        }
        _loading = true;
        UpdateCount();
        var log = (string)((ComboBoxItem)LogBox.SelectedItem).Tag;
        var levels = LevelFilter();
        var since = Since();
        IProgress<List<EventRow>> progress = new Progress<List<EventRow>>(batch =>
        {
            if (cancel.IsCancellationRequested) return;
            AddRows(batch);
            UpdateCount();
        });
        var result = await Task.Run(() => EventLogs.Read(log, levels, since, Cap, ToRow, cancel, progress.Report));
        if (cancel.IsCancellationRequested) return;
        _loading = false;
        var note = result.Capped ? $"Stopped at the limit of {Cap:#,0} events, so older ones aren't shown or searched. Narrow the time range or levels to see them."
            : _items.Count > 0 ? result.Error : null;
        NoteText.Text = note;
        NoteText.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
        if (_items.Count == 0)
            ShowEmpty(result.Error ?? (since == null && levels.Length == 0 ? "This log is empty." : "No events in this log match these levels and this time range."));
    }

    /// <summary>WPF takes about 2 ms to group a row as it's added, so a batch goes in with the groups off and they're rebuilt once.</summary>
    void AddRows(List<EventRow> rows)
    {
        var groups = _view.GroupDescriptions.ToList();
        if (groups.Count > 0) _view.GroupDescriptions.Clear();
        foreach (var e in rows) _items.Add(e);
        foreach (var g in groups) _view.GroupDescriptions.Add(g);
    }

    void ShowEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = Visibility.Visible;
    }

    void UpdateCount()
    {
        if (_items.Count == 0)
        {
            CountText.Text = _loading ? "Reading the log…" : "";
            return;
        }
        var shown = _view.Cast<EventRow>().ToList();
        int errors = shown.Count(e => e.Level is 1 or 2), warnings = shown.Count(e => e.Level == 3);
        var text = shown.Count == _items.Count ? $"{_items.Count:#,0} events" : $"{shown.Count:#,0} of {_items.Count:#,0} events";
        if (errors > 0) text += $" · {errors:#,0} {(errors == 1 ? "error" : "errors")}";
        if (warnings > 0) text += $" · {warnings:#,0} {(warnings == 1 ? "warning" : "warnings")}";
        CountText.Text = text + (_loading ? " · reading…" : "");
        if (shown.Count == 0) ShowEmpty("No events match this search.");
        else EmptyText.Visibility = Visibility.Collapsed;
    }

    void Query_Changed(object sender, RoutedEventArgs e)
    {
        if (_started && LogBox.SelectedItem != null) Load();
    }

    /// <summary>A GroupStyle left on a DataGrid squashes its columns even with no groups, so it's only there while grouping is on.</summary>
    void Group_Click(object sender, RoutedEventArgs e)
    {
        _view.GroupDescriptions.Clear();
        List.GroupStyle.Clear();
        if (GroupBox.IsChecked != true) return;
        List.GroupStyle.Add((GroupStyle)FindResource("BySource"));
        var bySource = new PropertyGroupDescription(nameof(EventRow.Source));
        bySource.CustomSort = PropertyGroupDescription.CompareNameAscending;
        _view.GroupDescriptions.Add(bySource);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void OpenViewer_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error($"Couldn't open Event Viewer:\n\n{ex.Message}"); }
    }

    EventRow? Selected => List.SelectedItem as EventRow;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var s = Selected;
        DetailsPane.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        DetailRow.Height = s == null ? GridLength.Auto : new GridLength(2, GridUnitType.Star);
        ListRow.Height = new GridLength(3, GridUnitType.Star);
        FitHeight();
        if (s == null) return;
        DetailTitle.Text = $"{s.LevelText}: {s.Source}, event {s.Id}";
        DetailSub.Text = s.Time.ToString("F");
        DetailMessage.Text = s.Message;
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Ui.SearchOnline($"{s.Source} event {s.Id}");
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Ui.Copy($"{s.LevelText}: {s.Source}, event {s.Id}\n{s.Time:F}\n\n{s.Message}");
    }
}
