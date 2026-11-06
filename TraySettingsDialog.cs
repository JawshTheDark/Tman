using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Tman.Core;
using Tman.Monitoring;
using Tman.Tray;

namespace Tman;

/// <summary>Choose how Tman appears in the system tray and which metrics it shows. Changes apply immediately.</summary>
public sealed class TraySettingsDialog : Window
{
    sealed class Row
    {
        public required TrayMetric Metric { get; init; }
        public required CheckBox Box { get; init; }
        public required ListBoxItem Item { get; init; }
    }

    readonly Action _apply, _rebuild;
    readonly List<Row> _rows = new();
    readonly ListBox _list = new() { Height = 250, Margin = new Thickness(0, 6, 8, 0) };
    readonly CheckBox _enabled = new() { Content = "Show Tman in the system tray", FontWeight = FontWeights.Bold };
    readonly RadioButton _combined = new() { Content = "One icon with a flyout", GroupName = "TrayLayout", MinWidth = 0, Margin = new Thickness(0, 0, 16, 0) };
    readonly RadioButton _separate = new() { Content = "Separate icons", GroupName = "TrayLayout", MinWidth = 0 };
    readonly TextBlock _iconsHint = Hint("");
    readonly StackPanel _stylePanel = new();
    readonly RadioButton _number = new() { Content = "Number", GroupName = "TrayStyle", MinWidth = 0, Margin = new Thickness(0, 0, 16, 0) };
    readonly RadioButton _meter = new() { Content = "LED meter", GroupName = "TrayStyle", MinWidth = 0 };
    readonly CheckBox _keep = new() { Content = "Keep Tman running in the tray when its window is closed", Margin = new Thickness(0, 14, 0, 0) };
    readonly StackPanel _preview = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), MinHeight = 40 };
    bool _loading = true;

    public TraySettingsDialog(Action apply, Action rebuild)
    {
        _apply = apply;
        _rebuild = rebuild;
        Title = "Tray icons";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        var s = Settings.Current;
        _enabled.IsChecked = s.TrayEnabled;
        _combined.IsChecked = s.TrayLayout != "separate";
        _separate.IsChecked = s.TrayLayout == "separate";
        _number.IsChecked = s.TrayStyle != "meter";
        _meter.IsChecked = s.TrayStyle == "meter";
        _keep.IsChecked = s.KeepInTray;

        // Chosen metrics first, in their tray order, then the rest.
        var ordered = s.TrayItems.Select(TrayMetrics.Find).OfType<TrayMetric>()
            .Concat(TrayMetrics.All.Where(m => !s.TrayItems.Contains(m.Key)));
        foreach (var m in ordered)
        {
            var box = new CheckBox { IsChecked = s.TrayItems.Contains(m.Key), Content = m.Name, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            box.Click += (_, _) => Changed();
            var swatch = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(m.Color), Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var line = new DockPanel();
            line.Children.Add(swatch);
            line.Children.Add(box);
            var item = new ListBoxItem { Content = line, Padding = new Thickness(6, 2, 6, 2) };
            _rows.Add(new Row { Metric = m, Box = box, Item = item });
            _list.Items.Add(item);
        }

        _enabled.Click += (_, _) => Changed();
        _combined.Checked += (_, _) => Changed();
        _separate.Checked += (_, _) => Changed();
        _number.Checked += (_, _) => Changed();
        _meter.Checked += (_, _) => Changed();
        _keep.Click += (_, _) => Changed();

        var up = new Button { Content = "Move up", MinWidth = 100, Margin = new Thickness(0, 6, 0, 6) };
        var down = new Button { Content = "Move down", MinWidth = 100 };
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);
        var moveButtons = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        moveButtons.Children.Add(up);
        moveButtons.Children.Add(down);
        var listRow = new DockPanel();
        DockPanel.SetDock(moveButtons, Dock.Right);
        listRow.Children.Add(moveButtons);
        listRow.Children.Add(_list);

        var layout = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        layout.Children.Add(_combined);
        layout.Children.Add(_separate);

        var style = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        style.Children.Add(_number);
        style.Children.Add(_meter);
        _stylePanel.Children.Add(Label("STYLE", 16));
        _stylePanel.Children.Add(style);

        var pin = new Button { Content = "Always show on the taskbar", Margin = new Thickness(0, 0, 8, 0) };
        pin.Click += (_, _) => Pin();
        var settings = new Button { Content = "Taskbar settings" };
        settings.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); } catch { }
        };
        var pinRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        pinRow.Children.Add(pin);
        pinRow.Children.Add(settings);

        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(_enabled);
        root.Children.Add(Label("LAYOUT", 16));
        root.Children.Add(layout);
        root.Children.Add(Label("WHAT IT SHOWS", 16));
        root.Children.Add(_iconsHint);
        root.Children.Add(listRow);
        root.Children.Add(_stylePanel);
        root.Children.Add(Label("PREVIEW (ACTUAL SIZE, ENLARGED 2×)", 14));
        root.Children.Add(_preview);
        root.Children.Add(_keep);
        root.Children.Add(Label("VISIBILITY", 16));
        root.Children.Add(Hint("Windows 11 puts new tray icons behind the ^ arrow at first. Pin Tman to keep it in view."));
        root.Children.Add(pinRow);
        root.Children.Add(close);
        Content = root;

        SystemMonitor.Instance.Updated += OnUpdated;
        Closed += (_, _) => SystemMonitor.Instance.Updated -= OnUpdated;
        _loading = false;
        UpdatePreview();
    }

    static TextBlock Label(string text, double top) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("PanelLabel"),
        Margin = new Thickness(0, top, 0, 0),
    };

    static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.FindResource("Subtle"),
        Margin = new Thickness(0, 3, 0, 0),
    };

    void Move(int delta)
    {
        int i = _list.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        (_rows[i], _rows[j]) = (_rows[j], _rows[i]);
        var item = _rows[j].Item;
        _list.Items.RemoveAt(i);
        _list.Items.Insert(j, item);
        _list.SelectedIndex = j;
        Changed();
    }

    void Changed()
    {
        if (_loading) return;
        var s = Settings.Current;
        s.TrayEnabled = _enabled.IsChecked == true;
        s.TrayLayout = _separate.IsChecked == true ? "separate" : "combined";
        s.TrayItems = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Metric.Key).ToList();
        s.TrayStyle = _meter.IsChecked == true ? "meter" : "number";
        s.KeepInTray = _keep.IsChecked == true;
        if (!MainWindow.SnapshotMode) s.Save();
        _apply();
        UpdatePreview();
    }

    void OnUpdated(Snapshot s) => UpdatePreview();

    void UpdatePreview()
    {
        _preview.Children.Clear();
        var snap = SystemMonitor.Instance.Latest;
        var s = Settings.Current;
        bool combined = s.TrayLayout != "separate";
        _stylePanel.Visibility = combined ? Visibility.Collapsed : Visibility.Visible;
        _iconsHint.Text = combined
            ? "The first three ticked become bars on the icon. Hovering shows all of these, whichever are ticked."
            : "Tick what to show. The order here is the order the icons appear in the tray.";
        if (!s.TrayEnabled || (!combined && s.TrayItems.Count == 0))
        {
            _preview.Children.Add(Hint("No tray icons."));
            return;
        }
        if (combined)
        {
            var bars = TrayIcons.BarMetrics().Select(m => ((snap == null ? null : m.Read(snap))?.Level ?? 0, m.Color)).ToList();
            var icon = new Image { Source = TrayRenderer.RenderBars(bars, 16), Width = 32, Height = 32 };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.NearestNeighbor);
            _preview.Children.Add(icon);
            return;
        }
        foreach (var key in s.TrayItems)
        {
            var m = TrayMetrics.Find(key);
            if (m == null) continue;
            var reading = snap == null ? null : m.Read(snap);
            var bmp = TrayRenderer.Render(reading, m.Color, s.TrayStyle == "meter", 16);
            var img = new Image { Source = bmp, Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0), ToolTip = reading?.Tip ?? m.Name };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            _preview.Children.Add(img);
        }
    }

    void Pin()
    {
        int changed = TrayIcons.SetAlwaysShow(true);
        if (changed == 0)
        {
            MessageBox.Show(this, "Windows hasn't registered Tman's tray icon yet. Turn it on, wait a moment, then try again, or use Taskbar settings > Other system tray icons.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _rebuild();
    }
}
