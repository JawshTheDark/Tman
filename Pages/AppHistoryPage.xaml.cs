using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

/// <summary>One row in App history, refreshed in place from its <see cref="AppRecord"/>.</summary>
public sealed class AppRow : ObservableObject
{
    public AppRow(AppRecord r)
    {
        Record = r;
        _icon = r.Icon;
        Refresh();
    }

    public AppRecord Record { get; }
    public string Exe => Record.Exe;
    public string? Path => Record.Path;

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    string _name = "";
    public string Name { get => _name; private set => Set(ref _name, value); }

    long _cpu;
    public long CpuTime { get => _cpu; private set { if (Set(ref _cpu, value)) Raise(nameof(CpuText)); } }
    public string CpuText => Fmt.Duration(TimeSpan.FromTicks(_cpu));

    double _gpu;
    public double GpuSeconds { get => _gpu; private set { if (Set(ref _gpu, value)) Raise(nameof(GpuText)); } }
    public string GpuText => _gpu < 1 ? "" : Fmt.Duration(TimeSpan.FromSeconds(_gpu));

    long _io;
    public long IoBytes { get => _io; private set { if (Set(ref _io, value)) Raise(nameof(IoText)); } }
    public string IoText => Fmt.Bytes(_io);

    ulong _peak;
    public ulong PeakMemory { get => _peak; private set { if (Set(ref _peak, value)) Raise(nameof(PeakText)); } }
    public string PeakText => Fmt.Bytes(_peak);

    int _launches;
    public int Launches { get => _launches; private set => Set(ref _launches, value); }

    bool _running;
    public bool IsRunning { get => _running; private set => Set(ref _running, value); }

    DateTime _last;
    public DateTime LastSeen { get => _last; private set => Set(ref _last, value); }
    public string LastText => _running ? "Running" : Fmt.Ago(_last);

    public void Refresh()
    {
        Name = Record.Name;
        CpuTime = Record.CpuTime;
        GpuSeconds = Record.GpuSeconds;
        IoBytes = Record.IoBytes;
        PeakMemory = Record.PeakMemory;
        Launches = Record.Launches;
        IsRunning = Record.Running > 0;
        LastSeen = Record.LastSeen;
        Raise(nameof(LastText)); // "5 min ago" ages even when nothing else changes
        Icon ??= Record.Icon;
    }
}

public partial class AppHistoryPage : UserControl, IPage
{
    readonly ObservableCollection<AppRow> _rows = new();
    readonly Dictionary<AppRecord, AppRow> _map = new();
    readonly ListCollectionView _view;
    string _filter = "";
    int _version = -1;
    bool _visible;

    public AppHistoryPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _rows;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.IsLiveSorting = true;
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(AppRow.IsRunning));
        _view.Filter = o => Matches((AppRow)o);
        Ui.SortBy(List, nameof(AppRow.CpuTime), ListSortDirection.Descending);
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
            UpdateCount();
        };
        SystemMonitor.Instance.Updated += _ => { if (_visible) Sync(); };
    }

    public void OnShown()
    {
        _visible = true;
        Sync();
    }

    public void OnHidden() => _visible = false;

    public void FocusSearch() => Search.FocusBox();

    bool Matches(AppRow r) =>
        (RunningOnly.IsChecked != true || r.IsRunning)
        && (_filter.Length == 0 || Ui.Contains(r.Name, _filter) || Ui.Contains(r.Exe, _filter) || Ui.Contains(r.Path, _filter));

    void Sync()
    {
        var usage = AppUsage.Instance;
        if (_version != usage.Version)
        {
            _rows.Clear();
            _map.Clear();
            _version = usage.Version;
        }
        foreach (var r in usage.Apps)
        {
            if (_map.TryGetValue(r, out var row))
            {
                row.Refresh();
                continue;
            }
            row = new AppRow(r);
            _map[r] = row;
            _rows.Add(row);
            if (row.Icon == null) LoadIcon(row);
        }
        SinceText.Text = $"Resource usage since {usage.Since:g}. Tman records apps while it's running, so time when it's closed isn't counted.";
        UpdateCount();
    }

    static async void LoadIcon(AppRow row)
    {
        var path = row.Path;
        row.Icon = await Task.Run(() => Icons.ForFile(path));
    }

    void UpdateCount()
    {
        int running = _rows.Count(r => r.IsRunning);
        CountText.Text = (_view.Count == _rows.Count ? $"{_rows.Count} apps" : $"{_view.Count} of {_rows.Count} apps") + $" · {running} running";
    }

    void RunningOnly_Click(object sender, RoutedEventArgs e)
    {
        _view.Refresh();
        UpdateCount();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Delete all recorded app history and start counting from now?", "Delete usage history")) return;
        AppUsage.Instance.Clear();
        Sync();
    }

    AppRow? Selected => List.SelectedItem as AppRow;

    void OpenLocation_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(Selected?.Path);

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) Ui.SearchOnline(r.Exe);
    }

    void Properties_Click(object sender, RoutedEventArgs e) => Ui.ShowProperties(Selected?.Path);

    void Copy_Click(object sender, RoutedEventArgs e) =>
        Ui.Copy(string.Join(Environment.NewLine, List.SelectedItems.Cast<AppRow>()
            .Select(r => $"{r.Name}\t{r.Exe}\t{r.CpuText}\t{r.GpuText}\t{r.IoText}\t{r.PeakText}\t{r.Launches}\t{r.LastText}\t{r.Path}")));
}
