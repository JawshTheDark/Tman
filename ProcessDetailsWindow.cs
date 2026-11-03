using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Controls;
using Tman.Core;
using Tman.Monitoring;
using Tman.Pages;

namespace Tman;

/// <summary>Everything about one process: overview, threads, loaded modules, environment and open files.</summary>
public sealed class ProcessDetailsWindow : Window
{
    sealed class ThreadRow : ObservableObject
    {
        public int Tid { get; init; }
        string _name = "", _state = "", _start = "";
        double _cpu;
        long _cpuTime;
        int _priority;
        uint _switches;
        public string Name { get => _name; set => Set(ref _name, value); }
        public string State { get => _state; set => Set(ref _state, value); }
        public int Priority { get => _priority; set => Set(ref _priority, value); }
        public double Cpu { get => _cpu; set { if (Set(ref _cpu, value)) Raise(nameof(CpuText)); } }
        public string CpuText => Fmt.Pct(_cpu);
        public long CpuTime { get => _cpuTime; set { if (Set(ref _cpuTime, value)) Raise(nameof(CpuTimeText)); } }
        public string CpuTimeText => $"{TimeSpan.FromTicks(_cpuTime):h\\:mm\\:ss\\.ff}";
        public uint Switches { get => _switches; set => Set(ref _switches, value); }
        public string Start { get => _start; set => Set(ref _start, value); }
    }

    sealed class ModuleRow : ObservableObject
    {
        public required ModuleInfo Module { get; init; }
        public string Name => Module.Name;
        public string Path => Module.Path;
        public ulong Base => Module.Base;
        public string BaseText => $"0x{Module.Base:X}";
        public uint Size => Module.Size;
        public string SizeText => Fmt.Bytes(Module.Size);
        string _description = "", _company = "", _version = "";
        public string Description { get => _description; set => Set(ref _description, value); }
        public string Company { get => _company; set => Set(ref _company, value); }
        public string Version { get => _version; set => Set(ref _version, value); }
    }

    readonly ProcessItem _p;
    readonly TabControl _tabs = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly StackPanel _general = new() { Margin = new Thickness(4, 10, 4, 4) };
    readonly ObservableCollection<ThreadRow> _threads = new();
    readonly ObservableCollection<ModuleRow> _modules = new();
    readonly DataGrid _threadGrid = MakeGrid(), _moduleGrid = MakeGrid(), _envGrid = MakeGrid(), _fileGrid = MakeGrid();
    readonly TextBlock _threadNote = Note(), _moduleNote = Note(), _envNote = Note(), _fileNote = Note();
    readonly TabItem _threadTab, _moduleTab, _envTab, _fileTab;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly TextBlock _signer = new() { Margin = new Thickness(0, 3, 0, 0) };
    List<ModuleInfo>? _moduleList;
    Dictionary<int, long> _prevCpu = new();
    DateTime _prevTime;
    bool _modulesShown, _envLoaded, _filesLoaded, _refreshing;
    string? _currentDir;

