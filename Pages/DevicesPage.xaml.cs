using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public sealed record DeviceEntry(string Name, string Class, string PnpClass, string Manufacturer, int Code, string Status, string Id)
{
    const int Disabled = 22, NotConnected = 45;

    public bool IsDisabled => Code == Disabled;
    public bool IsMissing => Code == NotConnected;
    public bool IsProblem => (Code != 0 && !IsDisabled && !IsMissing) || (Code == 0 && Status.Length > 0 && Status != "OK");

    /// <summary>Problems first, then disabled devices, then working ones, then anything unplugged.</summary>
    public int Rank => IsProblem ? 0 : IsDisabled ? 1 : IsMissing ? 3 : 2;

    public string StatusText =>
        Code != 0 ? IsProblem ? $"{DeviceProblems.For(Code).Short} (code {Code})" : DeviceProblems.For(Code).Short
        : IsProblem ? $"Status: {Status}"
        : "Working";

    public Brush StatusBrush => (Brush)Application.Current.FindResource(IsProblem ? "BadBrush" : IsDisabled ? "WarnBrush" : IsMissing ? "SubtleBrush" : "GoodBrush");
}

public partial class DevicesPage : UserControl, IPage
{
    const string AllClasses = "All classes";

    List<DeviceEntry> _all = new();
    ICollectionView? _view;
    string _filter = "";
    bool _busy, _filling;

    public DevicesPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        if (!MainWindow.IsAdmin)
        {
            const string tip = "Needs administrator rights. Use File > Restart as administrator.";
            EnableButton.ToolTip = DisableButton.ToolTip = ScanButton.ToolTip = tip;
            ScanButton.IsEnabled = false;
        }
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    DeviceEntry? Selected => List.SelectedItem as DeviceEntry;

    async void Load()
    {
        RefreshButton.IsEnabled = false;
        SetSummary("Loading devices…", "SubtleBrush");
        var previous = Selected?.Id;
        _all = await Task.Run(Read);
        RefreshButton.IsEnabled = true;

        List.ItemsSource = _all;
        _view = CollectionViewSource.GetDefaultView(_all);
        _view.Filter = Matches;
        FillClasses();
        ApplyFilter();
        List.SelectedItem = _all.FirstOrDefault(d => d.Id == previous) ?? _all.FirstOrDefault(d => d.IsProblem);
        if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
        UpdateSelection();
        UpdateSummary();
    }

