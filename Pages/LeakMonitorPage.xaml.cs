using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

/// <summary>One grid row: a recorder result with the text the columns show.</summary>
public sealed class LeakGridRow
{
    public LeakGridRow(LeakRow row) => Row = row;

    public LeakRow Row { get; }
    public string Name => Row.Name;
    public string Id => Row.Id;
    public string Detail => Row.Detail;
    public int Rank => (int)Row.Verdict;
    public string VerdictText => LeakAnalysis.Name(Row.Verdict);
    public Brush VerdictBrush => (Brush)Application.Current.FindResource(Row.Verdict switch
    {
        LeakVerdict.Leak => "BadBrush",
        LeakVerdict.Suspect => "WarnBrush",
        LeakVerdict.Stable => "GoodBrush",
        _ => "SubtleBrush",
    });

    public double Now => Row.Memory.Last;
    public double Growth => Row.Memory.Growth;
    public double Rate => Row.Memory.PerHour;
    public double HandlesNow => Row.Handles?.Last ?? 0;
    public double HandleGrowth => Row.Handles?.Growth ?? 0;
    public string NowText => Fmt.Bytes(Now);
    public string GrowthText => LeakRecorder.Signed(Growth);
    public string RateText => Row.Memory.Hours > 0 ? LeakRecorder.Signed(Rate) : "";
    public string HandlesText => Row.Handles is { } h ? h.Last.ToString("N0") : "";
    public string HandleGrowthText => Row.Handles is { } h ? $"{h.Growth:+#,0;-#,0;0}" : "";
}

