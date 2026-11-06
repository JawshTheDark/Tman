using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Live output of one job at a time, shared by System repair and Windows features: log, progress line, Stop button and result.</summary>
public partial class JobOutputPanel : UserControl
{
    const int MaxChars = 200_000;

    // chkdsk prints "Stage: 40%; Total: 12%"; the total is the one that means something.
    static readonly Regex TotalPercent = new(@"Total:\s*(\d{1,3})\s*%"), AnyPercent = new(@"(\d{1,3}(?:[.,]\d+)?)\s*%");

    readonly ConcurrentQueue<string> _lines = new();
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _started;
    Action? _stop;
    int _flushQueued;
    bool _running;

    /// <summary>Keeps every line in the log, for output where a percent sign is just text rather than progress.</summary>
    public bool PlainLog { get; set; }

    public JobOutputPanel()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => ShowClock();
        Loaded += (_, _) => { if (_running) _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    public void ShowIdle(string text)
    {
        TitleText.Text = "OUTPUT";
        Output.Text = text;
    }

    /// <param name="stop">What the Stop button does, or null when stopping this job isn't safe.</param>
    public void Start(string title, Action? stop)
    {
        _running = true;
        _stop = stop;
        _started = DateTime.Now;
        TitleText.Text = title.ToUpperInvariant();
        Output.Clear();
        Bar.Value = 0;
        ProgressPanel.Visibility = ResultPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        StopButton.Content = "Stop";
        StopButton.IsEnabled = true;
        StopButton.Visibility = stop != null ? Visibility.Visible : Visibility.Collapsed;
        ShowClock();
        _clock.Start();
    }

    /// <summary>Adds a line of output. Safe from any thread; lines are drawn in batches so a chatty tool can't swamp the window.</summary>
    public void Line(string line)
    {
        _lines.Enqueue(line);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            Dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
    }

    void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        var text = new StringBuilder();
        string? progress = null;
        while (_lines.TryDequeue(out var line))
        {
            if (!PlainLog && AnyPercent.IsMatch(line)) progress = line;
            else text.AppendLine(line);
        }
        if (progress != null) ShowProgress(progress);
        if (text.Length == 0) return;
        if (Output.Text.Length > MaxChars) Output.Text = Output.Text[^(MaxChars / 2)..];
        Output.AppendText(text.ToString());
        Output.ScrollToEnd();
    }

    void ShowProgress(string line)
    {
        var match = TotalPercent.Match(line);
        if (!match.Success) match = AnyPercent.Match(line);
        Bar.Value = double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var percent) ? percent / 100 : 0;
        // DISM draws its bar in text: [====   20.0%   ]
        ProgressText.Text = line.TrimStart().StartsWith('[') ? match.Value : line.Trim();
        ProgressPanel.Visibility = Visibility.Visible;
    }

    public void Finish(JobResult result)
    {
        Flush();
        _running = false;
        _stop = null;
        _clock.Stop();
        StopButton.Visibility = ProgressPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = Output.Text.Length > 0;
        ClockText.Text = $"Took {Fmt.Span(DateTime.Now - _started)}";
        ResultText.Text = result.Text;
        ResultText.Foreground = (Brush)FindResource(result.Level switch { JobLevel.Good => "GoodBrush", JobLevel.Warn => "WarnBrush", _ => "BadBrush" });
        RestartText.Text = result.Restart == true ? "Restart Windows to finish." : "No restart is needed.";
        RestartText.Visibility = result.Restart == null ? Visibility.Collapsed : Visibility.Visible;
        ResultPanel.Visibility = Visibility.Visible;
    }

    void ShowClock() => ClockText.Text = $"Running {Fmt.Span(DateTime.Now - _started)}";

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StopButton.Content = "Stopping…";
        _stop?.Invoke();
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Ui.Copy(Output.Text);
}
