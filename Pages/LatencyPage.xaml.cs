using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

public partial class LatencyPage : UserControl, IPage
{
    const string HowItWorks =
        "Press Start capture, then do whatever stutters or crackles until the capture ends: play the game, the music or the video. "
        + "Tman times every DPC and ISR, the short routines drivers run with everything else on that processor held up, and shows which drivers ran longest.";

    /// <summary>Tall enough to show a few rows of results under the verdict and tiles on the smallest window.</summary>
    const double MinPageHeight = 560;

    readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    CancellationTokenSource? _cancel;
    string? _lastError;
    bool _clearedOrphans;

    public LatencyPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        EmptyText.Text = HowItWorks + (MainWindow.IsAdmin ? "" : "\n\nRestart Tman as administrator to run a capture.");
        StartButton.IsEnabled = MainWindow.IsAdmin;
        Scroller.SizeChanged += (_, e) => Root.Height = Math.Max(e.NewSize.Height, MinPageHeight);
        Unloaded += (_, _) => _cancel?.Cancel();
        Application.Current.SessionEnding += (_, _) => EtwTrace.StopAllNow();
    }

    public void OnShown()
    {
        if (_clearedOrphans || !MainWindow.IsAdmin) return;
        _clearedOrphans = true;
        _ = Task.Run(() =>
        {
            try { EtwTrace.StopOrphans(); }
            catch { }
        });
    }

    /// <summary>A trace mustn't outlive the page it belongs to, so leaving it ends the capture and shows what it has so far.</summary>
    public void OnHidden() => _cancel?.Cancel();

    /// <summary>
    /// Runs one capture and shows the result on the page. A failure is shown there too, never in a dialog, so this is
    /// safe to await with nobody at the keyboard.
    /// </summary>
    public async Task RunCaptureAsync(int seconds)
    {
        if (_cancel != null) return;
        _lastError = null;
        if (!MainWindow.IsAdmin)
        {
            ShowError("A capture needs administrator rights. Restart Tman as administrator, then try again.");
            return;
        }

        var cancel = _cancel = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        void Tick(object? s, EventArgs e)
        {
            double left = Math.Max(0, seconds - clock.Elapsed.TotalSeconds);
            Progress.Value = 1 - left / seconds;
            StatusText.Text = left > 0 ? $"Capturing… {Math.Ceiling(left):0} s left" : "Finishing…";
        }
        _ticker.Tick += Tick;
        _ticker.Start();
        Tick(null, EventArgs.Empty);
        StartButton.IsEnabled = LengthBox.IsEnabled = false;
        StopButton.IsEnabled = true;
        Progress.Visibility = Visibility.Visible;
        EmptyCard.Visibility = Visibility.Collapsed;

        try
        {
            ShowResult(await Task.Run(() => LatencyCapture.Run(seconds, cancel.Token)));
        }
        catch (EtwFailure ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            ShowError($"The capture failed: {ex.Message}");
        }
        finally
        {
            _ticker.Stop();
            _ticker.Tick -= Tick;
            cancel.Dispose();
            _cancel = null;
            StartButton.IsEnabled = LengthBox.IsEnabled = MainWindow.IsAdmin;
            StopButton.IsEnabled = false;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        int seconds = int.Parse((string)((ComboBoxItem)LengthBox.SelectedItem).Tag);
        await RunCaptureAsync(seconds);
        if (_lastError != null) Ui.Error(_lastError);
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _cancel?.Cancel();
    }

    void ShowError(string message)
    {
        _lastError = message;
        StatusText.Text = "";
        VerdictCard.Visibility = Tiles.Visibility = List.Visibility = DetailsPane.Visibility = Visibility.Collapsed;
        EmptyCard.Visibility = Visibility.Visible;
        EmptyText.Text = $"{message}\n\n{HowItWorks}";
    }

    internal void ShowResult(LatencyResult r)
    {
        var verdict = LatencyVerdict.From(r);
        VerdictTitle.Text = verdict.Headline;
        VerdictTitle.Foreground = (Brush)FindResource(verdict.Brush);
        VerdictText.Text = verdict.Detail;
        VerdictCard.Visibility = Visibility.Visible;

        bool shortened = r.Seconds + 1 < r.RequestedSeconds;
        StatusText.Text = $"Captured {r.Seconds:0.#} s" + (shortened ? $" of {r.RequestedSeconds} s (stopped early)" : "") + $" at {DateTime.Now:T}";

        if (r.Events == 0)
        {
            Tiles.Visibility = List.Visibility = DetailsPane.Visibility = Visibility.Collapsed;
            EmptyCard.Visibility = Visibility.Visible;
            EmptyText.Text = HowItWorks;
            return;
        }

        EmptyCard.Visibility = Visibility.Collapsed;
        Tiles.Visibility = List.Visibility = Visibility.Visible;
        SetTile(DpcValue, DpcSub, r.WorstDpc, d => d.DpcMax);
        SetTile(IsrValue, IsrSub, r.WorstIsr, d => d.IsrMax);
        if (r.CpuShare.Length > 0)
        {
            int busiest = Array.IndexOf(r.CpuShare, r.CpuShare.Max());
            CpuValue.Text = $"CPU {busiest}";
            CpuSub.Text = $"{r.CpuShare[busiest]:0}% of driver time";
        }
        EventsValue.Text = Fmt.Num(r.Events);
        EventsSub.Text = r.Lost > 0 ? $"{Fmt.Num(r.Lost)} lost by Windows" : "None lost";

        List.ItemsSource = r.Drivers;
        Ui.SortBy(List, nameof(DriverLatency.Max), ListSortDirection.Descending);
        DetailsPane.Visibility = Visibility.Collapsed;
    }

    void SetTile(TextBlock value, TextBlock sub, DriverLatency? driver, Func<DriverLatency, double> max)
    {
        if (driver == null)
        {
            value.Text = "None";
            value.Foreground = (Brush)FindResource("SubtleBrush");
            sub.Text = "None seen in this capture";
            return;
        }
        double us = max(driver);
        value.Text = $"{LatencyVerdict.Us(us)} µs";
        value.Foreground = (Brush)FindResource(us >= LatencyVerdict.RiskyUs ? "BadBrush" : us >= LatencyVerdict.FineUs ? "WarnBrush" : "GoodBrush");
        sub.Text = driver.File;
        sub.ToolTip = driver.Description.Length > 0 ? driver.Description : null;
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not DriverLatency d)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }
        DetailTitle.Text = d.Description.Length > 0 ? $"{d.File}  ·  {d.Description}" : d.File;
        DetailPath.Text = d.Path.Length > 0 ? d.Path : "Tman couldn't match these routines to a loaded driver.";
        DetailDpc.Text = Line("DPC", d.DpcCount, d.DpcTotal, d.DpcMax);
        DetailIsr.Text = Line("ISR", d.IsrCount, d.IsrTotal, d.IsrMax);
        DetailsPane.Visibility = Visibility.Visible;
    }

    static string Line(string kind, long count, double total, double max) => count == 0
        ? $"{kind}: none"
        : $"{kind}: {Fmt.Num(count)} runs, {LatencyVerdict.Us(total)} µs in total, {LatencyVerdict.Us(total / count)} µs on average, {LatencyVerdict.Us(max)} µs at most";

    void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DriverLatency d) Ui.OpenFileLocation(d.Path);
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DriverLatency d) Ui.SearchOnline($"{d.File} {d.Description} high DPC latency");
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is DriverLatency d)
            Ui.Copy($"{d.File}\t{d.Description}\t{Line("DPC", d.DpcCount, d.DpcTotal, d.DpcMax)}\t{Line("ISR", d.IsrCount, d.IsrTotal, d.IsrMax)}\t{d.Path}");
    }
}
