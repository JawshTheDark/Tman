using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public partial class FlightRecorderPage : UserControl, IPage
{
    static FlightRecorder Recorder => FlightRecorder.Instance;

    readonly ObservableCollection<FlightEvent> _events = new();
    readonly ListCollectionView _eventView;
    FlightFrame? _shown;
    long _lastSeq;
    bool _visible, _pickingFromList;

    public FlightRecorderPage()
    {
        InitializeComponent();
        Ui.SetupGrid(MomentList);
        Ui.SetupGrid(EventList);
        EventList.ItemsSource = _events;
        _eventView = (ListCollectionView)CollectionViewSource.GetDefaultView(_events);
        _eventView.Filter = o => Matches((FlightEvent)o);
        Ui.SortBy(EventList, nameof(FlightEvent.Time), ListSortDirection.Descending);
        _eventView.SortDescriptions.Add(new SortDescription(nameof(FlightEvent.Seq), ListSortDirection.Descending));

        Chart.Frames = Recorder.Frames;
        Chart.Events = Recorder.Events;
        Chart.Picked += t => Pick(t, keepInView: false);
        SystemMonitor.Instance.Updated += _ => { if (_visible) Refresh(); };
    }

    public void OnShown()
    {
        _visible = true;
        Refresh();
    }

    public void OnHidden() => _visible = false;

    bool Matches(FlightEvent e) => EventFilter.SelectedIndex switch
    {
        1 => e.IsSpike,
        2 => e.Kind == FlightEventKind.Started,
        3 => e.Kind == FlightEventKind.Exited,
        _ => true,
    };

    void Refresh()
    {
        // Mirror the recorder's event list: append what's new, drop what aged out of the window.
        var events = Recorder.Events;
        int i = events.Count - 1;
        while (i >= 0 && events[i].Seq > _lastSeq) i--;
        for (int k = i + 1; k < events.Count; k++) _events.Add(events[k]);
        if (events.Count > 0) _lastSeq = Math.Max(_lastSeq, events[^1].Seq);
        long oldest = events.Count > 0 ? events[0].Seq : long.MaxValue;
        while (_events.Count > 0 && _events[0].Seq < oldest) _events.RemoveAt(0);

        var frames = Recorder.Frames;
        StatusText.Text = frames.Count == 0
            ? "Waiting for the first sample…"
            : $"Recording since {Recorder.Started:T} · {Fmt.Span(frames[^1].Time - frames[0].Time)} kept";
        EventCount.Text = $"{_eventView.Count} in the last hour";
        RedrawChart();
        UpdateMoment();
    }

    void RedrawChart()
    {
        TimelinePanel.HeaderRight = (Chart.End == null ? "LIVE" : "FROZEN") + $"  ·  last {Chart.Span.TotalMinutes:0} min";
        Chart.Redraw();
    }

    void UpdateMoment()
    {
        var frame = Chart.Selected is { } t ? Recorder.At(t) : Recorder.Frames.LastOrDefault();
        if (frame == null)
        {
            MomentTitle.Text = "Now";
            MomentStats.Text = "";
            return;
        }
        MomentTitle.Text = Chart.Selected == null ? "Now" : $"At {frame.Time:T}  ({Fmt.Span(DateTime.Now - frame.Time)} ago)";
        if (frame == _shown) return;
        _shown = frame;
        var parts = new List<string>
        {
            $"CPU {frame.Cpu:0}%",
            $"Memory {frame.Memory:0}%",
            $"GPU {frame.Gpu:0}%",
            $"Disk {Fmt.Rate(frame.DiskBps)}",
            $"Network {Fmt.Bits(frame.NetBps)}",
        };
        if (frame.Ghz > 0) parts.Add($"{frame.Ghz:0.00} GHz");
        if (frame.Watts > 0) parts.Add(Fmt.Watts(frame.Watts));
        MomentStats.Text = string.Join("  ·  ", parts);
        MomentList.ItemsSource = frame.Top.OrderByDescending(p => p.Cpu).ToList();
    }

    /// <summary>Selects a moment and freezes the view so it doesn't scroll away.</summary>
    void Pick(DateTime t, bool keepInView)
    {
        var frames = Recorder.Frames;
        if (frames.Count == 0) return;
        var frame = Recorder.At(t)!;
        Chart.Selected = frame.Time;
        var end = Chart.End ?? Chart.ViewEnd;
        if (keepInView && (frame.Time < end - Chart.Span || frame.Time > end))
            end = Min(frames[^1].Time, frame.Time + Chart.Span / 2);
        Chart.End = end;
        LiveButton.Visibility = Visibility.Visible;
        if (!_pickingFromList) EventList.SelectedItem = null;
        RedrawChart();
        UpdateMoment();
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    void Live_Click(object sender, RoutedEventArgs e)
    {
        Chart.End = null;
        Chart.Selected = null;
        LiveButton.Visibility = Visibility.Collapsed;
        EventList.SelectedItem = null;
        Refresh();
    }

    void Range_Click(object sender, RoutedEventArgs e)
    {
        Chart.Span = TimeSpan.FromMinutes(int.Parse((string)((RadioButton)sender).Tag));
        if (Chart.Selected is { } sel && Recorder.Frames.Count > 0)
            Chart.End = Min(Recorder.Frames[^1].Time, sel + Chart.Span / 2);
        RedrawChart();
    }

    void EventFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_eventView == null) return; // fires during InitializeComponent
        _eventView.Refresh();
        EventCount.Text = $"{_eventView.Count} in the last hour";
    }

    void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is not FlightEvent ev) return;
        _pickingFromList = true;
        try { Pick(ev.Time, keepInView: true); }
        finally { _pickingFromList = false; }
    }

    void CopyEvent_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is FlightEvent ev) Ui.Copy($"{ev.Time:G}\t{ev.KindText}\t{ev.Title}\t{ev.Detail}");
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export flight recording",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"tman-flight-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var sb = new StringBuilder("Time,CPU %,Memory %,Memory used (bytes),GPU %,Disk (bytes/s),Network (bytes/s),Clock (GHz),CPU package (W),Top CPU process,Top CPU %,Top memory process,Top memory (bytes)\r\n");
            foreach (var f in Recorder.Frames)
            {
                var cpu = f.Top.MaxBy(p => p.Cpu);
                var mem = f.Top.MaxBy(p => p.Memory);
                sb.Append(string.Join(",", f.Time.ToString("yyyy-MM-dd HH:mm:ss"), f.Cpu.ToString("0.0"), f.Memory.ToString("0.0"), f.MemoryUsed,
                    f.Gpu.ToString("0.0"), f.DiskBps.ToString("0"), f.NetBps.ToString("0"), f.Ghz.ToString("0.00"), f.Watts.ToString("0.0"),
                    Csv(cpu?.Name), cpu?.Cpu.ToString("0.0") ?? "", Csv(mem?.Name), mem?.Memory.ToString() ?? "")).Append("\r\n");
            }
            File.WriteAllText(dlg.FileName, sb.ToString());

            var events = new StringBuilder("Time,Event,What,Details,Ended\r\n");
            foreach (var ev in Recorder.Events.OrderBy(x => x.Time))
                events.Append(string.Join(",", ev.Time.ToString("yyyy-MM-dd HH:mm:ss"), ev.KindText, Csv(ev.Title), Csv(ev.Detail),
                    ev.End?.ToString("yyyy-MM-dd HH:mm:ss") ?? "")).Append("\r\n");
            var eventsPath = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, Path.GetFileNameWithoutExtension(dlg.FileName) + "-events.csv");
            File.WriteAllText(eventsPath, events.ToString());
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't export the recording: {ex.Message}");
        }
    }

    static string Csv(string? s) =>
        s == null ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