public partial class LeakMonitorPage : UserControl, IPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    LeakReport? _report;
    string _filter = "";
    bool _loading, _driversListed, _testing;

    const int ZombieTab = 2;

    public LeakMonitorPage()
    {
        InitializeComponent();
        Ui.SetupGrid(AppsGrid);
        Ui.SetupGrid(TagsGrid);
        Ui.SetupGrid(HoldersGrid);
        Ui.SetupGrid(DriversGrid);
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ShowRows();
        };
        _timer.Tick += async (_, _) => await Reload();
        OpenLogButton.IsEnabled = File.Exists(LeakScan.LogPath);
    }

    public async void OnShown()
    {
        _timer.Start();
        await Reload();
    }

    public void OnHidden() => _timer.Stop();
    public void FocusSearch() => Search.FocusBox();

    /// <summary>Dev aid for <c>--snap</c>: fills the page with made-up history.</summary>
    internal async Task ShowSample()
    {
        LeakRecorder.Instance.LoadSample();
        while (_loading) await Task.Delay(20);
        await Reload();
    }

    double? Hours => (Period.SelectedItem as ComboBoxItem)?.Tag is string hours ? double.Parse(hours) : null;

    async Task Reload()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var hours = Hours;
            _report = await Task.Run(() => LeakRecorder.Instance.Build(hours));
            ShowReport(_report);
        }
        finally
        {
            _loading = false;
        }
    }

    void ShowReport(LeakReport r)
    {
        Headline.Text = r.Headline;
        Headline.Foreground = (Brush)FindResource(r.Worst switch { LeakVerdict.Leak => "BadBrush", LeakVerdict.Suspect => "WarnBrush", _ => "GoodBrush" });
        if (r.Worst == LeakVerdict.Watching) Headline.Foreground = (Brush)FindResource("LabelBrush");
        Detail.Text = r.Detail + " Tman only records while it's running, including from the tray.";
        Summary.Text = r.Started is { } started ? $"Since {started:g} · {r.Samples:N0} sample{(r.Samples == 1 ? "" : "s")}" : "";
        ShowRows();
    }

    IEnumerable<LeakRow> Visible(IEnumerable<LeakRow> rows) =>
        _filter.Length == 0 ? rows : rows.Where(r => Ui.Contains(r.Name, _filter) || Ui.Contains(r.Detail, _filter) || Ui.Contains(r.Id, _filter));

    void ShowRows()
    {
        if (_report == null) return;
        Fill(AppsGrid, Visible(ByProcess.IsChecked == true ? _report.Processes : _report.Apps), AppsEmpty, "Nothing to show yet. Apps appear after their first sample.");
        Fill(TagsGrid, Visible(_report.Tags), TagsEmpty, _report.HasPoolTags ? "Nothing to show yet." : "Windows wouldn't give Tman the list of kernel pools. Try restarting Tman as administrator.");
        ShowChart();
    }

    static void Fill(DataGrid grid, IEnumerable<LeakRow> rows, TextBlock empty, string emptyText)
    {
        var selected = (grid.SelectedItem as LeakGridRow)?.Row;
        var items = rows.Select(r => new LeakGridRow(r)).ToList();
        grid.ItemsSource = items;
        grid.SelectedItem = items.FirstOrDefault(i => i.Row.Name == selected?.Name && i.Row.Id == selected?.Id) ?? items.FirstOrDefault();
        empty.Text = emptyText;
        empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    DataGrid CurrentGrid => Tabs.SelectedIndex == 1 ? TagsGrid : AppsGrid;

    void ShowChart()
    {
        FindDriverButton.IsEnabled = TagsGrid.SelectedItem != null;
        if (Tabs.SelectedIndex == ZombieTab && _report != null)
        {
            ChartTitle.Text = "Zombie processes over time";
            ChartWhy.Text = _report.Zombies.Points.Count < 2 ? "A trend appears after a few samples." : LeakAnalysis.Explain(_report.Zombies.Memory);
            Chart.Show(_report.Zombies.Points, _report.Zombies.Memory, v => v.ToString("N0"));
        }
        else if (CurrentGrid.SelectedItem is LeakGridRow { Row: var row } item)
        {
            ChartTitle.Text = $"{row.Name} · {item.VerdictText}";
            var why = row.Verdict > row.Memory.Verdict ? "Its memory looks fine, but its handles or threads are climbing, which is a leak of a different kind." : LeakAnalysis.Explain(row.Memory);
            ChartWhy.Text = why;
            Chart.Show(row.Points, row.Memory, v => Fmt.Bytes(v));
        }
        else if (_report != null)
        {
            ChartTitle.Text = "Total commit";
            ChartWhy.Text = LeakAnalysis.Explain(_report.Commit.Memory);
            Chart.Show(_report.Commit.Points, _report.Commit.Memory, v => Fmt.Bytes(v));
        }
    }

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowChart();

    async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        ShowChart();
        if (Tabs.SelectedIndex == ZombieTab) await LoadZombies();
    }

    async Task LoadZombies()
    {
        var state = await Task.Run(ZombieCheck.Read);
        if (!_driversListed)
        {
            DriversGrid.ItemsSource = await Task.Run(ZombieCheck.ThirdPartyDrivers);
            _driversListed = true;
        }
        if (state == null)
        {
            ZombieTitle.Text = "Windows wouldn't give Tman the kernel's process count";
            ZombieText.Text = "Try restarting Tman as administrator.";
            return;
        }
        HoldersGrid.ItemsSource = state.Holders;
        ZombieTitle.Text = $"{state.Zombies:N0} zombie processes: {state.KernelProcesses:N0} process objects in the kernel, {state.Running:N0} running";
        ZombieText.Text = state.Zombies < 200
            ? "That's normal. Windows keeps a few for a moment after a process exits."
            : state.ProcessHandles >= state.Zombies
                ? $"Apps hold {state.ProcessHandles:N0} process handles, enough to explain them. Check the apps at the top of the list on the left."
                : $"Apps hold only {state.ProcessHandles:N0} process handles, too few to explain them, so a driver is the likely cause. Turn off one suspect at a time, then press Test process exits. When exited processes leave memory, you've found it.";
    }

    async void TestExits_Click(object sender, RoutedEventArgs e)
    {
        if (_testing) return;
        _testing = true;
        TestButton.IsEnabled = false;
        ChartWhy.Text = "Testing. This takes about 10 seconds.";
        try
        {
            var result = await Task.Run(() => ZombieCheck.TestExits());
            await LoadZombies();
            ZombieText.Text = result == null ? "The test couldn't read the kernel's count."
                : result.Leaking ? $"Exits are leaking: Tman started {result.Started} short processes and about {result.Stayed} stayed in memory after exiting. Whatever is causing this is still active."
                : $"Exits look clean: Tman started {result.Started} short processes and about {result.Stayed} stayed. If that's new, the last thing you turned off was the cause. Zombies already stuck stay until Windows restarts.";
            ShowChart();
        }
        finally
        {
            _testing = false;
            TestButton.IsEnabled = true;
        }
    }

    void ByProcess_Click(object sender, RoutedEventArgs e) => ShowRows();

    async void Period_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await Reload();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_testing) return;
        _testing = true;
        ScanButton.IsEnabled = TestButton.IsEnabled = false;
        ScanText.Visibility = Visibility.Visible;
        try
        {
            void Status(string text) => Dispatcher.BeginInvoke(() => ScanText.Text = text);
            var result = await Task.Run(() => LeakScan.Run(Status));
            ScanText.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            ScanText.Text = $"Scan saved at {DateTime.Now:t}: {result.Conclusion}";
            OpenLogButton.IsEnabled = true;
            await Reload();
            if (Tabs.SelectedIndex == ZombieTab) await LoadZombies();
        }
        catch (Exception ex)
        {
            ScanText.Text = $"The scan failed: {ex.Message}";
        }
        finally
        {
            _testing = false;
            ScanButton.IsEnabled = TestButton.IsEnabled = true;
        }
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(LeakScan.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't open the log:\n\n{ex.Message}");
        }
    }

    async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Forget the leak history and start watching from now?\n\nFinding a slow leak can take hours of history.", "Leak monitor")) return;
        LeakRecorder.Instance.Reset();
        await Reload();
    }

    async void FindDriver_Click(object sender, RoutedEventArgs e)
    {
        if (TagsGrid.SelectedItem is not LeakGridRow { Row: var row }) return;
        FindDriverButton.IsEnabled = false;
        ChartWhy.Text = $"Searching loaded drivers for {row.Name}…";
        try
        {
            var found = await Task.Run(() => PoolTagFinder.Find(row.Name));
            ChartWhy.Text = found.Count == 0
                ? $"No loaded driver contains {row.Name}. Some tags are built at run time, so this doesn't rule anything out."
                : $"{row.Name} appears in: {string.Join(", ", found)}. A short tag can match by chance, so treat this as a lead. Updating that driver is the usual fix.";
        }
        catch (Exception ex)
        {
            ChartWhy.Text = $"The search failed: {ex.Message}";
        }
        FindDriverButton.IsEnabled = true;
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        var dialog = new SaveFileDialog { FileName = "tman-leaks.csv", Filter = "CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return;
        var csv = new StringBuilder("Type,Name,Id,Verdict,Now (bytes),Growth (bytes),Per hour (bytes),Hours watched,Fit\r\n");
        void Add(string type, IEnumerable<LeakRow> rows)
        {
            foreach (var r in rows)
                csv.Append($"{type},\"{r.Name.Replace("\"", "\"\"")}\",\"{r.Id}\",{LeakAnalysis.Name(r.Verdict)},{r.Memory.Last:0},{r.Memory.Growth:0},{r.Memory.PerHour:0},{r.Memory.Hours:0.00},{r.Memory.Fit:0.00}\r\n");
        }
        Add("App", _report.Apps);
        Add("Process", _report.Processes);
        Add("Kernel pool", _report.Tags);
        try
        {
            File.WriteAllText(dialog.FileName, csv.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error($"Couldn't save the file:\n\n{ex.Message}");
        }
    }
}
