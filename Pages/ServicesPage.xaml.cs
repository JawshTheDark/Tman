using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

public sealed class ServiceItem : ObservableObject
{
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string PathName { get; init; } = "";
    public string Account { get; init; } = "";

    string _state = "";
    public string State
    {
        get => _state;
        set { if (Set(ref _state, value)) Raise(nameof(StateBrush)); }
    }
    public Brush StateBrush => (Brush)Application.Current.FindResource(_state == "Running" ? "GoodBrush" : _state == "Stopped" ? "SubtleBrush" : "WarnBrush");

    string _startMode = "";
    public string StartMode { get => _startMode; set => Set(ref _startMode, value); }

    int _pid;
    public int Pid { get => _pid; set { if (Set(ref _pid, value)) Raise(nameof(PidText)); } }
    public string PidText => _pid > 0 ? _pid.ToString() : "";

    public string? ExePath
    {
        get
        {
            if (PathName.Length == 0) return null;
            var f = Ui.SplitCommand(PathName).File;
            return File.Exists(f) ? f : null;
        }
    }
}

public partial class ServicesPage : UserControl, IPage
{
    readonly ObservableCollection<ServiceItem> _items = new();
    readonly ICollectionView _view;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    string _filter = "";
    bool _busy;

    public ServicesPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(ServiceItem.Name), ListSortDirection.Ascending));
        _view.Filter = Matches;
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
            UpdateCount();
        };
        _timer.Tick += async (_, _) => await RefreshStates();
        Load();
    }

    public void OnShown() => _timer.Start();
    public void OnHidden() => _timer.Stop();
    public void FocusSearch() => Search.FocusBox();

    bool Matches(object o)
    {
        var s = (ServiceItem)o;
        var state = StateFilter?.SelectedIndex ?? 0;
        if (state == 1 && s.State != "Running") return false;
        if (state == 2 && s.State != "Stopped") return false;
        return _filter.Length == 0 || Ui.Contains(s.Name, _filter) || Ui.Contains(s.DisplayName, _filter) || Ui.Contains(s.Description, _filter)
               || s.PidText == _filter;
    }

    void StateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view == null) return;
        _view.Refresh();
        UpdateCount();
    }

    void UpdateCount()
    {
        var running = _items.Count(s => s.State == "Running");
        CountText.Text = $"{_items.Count} services · {running} running";
    }

    async void Load()
    {
        CountText.Text = "Loading…";
        var items = await Task.Run(() => Wmi.Query("SELECT Name, DisplayName, State, StartMode, ProcessId, Description, PathName, StartName FROM Win32_Service")
            .Select(o => new ServiceItem
            {
                Name = o.Str("Name"),
                DisplayName = o.Str("DisplayName"),
                Description = o.Str("Description"),
                PathName = o.Str("PathName"),
                Account = o.Str("StartName"),
                State = o.Str("State"),
                StartMode = o.Str("StartMode") == "Auto" ? "Automatic" : o.Str("StartMode"),
                Pid = (int)o.U64("ProcessId"),
            }).ToList());
        var selected = (List.SelectedItem as ServiceItem)?.Name;
        _items.Clear();
        foreach (var i in items) _items.Add(i);
        if (selected != null) List.SelectedItem = _items.FirstOrDefault(i => i.Name == selected);
        UpdateCount();
    }

    /// <summary>Cheap status-only refresh via the service control manager (no WMI).</summary>
    async Task RefreshStates()
    {
        if (_busy) return;
        var states = await Task.Run(() =>
        {
            try { return ServiceController.GetServices().ToDictionary(s => s.ServiceName, s => Pretty(s.Status), StringComparer.OrdinalIgnoreCase); }
            catch { return new Dictionary<string, string>(); }
        });
        foreach (var i in _items)
            if (states.TryGetValue(i.Name, out var st))
            {
                if (i.State != st && st == "Stopped") i.Pid = 0;
                i.State = st;
            }
        UpdateCount();
        List_SelectionChanged(this, null!);
    }

    static string Pretty(ServiceControllerStatus s) => s switch
    {
        ServiceControllerStatus.StartPending => "Starting",
        ServiceControllerStatus.StopPending => "Stopping",
        ServiceControllerStatus.ContinuePending => "Resuming",
        ServiceControllerStatus.PausePending => "Pausing",
        _ => s.ToString(),
    };

    ServiceItem? Selected => List.SelectedItem as ServiceItem;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var s = Selected;
        StartButton.IsEnabled = !_busy && s != null && s.State == "Stopped" && s.StartMode != "Disabled";
        StopButton.IsEnabled = !_busy && s != null && s.State == "Running";
        RestartButton.IsEnabled = !_busy && s != null && s.State == "Running";
        if (s == null)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsPane.Visibility = Visibility.Visible;
        DetailTitle.Text = s.DisplayName;
        DetailDesc.Text = s.Description;
        DetailDesc.Visibility = s.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailPath.Text = s.PathName;
        DetailPath.ToolTip = s.PathName;
    }

    void Start_Click(object sender, RoutedEventArgs e) => Control("start");
    void Stop_Click(object sender, RoutedEventArgs e) => Control("stop");
    void Restart_Click(object sender, RoutedEventArgs e) => Control("restart");

    async void Control(string action)
    {
        if (Selected is not { } s || _busy) return;
        if (action != "start" && !Ui.Confirm($"{(action == "stop" ? "Stop" : "Restart")} the service \"{s.DisplayName}\"?\n\nOther services or apps that depend on it may stop working.")) return;
        _busy = true;
        List_SelectionChanged(this, null!);
        CountText.Text = $"{(action == "start" ? "Starting" : action == "stop" ? "Stopping" : "Restarting")} {s.DisplayName}…";
        var error = await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(s.Name);
                var timeout = TimeSpan.FromSeconds(30);
                if (action is "stop" or "restart")
                {
                    sc.Stop(stopDependentServices: true);
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                }
                if (action is "start" or "restart")
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.InnerException?.Message ?? ex.Message;
            }
        });
        _busy = false;
        if (error != null)
            Ui.Error($"Couldn't {action} {s.DisplayName}:\n\n{error}" + (MainWindow.IsAdmin ? "" : "\n\nManaging services needs administrator rights. Use File > Restart as administrator."));
        Load();
    }

    async void StartMode_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var mode = (string)((MenuItem)sender).Tag;
        if (mode == "Disabled" && !Ui.Confirm($"Disable \"{s.DisplayName}\"? Windows features that depend on it may stop working.")) return;
        var code = await Task.Run(() =>
        {
            try
            {
                using var mo = new ManagementObject($"Win32_Service.Name='{s.Name.Replace("'", "\\'")}'");
                return Convert.ToUInt32(mo.InvokeMethod("ChangeStartMode", new object[] { mode }));
            }
            catch
            {
                return 2u;
            }
        });
        if (code == 0) s.StartMode = mode;
        else Ui.Error(code == 2 ? "Changing the startup type needs administrator rights. Use File > Restart as administrator." : $"Couldn't change the startup type (error {code}).");
    }

    void OpenMmc_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true }); } catch { }
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(Selected?.ExePath);

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Ui.SearchOnline($"{s.Name} service {s.DisplayName}");
    }

    void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Ui.Copy(s.Name);
    }
}
