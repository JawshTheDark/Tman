using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public sealed record BenchRun(DateTime Time, string Test, string Summary)
{
    [JsonIgnore] public string TimeText => Time.ToString("g");
}

public partial class BenchmarksPage : UserControl, IPage
{
    sealed record DriveChoice(string Label, string Folder)
    {
        public override string ToString() => Label;
    }

    static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "benchmarks.json");

    readonly ObservableCollection<BenchRun> _history;
    readonly Table _cpuTable, _memTable, _diskTable;
    CancellationTokenSource? _cts;

    public BenchmarksPage()
    {
        InitializeComponent();
        Ui.SetupGrid(HistoryList);
        _history = new ObservableCollection<BenchRun>(LoadHistory());
        HistoryList.ItemsSource = _history;
        Ui.SortBy(HistoryList, nameof(BenchRun.Time), ListSortDirection.Descending);

        _cpuTable = new Table(CpuTable, new[] { "", "1 thread", "All threads" }, Bench.Kernels.Select(k => k.Name).ToArray());
        _memTable = new Table(MemTable, null, new[] { "Write bandwidth", "Copy bandwidth", "Tested with" });
        _diskTable = new Table(DiskTable, new[] { "", "Read", "Write" }, new[] { "Random 4K Q32", "Random 4K Q1" });
        SizeChanged += (_, e) => Cards.Columns = e.NewSize.Width > 980 ? 3 : 2;
    }

    public void OnShown()
    {
        var info = StaticInfo.Current;
        CpuNote.Text = $"{(info.CpuName != "" ? info.CpuName : "Processor")} · {Environment.ProcessorCount} threads";
        MemNote.Text = info.InstalledMemory > 0
            ? $"{Fmt.Bytes(info.InstalledMemory)}" + (info.MemorySpeed != "" ? $" · {info.MemorySpeed}" : "")
            : "Bandwidth across all cores, latency on one";
        if (_cts == null) LoadDrives();
    }

    void LoadDrives()
    {
        var previous = (DriveBox.SelectedItem as DriveChoice)?.Folder;
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        var choices = new List<DriveChoice>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                bool isSystem = string.Equals(d.Name, system, StringComparison.OrdinalIgnoreCase);
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                // The system drive's root needs admin to write to, so test inside %TEMP% there.
                choices.Add(new DriveChoice($"{label} ({d.Name.TrimEnd('\\')}) · {Fmt.Bytes(d.AvailableFreeSpace)} free",
                    isSystem ? Path.GetTempPath() : d.RootDirectory.FullName));
            }
            catch { }
        }
        DriveBox.ItemsSource = choices;
        DriveBox.SelectedItem = choices.FirstOrDefault(c => c.Folder == previous) ?? choices.FirstOrDefault();
    }

    void RunAll_Click(object sender, RoutedEventArgs e) => _ = Run(true, true, true);
    void RunCpu_Click(object sender, RoutedEventArgs e) => _ = Run(true, false, false);
    void RunMemory_Click(object sender, RoutedEventArgs e) => _ = Run(false, true, false);
    void RunDisk_Click(object sender, RoutedEventArgs e) => _ = Run(false, false, true);
    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>Dev aid for <c>--snap</c>: runs everything and returns when done.</summary>
    internal Task RunAllAsync() => Run(true, true, true);

    async Task Run(bool cpu, bool memory, bool disk)
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        SetBusy(true);
        StatusText.Foreground = (Brush)FindResource("SubtleBrush");
        var progress = new Progress<string>(s => StatusText.Text = s);
        try
        {
            if (cpu)
            {
                var r = await Task.Run(() => Bench.RunCpu(TimeSpan.FromSeconds(1), progress, ct), ct);
                ShowCpu(r);
                Record("Processor", $"Single-thread {r.SingleScore:#,0} · multi-thread {r.MultiScore:#,0} ({r.Threads} threads, {r.MultiScore / r.SingleScore:0.0}× single)");
            }
            if (memory)
            {
                var r = await Task.Run(() => Bench.RunMemory(TimeSpan.FromSeconds(1.5), progress, ct), ct);
                ShowMemory(r);
                Record("Memory", $"Read {Fmt.Rate(r.ReadBps)} · write {Fmt.Rate(r.WriteBps)} · copy {Fmt.Rate(r.CopyBps)} · latency {r.LatencyNs:0.0} ns");
            }
            if (disk)
            {
                if (DriveBox.SelectedItem is not DriveChoice d) throw new InvalidOperationException("Pick a drive to test.");
                // Task.Run so the I/O completions don't queue up behind the UI thread.
                var r = await Task.Run(() => Bench.RunDisk(d.Folder, TimeSpan.FromSeconds(2), progress, ct), ct);
                ShowDisk(r);
                Record($"Drive {Path.GetPathRoot(d.Folder)?.TrimEnd('\\')}",
                    $"Sequential {Fmt.Rate(r.SeqRead)} read / {Fmt.Rate(r.SeqWrite)} write · 4K Q32 {Fmt.Rate(r.Rnd32Read)} / {Fmt.Rate(r.Rnd32Write)} · 4K Q1 {Fmt.Rate(r.Rnd1Read)} / {Fmt.Rate(r.Rnd1Write)}");
                LoadDrives();
            }
            StatusText.Text = $"Finished at {DateTime.Now:t}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't finish: {ex.Message}";
            StatusText.Foreground = (Brush)FindResource("BadBrush");
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        foreach (var b in new[] { RunAllButton, CpuButton, MemButton, DiskButton }) b.IsEnabled = !busy;
        DriveBox.IsEnabled = !busy;
        CancelButton.Visibility = Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowCpu(Bench.CpuResult r)
    {
        SingleScore.Text = r.SingleScore.ToString("#,0");
        MultiScore.Text = r.MultiScore.ToString("#,0");
        MultiLabel.Text = $"Multi-thread score ({r.MultiScore / r.SingleScore:0.0}×)";
        for (int i = 0; i < r.Kernels.Count; i++)
        {
            var k = r.Kernels[i];
            _cpuTable.Set(i, 0, Rate(k.SingleRate / k.UnitScale), k.Unit);
            _cpuTable.Set(i, 1, Rate(k.MultiRate / k.UnitScale), k.Unit);
        }
    }

    static string Rate(double v) => v < 10 ? v.ToString("0.00") : v < 100 ? v.ToString("0.0") : v.ToString("#,0");

    void ShowMemory(Bench.MemoryResult r)
    {
        MemRead.Text = Fmt.Rate(r.ReadBps);
        MemLatency.Text = $"{r.LatencyNs:0.0} ns";
        _memTable.Set(0, 0, Fmt.Rate(r.WriteBps));
        _memTable.Set(1, 0, Fmt.Rate(r.CopyBps));
        _memTable.Set(2, 0, $"{Fmt.Bytes(r.TestBytes)}, {Environment.ProcessorCount} threads");
    }

    void ShowDisk(Bench.DiskResult r)
    {
        SeqRead.Text = Fmt.Rate(r.SeqRead);
        SeqWrite.Text = Fmt.Rate(r.SeqWrite);
        static string Iops(double bps) => $"{bps / 4096:#,0} IOPS";
        _diskTable.Set(0, 0, Fmt.Rate(r.Rnd32Read), Iops(r.Rnd32Read));
        _diskTable.Set(0, 1, Fmt.Rate(r.Rnd32Write), Iops(r.Rnd32Write));
        _diskTable.Set(1, 0, Fmt.Rate(r.Rnd1Read), Iops(r.Rnd1Read));
        _diskTable.Set(1, 1, Fmt.Rate(r.Rnd1Write), Iops(r.Rnd1Write));
    }

    // ---- History ----

    void Record(string test, string summary)
    {
        _history.Add(new BenchRun(DateTime.Now, test, summary));
        SaveHistory();
    }

    static List<BenchRun> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<BenchRun>>(File.ReadAllText(HistoryPath)) ?? new();
        }
        catch { }
        return new();
    }

    void SaveHistory()
    {
        if (MainWindow.SnapshotMode) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history.ToList()));
        }
        catch { }
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0 || !Ui.Confirm("Delete all saved benchmark results?", "Clear results")) return;
        _history.Clear();
        SaveHistory();
    }

    void CopyHistory_Click(object sender, RoutedEventArgs e) =>
        Ui.Copy(string.Join(Environment.NewLine, HistoryList.SelectedItems.Cast<BenchRun>().Select(r => $"{r.TimeText}\t{r.Test}\t{r.Summary}")));

    /// <summary>Small label/value grid inside a result card.</summary>
    sealed class Table
    {
        readonly TextBlock[,] _cells;

        public Table(Grid grid, string[]? header, string[] rows)
        {
            var app = Application.Current;
            int values = header == null ? 1 : header.Length - 1;
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int c = 0; c < values; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 72 });

            int r = 0;
            if (header != null)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                for (int c = 1; c < header.Length; c++)
                    Add(grid, new TextBlock { Text = header[c], Style = (Style)app.FindResource("StatLabel"), TextAlignment = TextAlignment.Right }, 0, c);
                r = 1;
            }
            _cells = new TextBlock[rows.Length, values];
            for (int i = 0; i < rows.Length; i++, r++)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                Add(grid, new TextBlock { Text = rows[i], TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)app.FindResource("SubtleBrush"), Margin = new Thickness(0, 4, 0, 4) }, r, 0);
                for (int c = 0; c < values; c++)
                {
                    var cell = new TextBlock { Text = "—", TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 4, 0, 4) };
                    _cells[i, c] = cell;
                    Add(grid, cell, r, c + 1);
                }
            }
        }

        static void Add(Grid g, UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        public void Set(int row, int col, string text, string? tip = null)
        {
            _cells[row, col].Text = text;
            _cells[row, col].ToolTip = tip;
        }
    }
}