    public ProcessDetailsWindow(ProcessItem p)
    {
        _p = p;
        Title = $"{p.Name} (PID {p.Pid})";
        Width = 1000;
        Height = 660;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.Dress(this);

        _threadTab = Tab("Threads", _threadGrid, _threadNote);
        _moduleTab = Tab("Modules", _moduleGrid, _moduleNote);
        _envTab = Tab("Environment", _envGrid, _envNote);
        _fileTab = Tab("Open files", _fileGrid, _fileNote);
        _tabs.Items.Add(new TabItem { Header = "General", Content = new ScrollViewer { Content = _general, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        foreach (var t in new[] { _threadTab, _moduleTab, _envTab, _fileTab }) _tabs.Items.Add(t);
        _tabs.SelectionChanged += (s, e) => { if (e.OriginalSource == _tabs) OnTabChanged(); };

        SetupThreadGrid();
        SetupModuleGrid();
        Columns(_envGrid, ("Variable", "Key", 220.0), ("Value", "Value", -1.0));
        Columns(_fileGrid, ("Type", "Kind", 90.0), ("Path", "Path", -1.0));

        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_tabs);
        Content = root;

        _timer.Tick += (_, _) => RefreshThreads();
        Closed += (_, _) => _timer.Stop();
        Loaded += async (_, _) =>
        {
            // Modules feed thread start addresses and the General tab, so fetch them up front.
            _moduleList = await Task.Run(() => ProcessInspector.Modules(_p.Pid, out _));
            var env = await Task.Run(() => (Vars: ProcessInspector.Environment(_p.Pid, out var cwd, out var err), Cwd: cwd, Err: err));
            _currentDir = env.Cwd;
            FillEnvironment(env.Vars, env.Err);
            FillGeneral();
            RefreshThreads();
        };
        FillGeneral();
    }

    // ---- Layout helpers ----

    static DataGrid MakeGrid()
    {
        var g = new DataGrid { SelectionMode = DataGridSelectionMode.Extended };
        Ui.SetupGrid(g);
        return g;
    }

    static TextBlock Note() => new()
    {
        Margin = new Thickness(4, 8, 4, 8),
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.FindResource("SubtleBrush"),
        Visibility = Visibility.Collapsed,
    };

    static TabItem Tab(string header, DataGrid grid, TextBlock note)
    {
        var panel = new DockPanel();
        DockPanel.SetDock(note, Dock.Top);
        panel.Children.Add(note);
        panel.Children.Add(grid);
        return new TabItem { Header = header, Content = panel };
    }

    static void Columns(DataGrid g, params (string Header, string Path, double Width)[] cols)
    {
        var text = (Style)Application.Current.FindResource("CellText");
        foreach (var (h, path, w) in cols)
            g.Columns.Add(new DataGridTextColumn
            {
                Header = h,
                Binding = new Binding(path),
                ElementStyle = text,
                Width = w < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(w),
            });
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => Ui.Copy(string.Join(Environment.NewLine, g.SelectedItems.Cast<object>().Select(o =>
            string.Join("\t", cols.Select(c => o.GetType().GetProperty(c.Path)?.GetValue(o)?.ToString() ?? "")))));
        g.ContextMenu = new ContextMenu();
        g.ContextMenu.Items.Add(copy);
    }

    static DataGridTextColumn Num(string header, string path, string sort, double width) => new()
    {
        Header = header,
        Binding = new Binding(path),
        SortMemberPath = sort,
        Width = width,
        ElementStyle = (Style)Application.Current.FindResource("NumText"),
    };

    FrameworkElement Header()
    {
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Source = _p.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = _p.Name, FontSize = 18, FontWeight = FontWeights.Bold });
        var sub = string.Join("  ·  ", new[] { _p.Description, _p.Company, $"PID {_p.Pid}", _p.User }.Where(s => s.Length > 0));
        text.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Subtle"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(_signer);
        Grid.SetColumn(text, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(Button("Open file location", () => Ui.OpenFileLocation(_p.Path)));
        buttons.Children.Add(Button("Set affinity…", () => new AffinityDialog(_p.Pid, _p.Name) { Owner = this }.ShowDialog()));
        buttons.Children.Add(Button("Create memory dump…", () => ProcessActions.CreateDump(this, _p.Pid, _p.Name)));
        Grid.SetColumn(buttons, 2);

        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(buttons);
        ShowSignature(_p.Path.Length > 0 ? Signatures.GetOrQueue(_p.Path) : null);
        if (_p.Path.Length > 0) Signatures.Verified += OnVerified;
        Closed += (_, _) => Signatures.Verified -= OnVerified;
        return grid;
    }

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    void OnVerified(string path, SignatureInfo info)
    {
        if (string.Equals(path, _p.Path, StringComparison.OrdinalIgnoreCase)) Dispatcher.BeginInvoke(() => ShowSignature(info));
    }

    void ShowSignature(SignatureInfo? s)
    {
        string key = s?.State switch
        {
            SignatureState.Valid => "GoodBrush",
            SignatureState.Unsigned => "WarnBrush",
            SignatureState.Untrusted => "BadBrush",
            _ => "SubtleBrush",
        };
        _signer.Foreground = (Brush)FindResource(key);
        _signer.Text = s == null
            ? (_p.Path.Length > 0 ? "Checking signature…" : "")
            : s.State switch
            {
                SignatureState.Valid => $"Verified signer: {s.Text}" + (s.ViaCatalog ? "  (Windows catalog)" : ""),
                SignatureState.Unsigned => "Not signed: Windows can't confirm who made this program",
                SignatureState.Untrusted => $"Signature present but not trusted{(s.Signer.Length > 0 ? $" ({s.Signer})" : "")}",
                _ => "Couldn't check the signature",
            };
    }

    // ---- General ----

    void FillGeneral()
    {
        _general.Children.Clear();
        var parent = SystemMonitor.Instance.Latest?.Processes.FirstOrDefault(x => x.Pid == _p.ParentPid && x.CreateTime <= _p.CreateTime);
        string version = "";
        if (_p.Path.Length > 0)
        {
            try { version = FileVersionInfo.GetVersionInfo(_p.Path).FileVersion ?? ""; } catch { }
        }
        string affinity = ProcessInspector.GetAffinity(_p.Pid, out var mask, out var system, out _)
            ? mask == system ? "All processors" : $"{System.Numerics.BitOperations.PopCount(mask)} of {System.Numerics.BitOperations.PopCount(system)} processors"
            : "";
        var rows = new (string, string)[]
        {
            ("Path", _p.Path),
            ("Command line", _p.CommandLine),
            ("Current directory", _currentDir ?? ""),
            ("Parent", parent != null ? $"{parent.Name} (PID {parent.Pid})" : $"PID {_p.ParentPid} (no longer running)"),
            ("User", _p.User),
            ("Session", _p.SessionId.ToString()),
            ("Started", _p.Started),
            ("Priority", _p.Priority),
            ("Affinity", affinity),
            ("Description", _p.Description),
            ("Company", _p.Company),
            ("Version", version),
        };
        foreach (var (key, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = key, Style = (Style)FindResource("StatLabel"), Margin = new Thickness(0, 3, 0, 0) });
            var box = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0) };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            _general.Children.Add(row);
        }
    }

    // ---- Threads ----

    void SetupThreadGrid()
    {
        _threadGrid.ItemsSource = _threads;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_threads);
        view.IsLiveSorting = true;
        Columns(_threadGrid, ("Name", "Name", 180.0), ("State", "State", 170.0));
        _threadGrid.Columns.Insert(0, Num("TID", "Tid", "Tid", 70));
        _threadGrid.Columns.Add(Num("Priority", "Priority", "Priority", 80));
        _threadGrid.Columns.Add(Num("CPU", "CpuText", "Cpu", 70));
        _threadGrid.Columns.Add(Num("CPU time", "CpuTimeText", "CpuTime", 100));
        _threadGrid.Columns.Add(Num("Switches", "Switches", "Switches", 90));
        _threadGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Start address",
            Binding = new Binding("Start"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = (Style)FindResource("CellText"),
        });
        Ui.SortBy(_threadGrid, nameof(ThreadRow.CpuTime), ListSortDirection.Descending);
    }

    async void RefreshThreads()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var list = await Task.Run(() => ProcessInspector.Threads(_p.Pid, _p.CreateTime));
            if (list == null)
            {
                _threadNote.Text = "This process has exited.";
                _threadNote.Visibility = Visibility.Visible;
                _timer.Stop();
                return;
            }
            var now = DateTime.UtcNow;
            double elapsed = _prevTime == default ? 0 : (now - _prevTime).TotalSeconds * 1e7 * Environment.ProcessorCount;
            var byTid = _threads.ToDictionary(t => t.Tid);
            var next = new Dictionary<int, long>();
            foreach (var t in list)
            {
                if (!byTid.TryGetValue(t.Tid, out var row))
                {
                    row = new ThreadRow { Tid = t.Tid };
                    _threads.Add(row);
                }
                row.Name = t.Name;
                row.State = t.State;
                row.Priority = t.Priority;
                row.Cpu = elapsed > 0 && _prevCpu.TryGetValue(t.Tid, out var prev) ? Math.Clamp((t.CpuTime - prev) / elapsed * 100, 0, 100) : 0;
                row.CpuTime = t.CpuTime;
                row.Switches = t.ContextSwitches;
                row.Start = ProcessInspector.Symbolize(t.StartAddress, _moduleList);
                next[t.Tid] = t.CpuTime;
            }
            for (int i = _threads.Count - 1; i >= 0; i--)
                if (!next.ContainsKey(_threads[i].Tid)) _threads.RemoveAt(i);
            _prevCpu = next;
            _prevTime = now;
            _threadTab.Header = $"Threads ({list.Count})";
            _threadNote.Text = "Refreshes every 2 seconds. Names and start addresses need permission to open each thread.";
            _threadNote.Visibility = Visibility.Visible;
            if (_tabs.SelectedItem == _threadTab) _timer.Start();
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---- Modules ----

    void SetupModuleGrid()
    {
        _moduleGrid.ItemsSource = _modules;
        Columns(_moduleGrid, ("Name", "Name", 170.0), ("Description", "Description", 200.0), ("Company", "Company", 140.0), ("Version", "Version", 100.0), ("Path", "Path", -1.0));
        _moduleGrid.Columns.Insert(1, Num("Base", "BaseText", "Base", 130));
        _moduleGrid.Columns.Insert(2, Num("Size", "SizeText", "Size", 80));
    }

    async void ShowModules()
    {
        if (_modulesShown) return;
        _modulesShown = true;
        string? error = null;
        _moduleList ??= await Task.Run(() => ProcessInspector.Modules(_p.Pid, out error));
        if (_moduleList == null)
        {
            _moduleNote.Text = $"Can't list modules: {error ?? "access denied"}." + (MainWindow.IsAdmin ? "" : " Restart Tman as administrator to inspect processes you don't own.");
            _moduleNote.Visibility = Visibility.Visible;
            return;
        }
        foreach (var m in _moduleList) _modules.Add(new ModuleRow { Module = m });
        _moduleTab.Header = $"Modules ({_modules.Count})";
        var rows = _modules.ToList();
        var details = await Task.Run(() => rows.Select(r =>
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(r.Path);
                return (v.FileDescription ?? "", v.CompanyName ?? "", v.FileVersion ?? "");
            }
            catch { return ("", "", ""); }
        }).ToList());
        for (int i = 0; i < rows.Count; i++) (rows[i].Description, rows[i].Company, rows[i].Version) = details[i];
    }

    // ---- Environment ----

    void FillEnvironment(List<KeyValuePair<string, string>>? vars, string? error)
    {
        _envLoaded = true;
        if (vars == null)
        {
            _envNote.Text = $"Can't read the environment: {error ?? "access denied"}." + (MainWindow.IsAdmin ? "" : " Restart Tman as administrator to inspect processes you don't own.");
            _envNote.Visibility = Visibility.Visible;
            return;
        }
        _envGrid.ItemsSource = vars.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).ToList();
        _envTab.Header = $"Environment ({vars.Count})";
    }

    // ---- Open files ----

    async void ShowFiles()
    {
        if (_filesLoaded) return;
        _filesLoaded = true;
        _fileNote.Text = "Looking for open files…";
        _fileNote.Visibility = Visibility.Visible;
        var scan = await Task.Run(() => Handles.Scan(_ => true, includeModules: false, onlyPid: _p.Pid, CancellationToken.None));
        if (scan.Denied > 0)
        {
            _fileNote.Text = "Can't look at this process's handles." + (MainWindow.IsAdmin ? "" : " Restart Tman as administrator to inspect processes you don't own.");
            return;
        }
        _fileGrid.ItemsSource = scan.Results.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        _fileTab.Header = $"Open files ({scan.Results.Count})";
        _fileNote.Text = $"Files and folders this process has open right now (checked in {scan.Elapsed.TotalSeconds:0.0} s). Loaded DLLs are on the Modules tab.";
    }

    void OnTabChanged()
    {
        _timer.Stop();
        if (_tabs.SelectedItem == _threadTab) RefreshThreads();
        else if (_tabs.SelectedItem == _moduleTab) ShowModules();
        else if (_tabs.SelectedItem == _fileTab) ShowFiles();
        else if (_tabs.SelectedItem == _envTab && !_envLoaded) _envNote.Text = "Loading…";
    }

    /// <summary>Dev aid for --snap: switch to a tab by index.</summary>
    internal void SelectTab(int index) => _tabs.SelectedIndex = index;
}
