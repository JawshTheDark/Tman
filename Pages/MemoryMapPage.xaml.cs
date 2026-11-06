using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

public sealed record MemorySegment(string Name, ulong Bytes, ulong Whole, Brush Brush, double Opacity, string Meaning)
{
    public string SizeText => Fmt.Bytes(Bytes);
    public string PercentText => Whole == 0 ? "" : $"{Bytes * 100.0 / Whole:0.#}%";
}

public sealed record StandbyLevel(string Label, ulong Bytes, double Share)
{
    public string SizeText => Fmt.Bytes(Bytes);
}

public partial class MemoryMapPage : UserControl, IPage
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    MemoryMapReader? _reader;
    bool _reading, _shown;
    int _tagSignature;
    ulong _lastStandby;
    string _notice = "";
    DateTime _noticeUntil;

    public MemoryMapPage()
    {
        InitializeComponent();
        Ui.SetupGrid(Tags);
        Tags.PreviewMouseWheel += ForwardWheel;
        EmptyButton.IsEnabled = MainWindow.IsAdmin;
        EmptyButton.ToolTip = MainWindow.IsAdmin
            ? "Drop cached file data from RAM. Windows refills it, and this rarely helps."
            : "This needs administrator rights.";
        _timer.Tick += (_, _) => Refresh();
        SizeChanged += (_, e) => Tiles.Columns = e.NewSize.Width < 760 ? 2 : 4;
    }

    public void OnShown()
    {
        _shown = true;
        _timer.Start();
        Refresh();
    }

    public void OnHidden()
    {
        _shown = false;
        _timer.Stop();
    }

    async void Refresh()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var data = await Task.Run(() => (_reader ??= new MemoryMapReader()).Read());
            if (_shown) Show(data);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            Subtitle.Text = $"Couldn't read memory information: {ex.Message}";
        }
        finally
        {
            _reading = false;
        }
    }

    Brush Res(string key) => (Brush)FindResource(key);

    void Show(MemoryMapData m)
    {
        _lastStandby = m.Standby;
        Subtitle.Text = DateTime.Now < _noticeUntil ? _notice
            : m.ListsMissing ? "Windows didn't report its memory lists, so the split below is incomplete."
            : $"{Fmt.Bytes(m.Installed)} installed. Updates every 2 seconds.";

        ulong whole = m.Installed;
        var segments = new List<MemorySegment>
        {
            new("In use", m.InUse, whole, Res("MemBrush"), 1, "Held by programs, drivers and Windows. This is the memory that can't simply be reused."),
            new("Modified", m.Modified, whole, Res("NetBrush"), 1, "Changed data waiting to be written to disk. Once written, it becomes standby."),
            new("Standby", m.Standby, whole, Res("CpuBrush"), 1, "Cached data that isn't in use. Windows hands it over the moment a program needs memory, so it counts as available."),
        };
        if (m.ZeroKnown)
        {
            segments.Add(new("Free", m.Free, whole, Res("GoodBrush"), 1, "Unused pages that still need clearing before reuse."));
            segments.Add(new("Zero", m.Zero, whole, Res("GoodBrush"), 0.5, "Unused pages already cleared, ready to hand out at once."));
        }
        else
        {
            segments.Add(new("Free and zero", m.Free, whole, Res("GoodBrush"), 1, "Unused pages, ready to hand out."));
        }
        segments.Add(new("Hardware reserved", m.HardwareReserved, whole, Res("SubtleBrush"), 1, "Set aside by firmware and devices, often built-in graphics. Windows can't use it."));

        Legend.ItemsSource = segments;
        DrawBar(segments);

        CommitValue.Text = Fmt.Bytes(m.Commit);
        CommitSub.Text = $"of a {Fmt.Bytes(m.CommitLimit)} limit: memory promised to programs, backed by RAM and the page file";
        CommitValue.Foreground = Res(m.CommitLimit > 0 && m.Commit > m.CommitLimit * 0.9 ? "WarnBrush" : "MemBrush");
        PagedValue.Text = Fmt.Bytes(m.PagedPool);
        NonPagedValue.Text = Fmt.Bytes(m.NonPagedPool);
        CacheValue.Text = m.FileCache > 0 ? Fmt.Bytes(m.FileCache) : "—";

        ulong biggest = m.StandbyLevels.Count == 0 ? 0 : m.StandbyLevels.Max(l => l.Bytes);
        StandbyLevels.ItemsSource = m.StandbyLevels.Select(l => new StandbyLevel(l.Label, l.Bytes, biggest == 0 ? 0 : (double)l.Bytes / biggest)).ToList();
        StandbyNote.Text = m.ZeroKnown
            ? "When a program needs memory, Windows takes it from priority 0 first and priority 7 last. The higher the number, the more it wants to keep that data."
            : "Windows reports three groups here. It takes memory from Reserve first and Core last.";

        ShowTags(m.PoolTags);
    }

    void DrawBar(List<MemorySegment> segments)
    {
        Bar.Children.Clear();
        Bar.ColumnDefinitions.Clear();
        foreach (var s in segments)
        {
            if (s.Bytes == 0) continue;
            Bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(s.Bytes, GridUnitType.Star) });
            var piece = new Border { Background = s.Brush, Opacity = s.Opacity, ToolTip = $"{s.Name}: {s.SizeText} ({s.PercentText})" };
            Grid.SetColumn(piece, Bar.ColumnDefinitions.Count - 1);
            Bar.Children.Add(piece);
        }
    }

    void ShowTags(List<PoolTagUse>? tags)
    {
        if (tags == null)
        {
            Tags.Visibility = Visibility.Collapsed;
            PoolNote.Text = "Windows didn't report pool tags on this PC.";
            return;
        }
        // Rebuilding the rows would drop the selection and any open menu, so wait until something really changed.
        int signature = 0;
        foreach (var t in tags) signature = HashCode.Combine(signature, t);
        if (signature == _tagSignature) return;
        _tagSignature = signature;
        var selected = (Tags.SelectedItem as PoolTagUse)?.Tag;
        Tags.ItemsSource = tags;
        Tags.SelectedItem = tags.FirstOrDefault(t => t.Tag == selected);
    }

    /// <summary>The grid sits inside the page's scroller and shows every row, so the wheel should move the page.</summary>
    void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    async void Empty_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Empty the standby list?\n\n"
                + "This drops the file data Windows keeps in RAM to load things faster. Nothing is lost, but programs and files can load more slowly for a while, because Windows has to read that data from disk again.\n\n"
                + "Windows refills the list by itself, and this rarely helps. Free memory isn't better than cached memory.",
                "Empty standby list"))
            return;

        EmptyButton.IsEnabled = false;
        try
        {
            ulong before = _lastStandby;
            var error = await Task.Run(MemoryMapReader.EmptyStandbyList);
            if (error != null)
            {
                Ui.Error($"Couldn't empty the standby list. {error}");
                return;
            }
            _notice = $"Emptied the standby list ({Fmt.Bytes(before)}). Windows will fill it again as files are read.";
            _noticeUntil = DateTime.Now.AddSeconds(10);
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            Ui.Error($"Couldn't empty the standby list. {ex.Message}");
        }
        finally
        {
            EmptyButton.IsEnabled = MainWindow.IsAdmin;
        }
    }

    void SearchTag_Click(object sender, RoutedEventArgs e)
    {
        if (Tags.SelectedItem is PoolTagUse t) Ui.SearchOnline($"Windows pool tag \"{t.Tag}\"");
    }

    void CopyTag_Click(object sender, RoutedEventArgs e)
    {
        if (Tags.SelectedItem is PoolTagUse t) Ui.Copy($"{t.Tag}\t{t.TotalText}\t{t.NonPagedText}\t{t.PagedText}\t{t.BlocksText}\t{t.Meaning}");
    }
}
