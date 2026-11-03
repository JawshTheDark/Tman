using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tman.Core;
using Tman.Monitoring;
using Tman.Pages;

namespace Tman;

public partial class MainWindow : Window
{
    sealed record NavEntry(string Key, string Glyph, string Title, Func<FrameworkElement> Create);

    readonly List<object> _entries;
    readonly Dictionary<string, FrameworkElement> _pages = new();
    readonly List<TextBlock> _navLabels = new();
    FrameworkElement? _current;

    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>True when running with --snap (dev rendering); settings are left untouched.</summary>
    public static bool SnapshotMode { get; private set; }

    public MainWindow()
    {
        InitializeComponent();

        _entries = new List<object>
        {
            new NavEntry("summary", "", "Summary", () => new SummaryPage()),
            new NavEntry("performance", "", "Performance", () => new PerformancePage()),
            new NavEntry("processes", "", "Processes", () => new ProcessesPage()),
            new NavEntry("sysinfo", "", "System info", () => new SystemInfoPage()),
            new NavEntry("history", "", "App history", () => new AppHistoryPage()),
            new NavEntry("users","", "Users", () => new UsersPage()),
            new NavEntry("startup", "", "Startup apps", () => new StartupPage()),
            new NavEntry("services", "", "Services", () => new ServicesPage()),
            "TOOLS",
            new NavEntry("power", "", "Power & freq", () => new PowerPage()),
            new NavEntry("flight", "", "Flight recorder", () => new FlightRecorderPage()),
            new NavEntry("connections", "", "Connections", () => new ConnectionsPage()),
            new NavEntry("locks", "", "File locks", () => new FileLocksPage()),
            new NavEntry("apps","", "Installed apps", () => new InstalledAppsPage()),
            new NavEntry("drivers", "", "Drivers", () => new DriversPage()),
            new NavEntry("disk", "", "Disk space", () => new DiskSpacePage()),
            new NavEntry("bench", "", "Benchmarks", () => new BenchmarksPage()),
        };
        BuildNav();

        var s = Settings.Current;
        Width = s.Width;
        Height = s.Height;
        if (s.Maximized) WindowState = WindowState.Maximized;
        Topmost = TopmostItem.IsChecked = s.Topmost;
        CollapseItem.IsChecked = s.NavCollapsed;
        ApplyNavCollapsed(s.NavCollapsed);
        CheckSpeed(SystemMonitor.Instance.IntervalMs);

        Title = IsAdmin ? "Tman (Administrator)" : "Tman";
        AdminBadge.Text = IsAdmin ? "Administrator" : "Standard user — some details need admin";
        AdminItem.IsEnabled = !IsAdmin;

        var start = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as NavEntry)?.Key == s.LastPage)
                    ?? Nav.Items.OfType<ListBoxItem>().First(i => i.Tag is NavEntry);
        Nav.SelectedItem = start;

        SystemMonitor.Instance.Updated += OnUpdated;
        PreviewKeyDown += OnPreviewKeyDown;
        StartClock();

        var args = Environment.GetCommandLineArgs();
        var snap = Array.IndexOf(args, "--snap");
        if (snap > 0 && snap + 1 < args.Length)
        {
            SnapshotMode = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            WindowState = WindowState.Normal;
            Loaded += (_, _) => RunSnapshots(args[snap + 1], snap + 2 < args.Length ? args[snap + 2] : null, snap + 3 < args.Length ? args[snap + 3] : null);
        }
        var offscreen = Array.IndexOf(args, "--offscreen");
        if (offscreen > 0)
        {
            // Dev aid for profiling: run normally but out of view, optionally on a given page.
            SnapshotMode = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            WindowState = WindowState.Normal;
            if (offscreen + 1 < args.Length)
                Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as NavEntry)?.Key == args[offscreen + 1]) ?? Nav.SelectedItem;
        }
        Closing += (_, _) =>
        {
            if (SnapshotMode) return;
            s.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.Width = Width;
                s.Height = Height;
            }
            s.Save();
        };
    }

    void BuildNav()
    {
        foreach (var e in _entries)
        {
            if (e is string header)
            {
                Nav.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = header, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush") },
                    Style = (Style)FindResource("NavHeader"),
                });
                continue;
            }
            var entry = (NavEntry)e;
            var label = new TextBlock { Text = entry.Title, FontSize = 13, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _navLabels.Add(label);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = entry.Glyph, Style = (Style)FindResource("Glyph") });
            panel.Children.Add(label);
            Nav.Items.Add(new ListBoxItem { Content = panel, Tag = entry, ToolTip = entry.Title });
        }
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: NavEntry entry }) return;
        if (_current is IPage old) old.OnHidden();
        if (!_pages.TryGetValue(entry.Key, out var page))
            _pages[entry.Key] = page = entry.Create();
        Host.Content = page;
        _current = page;
        Settings.Current.LastPage = entry.Key;
        if (page is IPage p) p.OnShown();
    }

    void OnUpdated(Snapshot s)
    {
        StatusProcs.Text = $"{s.ProcessCount} processes";
        StatusCpu.Text = $"{s.Cpu.Total:0}%";
        StatusCpuBar.Value = s.Cpu.Total / 100;
        StatusMem.Text = $"{s.Mem.UsedPct:0}%  ({Fmt.Bytes(s.Mem.Used)} / {Fmt.Bytes(s.Mem.Total)})";
        StatusMemBar.Value = s.Mem.UsedPct / 100;
        StatusUptime.Text = $"Up {Fmt.Uptime(s.Uptime)}";
    }

    /// <summary>Status bar clock, flight-recorder length and the blinking REC light; runs even while sampling is paused.</summary>
    void StartClock()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        bool blink = false;
        void Tick()
        {
            ClockText.Text = DateTime.Now.ToString("T");
            var frames = FlightRecorder.Instance.Frames;
            var kept = frames.Count > 1 ? frames[^1].Time - frames[0].Time : TimeSpan.Zero;
            RecText.Text = $"REC {(int)kept.TotalMinutes}:{kept.Seconds:00}";
            bool recording = SystemMonitor.Instance.IntervalMs > 0;
            blink = !blink;
            RecDot.Opacity = recording ? (blink ? 1 : 0.25) : 0.25;
        }
        timer.Tick += (_, _) => Tick();
        Tick();
        timer.Start();
    }

    void Rec_Click(object sender, MouseButtonEventArgs e) =>
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Tag as NavEntry)?.Key == "flight") ?? Nav.SelectedItem;

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            SystemMonitor.Instance.RefreshNow();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            RunTask_Click(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _current is IPage page)
        {
            page.FocusSearch();
            e.Handled = true;
        }
    }

    void RunTask_Click(object sender, RoutedEventArgs e) => new RunDialog { Owner = this }.ShowDialog();

    void Admin_Click(object sender, RoutedEventArgs e) => RestartAsAdmin();

    public static void RestartAsAdmin()
    {
        try
        {
            Settings.Current.Save();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt.
        }
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    void Refresh_Click(object sender, RoutedEventArgs e) => SystemMonitor.Instance.RefreshNow();

    void Speed_Click(object sender, RoutedEventArgs e)
    {
        var ms = int.Parse((string)((MenuItem)sender).Tag);
        SystemMonitor.Instance.IntervalMs = ms;
        Settings.Current.IntervalMs = ms;
        CheckSpeed(ms);
    }

    void CheckSpeed(int ms)
    {
        foreach (var item in new[] { SpeedHigh, SpeedNormal, SpeedLow, SpeedPaused })
            item.IsChecked = int.Parse((string)item.Tag) == ms;
        var brush = (System.Windows.Media.Brush)FindResource(ms > 0 ? "GoodBrush" : "WarnBrush");
        LiveDot.Fill = LiveText.Foreground = brush;
        LiveText.Text = ms > 0 ? $"LIVE · {ms / 1000.0:0.#} s" : "PAUSED";
    }

    void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostItem.IsChecked;
        Settings.Current.Topmost = Topmost;
    }

    void Collapse_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.NavCollapsed = CollapseItem.IsChecked;
        ApplyNavCollapsed(CollapseItem.IsChecked);
    }

    void ApplyNavCollapsed(bool collapsed)
    {
        NavColumn.Width = new GridLength(collapsed ? 62 : 224);
        BrandText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var l in _navLabels) l.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in Nav.Items.OfType<ListBoxItem>().Where(i => i.Tag is not NavEntry))
            item.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Dev aid: <c>Tman.exe --snap &lt;dir&gt; [page] [extra]</c> renders pages to PNGs off-screen, then exits.
    /// <c>extra</c> is a folder to analyze (disk), any value to run the benchmarks (bench), or seconds to record first (flight).
    /// </summary>
    async void RunSnapshots(string dir, string? only, string? extra)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(2500);
        if (only == "details")
        {
            await SnapDetails(dir, extra);
            Application.Current.Shutdown();
            return;
        }
        if (only == "flight" && int.TryParse(extra, out var recordSeconds)) await Task.Delay(recordSeconds * 1000);
        foreach (var item in Nav.Items.OfType<ListBoxItem>().Where(i => i.Tag is NavEntry e && (only == null || e.Key == only)))
        {
            Nav.SelectedItem = item;
            if (extra != null && _current is DiskSpacePage disk) disk.Analyze(extra);
            if (extra != null && _current is BenchmarksPage bench) await bench.RunAllAsync();
            if (extra != null && _current is FileLocksPage locks) await locks.SearchAsync(extra);
            if (extra != null && _current is PerformancePage perf) { await Task.Delay(1500); perf.SelectDevice(extra); }
            await Task.Delay(((NavEntry)item.Tag).Key is "drivers" or "disk" or "apps" or "services" or "startup" or "processes" ? 8000 : 2500);
            var key = ((NavEntry)item.Tag).Key;
            SavePng((FrameworkElement)Content, Path.Combine(dir, key + ".png"));
            // Scrolling pages also get a capture of their whole content, below-the-fold included.
            if (_current is UserControl { Content: ScrollViewer { Content: FrameworkElement inner } })
                SavePng(inner, Path.Combine(dir, key + "-full.png"));
        }
        Application.Current.Shutdown();
    }

    /// <summary>Dev aid: <c>--snap &lt;dir&gt; details [pid]</c> renders each Details tab and exercises dump, affinity and signature checks.</summary>
    async Task SnapDetails(string dir, string? pidText)
    {
        int pid = int.TryParse(pidText, out var x) ? x : Environment.ProcessId;
        var sample = SystemMonitor.Instance.Latest?.Processes.FirstOrDefault(p => p.Pid == pid);
        if (sample == null) return;
        var win = new ProcessDetailsWindow(new ProcessItem(sample)) { Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
        win.Show();
        string[] tabs = { "general", "threads", "modules", "environment", "files" };
        for (int i = 0; i < tabs.Length; i++)
        {
            win.SelectTab(i);
            await Task.Delay(i == 4 ? 5000 : 2500);
            SavePng((FrameworkElement)win.Content, Path.Combine(dir, $"details-{tabs[i]}.png"));
        }
        win.Close();

        var checks = new List<string>();
        var dump = Path.Combine(dir, "test.dmp");
        string? dumpError = null;
        bool dumped = await Task.Run(() => ProcessInspector.WriteDump(pid, dump, out dumpError));
        checks.Add(dumped ? $"dump ok: {new FileInfo(dump).Length:N0} bytes" : $"dump failed: {dumpError}");
        if (File.Exists(dump)) File.Delete(dump);
        checks.Add(ProcessInspector.GetAffinity(pid, out var mask, out var system, out var affErr) ? $"affinity 0x{mask:X} of 0x{system:X}" : $"affinity error: {affErr}");
        foreach (var file in new[] { Environment.ProcessPath!, Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(Environment.SystemDirectory, "ntdll.dll"), Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts") })
            checks.Add($"signature {Path.GetFileName(file)}: {await Task.Run(() => Signatures.Check(file))}");
        foreach (var g in SystemMonitor.Instance.Latest?.Gpus ?? new())
            checks.Add($"gpu {g.Name}: {g.Sensors?.ToString() ?? "no sensors"}");
        File.WriteAllLines(Path.Combine(dir, "details-checks.txt"), checks);
    }

    static void SavePng(FrameworkElement root, string path)
    {
        int w = (int)root.ActualWidth, h = (int)root.ActualHeight;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        png.Save(fs);
    }

    void About_Click(object sender, RoutedEventArgs e)
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(this, $"Tman {v?.ToString(3)}\nA lightweight task manager for Windows 11.\n\n.NET {Environment.Version}",
            "About Tman", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
