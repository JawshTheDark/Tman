using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Tman.Controls;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Tray;

/// <summary>A rectangle in physical screen pixels, as the shell and Win32 report them.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ScreenRect
{
    public int Left, Top, Right, Bottom;

    public readonly bool Contains(ScreenPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ScreenPoint
{
    public int X, Y;
}

/// <summary>
/// The live panel that appears while the pointer rests on the tray icon. It never takes the focus, so it can't interrupt
/// typing or a game, and it stays open while the pointer is on it. Windows only reports the pointer while it moves over the
/// icon, so a short timer watches it from then on and stops as soon as the panel is gone. UI thread only.
/// </summary>
internal sealed class TrayFlyout : Window
{
    const int PollMs = 80, OpenDelayMs = 250, CloseDelayMs = 300, Gap = 8, HoverSlop = 16;

    sealed class Row
    {
        public required TrayMetric Metric { get; init; }
        public required Grid Panel { get; init; }
        public required TextBlock Value { get; init; }
        public required UsageBar Bar { get; init; }
    }

    readonly Action _open;
    readonly Func<ScreenRect?> _iconArea;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(PollMs) };
    readonly StackPanel _rows = new();
    readonly List<Row> _all = new();
    readonly TextBlock _empty = new() { Text = "Waiting for the first reading…", Style = Themed("Subtle"), Margin = new Thickness(0, 6, 0, 2) };
    ScreenRect _reported;
    DateTime _hoverStarted;
    DateTime? _left;
    bool _dismissed;

    public TrayFlyout(Action open, Func<ScreenRect?> iconArea)
    {
        _open = open;
        _iconArea = iconArea;
        Title = "Tman flyout";
        Width = 316;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -30000;
        Top = -30000;
        Ui.Dress(this);
        Background = Brushes.Transparent;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "TMAN", Style = Themed("PanelLabel"), Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(_empty);
        panel.Children.Add(_rows);
        panel.Children.Add(new TextBlock
        {
            Text = "Click to open Tman. Right-click the icon for options.",
            Style = Themed("Subtle"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Content = new Border
        {
            Style = Themed("Card"),
            Padding = new Thickness(14, 12, 14, 10),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(10),
            Child = panel,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black },
        };

        foreach (var metric in TrayMetrics.All) _all.Add(MakeRow(metric));
        SourceInitialized += (_, _) =>
        {
            const int ExStyle = -20, ToolWindow = 0x80, NoActivate = 0x08000000;
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(handle, ExStyle, new IntPtr(GetWindowLongPtrW(handle, ExStyle).ToInt64() | ToolWindow | NoActivate));
        };
        MouseLeftButtonUp += (_, _) =>
        {
            Dismiss();
            _open();
        };
        _poll.Tick += (_, _) => Watch();
    }

    static Style Themed(string key) => (Style)Application.Current.FindResource(key);

    static Row MakeRow(TrayMetric metric)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock { Text = metric.Short, Foreground = new SolidColorBrush(metric.Color), FontWeight = FontWeights.SemiBold };
        var value = new TextBlock { Foreground = (Brush)Application.Current.FindResource("LabelBrush"), TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        var bar = new UsageBar { Fill = new SolidColorBrush(metric.Color), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(value, 1);
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 2);
        grid.Children.Add(name);
        grid.Children.Add(value);
        grid.Children.Add(bar);
        return new Row { Metric = metric, Panel = grid, Value = value, Bar = bar };
    }

    /// <summary>The pointer is on the icon. Opens after a short rest, so sweeping past on the way to the clock does nothing.</summary>
    public void Hover()
    {
        // Kept fresh on every report. For an icon in the panel behind the ^ arrow, Windows may give the arrow's position
        // instead of the icon's, and this is then the only true record of where the icon is.
        GetCursorPos(out var cursor);
        _reported = new ScreenRect { Left = cursor.X - HoverSlop, Top = cursor.Y - HoverSlop, Right = cursor.X + HoverSlop, Bottom = cursor.Y + HoverSlop };
        if (_dismissed || _poll.IsEnabled) return;
        _hoverStarted = DateTime.UtcNow;
        _left = null;
        _poll.Start();
    }

    /// <summary>Closes now, and stays closed until the pointer has left the icon, so a click or a menu isn't covered by it reopening.</summary>
    public void Dismiss()
    {
        if (IsVisible) Hide();
        _dismissed = true;
        _poll.Start();
    }

    void Watch()
    {
        GetCursorPos(out var cursor);
        var shell = _iconArea();
        var area = shell is { } known && known.Contains(cursor) ? known : _reported;
        bool onIcon = area.Contains(cursor);
        if (_dismissed)
        {
            if (onIcon) return;
            _dismissed = false;
            _poll.Stop();
            return;
        }

        bool onPanel = false;
        if (IsVisible && GetWindowRect(new WindowInteropHelper(this).Handle, out var panel)) onPanel = panel.Contains(cursor);
        if (onIcon || onPanel)
        {
            _left = null;
            if (!IsVisible && (DateTime.UtcNow - _hoverStarted).TotalMilliseconds >= OpenDelayMs) Open(area);
            return;
        }

        _left ??= DateTime.UtcNow;
        if (IsVisible && (DateTime.UtcNow - _left.Value).TotalMilliseconds < CloseDelayMs) return;
        if (IsVisible) Hide();
        _poll.Stop();
    }

    void Open(ScreenRect icon)
    {
        Arrange(SystemMonitor.Instance.Latest);
        Show();
        UpdateLayout();
        MoveNear(icon);
    }

    /// <summary>Puts the rows in order: what's chosen for the icon first, then everything else this PC can report.</summary>
    void Arrange(Snapshot? snapshot)
    {
        _rows.Children.Clear();
        var chosen = Settings.Current.TrayItems;
        foreach (var row in _all.OrderBy(r => chosen.IndexOf(r.Metric.Key) is var i && i >= 0 ? i : int.MaxValue))
            _rows.Children.Add(row.Panel);
        if (snapshot != null) Update(snapshot);
    }

    /// <summary>Shows the latest figures. A metric this PC can't report drops out rather than showing a blank bar.</summary>
    public void Update(Snapshot snapshot)
    {
        _empty.Visibility = Visibility.Collapsed;
        foreach (var row in _all)
        {
            var reading = row.Metric.Read(snapshot);
            row.Panel.Visibility = reading == null ? Visibility.Collapsed : Visibility.Visible;
            if (reading == null) continue;
            row.Value.Text = reading.Value;
            row.Bar.Value = double.IsFinite(reading.Level) ? Math.Clamp(reading.Level, 0, 1) : 0;
        }
    }

    /// <summary>Above the icon, or beside or below it when the taskbar is elsewhere, and always on the icon's own screen.</summary>
    void MoveNear(ScreenRect icon)
    {
        const uint DefaultToNearest = 2, NoSize = 1, NoActivate = 0x10, Show = 0x40;
        var monitor = MonitorFromRect(ref icon, DefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var work = info.rcWork;
        double scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;

        // The shadow's margin is see-through, so the gap is measured from the card.
        const int shadow = 10;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        int margin = (int)(Math.Max(0, Gap - shadow) * scale);
        int x = Math.Clamp((icon.Left + icon.Right - w) / 2, work.Left + margin, Math.Max(work.Left, work.Right - w - margin));
        int y = icon.Top - h - margin;
        if (y < work.Top) y = icon.Bottom + margin;
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top, work.Bottom - h - margin));
        SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, 0, 0, NoSize | NoActivate | Show);
    }

    /// <summary>Dev aid for <c>--snap &lt;dir&gt; tray</c>: fills the panel without waiting for a hover.</summary>
    internal void ShowForSnapshot(Snapshot? snapshot)
    {
        Arrange(snapshot);
        Show();
        UpdateLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        base.OnClosed(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public ScreenRect rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out ScreenPoint point);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr window, out ScreenRect rect);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref ScreenRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);

    [DllImport("user32.dll")]
    static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
}