    static List<DeviceEntry> Read()
    {
        var classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string ClassOf(string guid, string fallback)
        {
            if (guid.Length == 0) return fallback.Length > 0 ? fallback : "Other devices";
            if (!classes.TryGetValue(guid, out var name))
                classes[guid] = name = ClassDescription(guid) ?? (fallback.Length > 0 ? fallback : "Other devices");
            return name;
        }

        return Wmi.Query("SELECT Name, Description, PNPClass, ClassGuid, Manufacturer, ConfigManagerErrorCode, Status, DeviceID FROM Win32_PnPEntity")
            .Where(o => !o.Str("DeviceID").StartsWith(@"HTREE\ROOT", StringComparison.OrdinalIgnoreCase))
            .Select(o =>
            {
                var name = o.Str("Name");
                if (name.Length == 0) name = o.Str("Description");
                if (name.Length == 0) name = o.Str("DeviceID");
                return new DeviceEntry(name, ClassOf(o.Str("ClassGuid"), o.Str("PNPClass")), o.Str("PNPClass"), o.Str("Manufacturer"),
                    (int)o.U64("ConfigManagerErrorCode"), o.Str("Status"), o.Str("DeviceID"));
            })
            .OrderBy(d => d.Rank).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Windows' own name for a device class, the heading Device Manager shows.</summary>
    static string? ClassDescription(string guid)
    {
        if (!Guid.TryParse(guid, out var id)) return null;
        var text = new char[256];
        if (!SetupDiGetClassDescriptionW(ref id, text, text.Length, IntPtr.Zero)) return null;
        var end = Array.IndexOf(text, '\0');
        return end > 0 ? new string(text, 0, end) : null;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetClassDescriptionW(ref Guid classGuid, [Out] char[] description, int size, IntPtr required);

    void FillClasses()
    {
        var current = ClassFilter.SelectedItem as string;
        _filling = true;
        ClassFilter.Items.Clear();
        ClassFilter.Items.Add(AllClasses);
        foreach (var c in _all.Select(d => d.Class).Distinct().Order(StringComparer.OrdinalIgnoreCase))
            ClassFilter.Items.Add(c);
        ClassFilter.SelectedItem = current != null && ClassFilter.Items.Contains(current) ? current : AllClasses;
        _filling = false;
    }

    bool Matches(object o)
    {
        var d = (DeviceEntry)o;
        if (ProblemsOnly.IsChecked == true && !d.IsProblem && !d.IsDisabled) return false;
        if (ClassFilter.SelectedItem is string c && c != AllClasses && d.Class != c) return false;
        return _filter.Length == 0 || Ui.Contains(d.Name, _filter) || Ui.Contains(d.Class, _filter) || Ui.Contains(d.Manufacturer, _filter)
               || Ui.Contains(d.StatusText, _filter) || Ui.Contains(d.Id, _filter);
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_filling) ApplyFilter();
    }

    void ApplyFilter()
    {
        if (_view == null) return;
        _view.Refresh();
        int shown = _view.Cast<object>().Count();
        CountText.Text = shown == _all.Count ? "" : $"{shown} of {_all.Count} shown";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Count == 0 ? "Windows didn't return any devices. The Windows Management Instrumentation service may be stopped."
            : ProblemsOnly.IsChecked == true && !_all.Any(d => d.IsProblem || d.IsDisabled) ? $"No problems found. All {_all.Count} devices are working."
            : "No devices match this filter.";
    }

    void SetSummary(string text, string brush)
    {
        SummaryText.Text = text;
        SummaryText.Foreground = (Brush)FindResource(brush);
    }

    void UpdateSummary()
    {
        if (_all.Count == 0)
        {
            SetSummary("Couldn't list devices.", "WarnBrush");
            return;
        }
        int problems = _all.Count(d => d.IsProblem), disabled = _all.Count(d => d.IsDisabled), missing = _all.Count(d => d.IsMissing);
        var text = problems == 0 ? $"No problems found. {Fmt.Num(_all.Count)} devices listed."
            : problems == 1 ? $"1 device has a problem, out of {Fmt.Num(_all.Count)}."
            : $"{problems} devices have problems, out of {Fmt.Num(_all.Count)}.";
        if (disabled > 0) text += $" {disabled} disabled.";
        if (missing > 0) text += $" {missing} not connected.";
        SetSummary(text, problems > 0 ? "BadBrush" : "GoodBrush");
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    void UpdateSelection()
    {
        var d = Selected;
        EnableItem.IsEnabled = EnableButton.IsEnabled = MainWindow.IsAdmin && !_busy && d is { IsDisabled: true };
        DisableItem.IsEnabled = DisableButton.IsEnabled = MainWindow.IsAdmin && !_busy && d is { IsDisabled: false, IsMissing: false };
        ScanButton.IsEnabled = MainWindow.IsAdmin && !_busy;
        SearchButton.IsEnabled = d != null;
        if (d == null)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsPane.Visibility = Visibility.Visible;
        DetailTitle.Text = d.Name;
        var problem = d.Code != 0 ? DeviceProblems.For(d.Code) : null;
        DetailMeaning.Text = problem?.Meaning ?? (d.IsProblem ? $"Windows reports this device's status as \"{d.Status}\"." : "This device is working properly.");
        DetailMeaning.Foreground = d.StatusBrush;
        DetailFix.Text = problem != null ? "What to try: " + problem.Fix : "What to try: update the driver, or search online for this device.";
        DetailFix.Visibility = d.Code != 0 || d.IsProblem ? Visibility.Visible : Visibility.Collapsed;
        DetailId.Text = d.Id;
        DetailId.ToolTip = d.Id;
    }

    /// <summary>What can go wrong if this device goes away, for the classes where that's more than an inconvenience.</summary>
    static string DisableWarning(DeviceEntry d) => d.PnpClass.ToLowerInvariant() switch
    {
        "diskdrive" or "hdc" or "scsiadapter" or "volume" =>
            "WARNING: this is a storage device. If Windows runs from it or needs it, Windows can crash or stop starting.\n\n",
        "display" =>
            "WARNING: this is a display adapter. Your screen can go black or drop to a low resolution.\n\n",
        "keyboard" =>
            "WARNING: this is a keyboard. If it's your only one, you won't be able to type, even to turn it back on.\n\n",
        "mouse" =>
            "WARNING: this is a mouse or other pointing device. If it's your only one, you won't be able to use the pointer.\n\n",
        "net" =>
            "WARNING: this is a network adapter. Any connection through it will drop, including remote sessions.\n\n",
        _ => "",
    };

    void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsDisabled: true } d || !MainWindow.IsAdmin) return;
        if (!Ui.Confirm($"Enable \"{d.Name}\"?\n\nWindows will load its driver and start using it again. If it was turned off because it caused trouble, the trouble may come back."))
            return;
        RunPnpUtil($"/enable-device \"{d.Id}\"", $"Enabling {d.Name}…", $"Couldn't enable {d.Name}.");
    }

    void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsDisabled: false, IsMissing: false } d || !MainWindow.IsAdmin) return;
        if (!Ui.Confirm($"Disable \"{d.Name}\"?\n\n{DisableWarning(d)}Windows will stop using this device, and anything that relies on it will stop working until you enable it again here or in Device Manager."))
            return;
        RunPnpUtil($"/disable-device \"{d.Id}\"", $"Disabling {d.Name}…", $"Couldn't disable {d.Name}.");
    }

    void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!MainWindow.IsAdmin) return;
        if (!Ui.Confirm("Scan for hardware changes?\n\nWindows will look for new or removed devices and may install drivers for any it finds. Some devices can drop out for a moment."))
            return;
        RunPnpUtil("/scan-devices", "Scanning for hardware changes…", "The scan for hardware changes failed.");
    }

    async void RunPnpUtil(string args, string doing, string failure)
    {
        _busy = true;
        UpdateSelection();
        RefreshButton.IsEnabled = false;
        SetSummary(doing, "SubtleBrush");
        var result = await Task.Run(() => Cmd.Run(Cmd.System("pnputil.exe"), args, 120000));
        _busy = false;
        if (result.ExitCode == 3010)
            Ui.Error("Done, but Windows needs a restart to finish.");
        else if (!result.Ok)
            Ui.Error($"{failure}\n\n{(result.ExitCode == Cmd.Cancelled ? "It took too long and was stopped." : result.Output.Trim())}");
        Load();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void DeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error($"Couldn't open Device Manager:\n\n{ex.Message}"); }
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } d) Ui.SearchOnline(d.Code != 0 ? $"{d.Name} device manager code {d.Code}" : $"{d.Name} driver");
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } d) Ui.Copy($"{d.Name}\t{d.StatusText}\t{d.Class}\t{d.Manufacturer}\t{d.Id}");
    }
}
