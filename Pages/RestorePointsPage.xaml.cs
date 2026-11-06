using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public sealed record RestorePointEntry(uint Sequence, DateTime? Created, string Description, string Type)
{
    public string DateText => Created?.ToString("g") ?? "";
}

public partial class RestorePointsPage : UserControl, IPage
{
    const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    const string ClientsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    const string SystemRestoreClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";
    const int DefaultLimitMinutes = 1440;
    const uint ModifySettings = 12, BeginSystemChange = 100;

    List<RestorePointEntry> _points = new();
    int _limitMinutes = DefaultLimitMinutes;
    bool _protectionOff, _creating;

    public RestorePointsPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        Load();
    }

    async void Load()
    {
        CreateButton.IsEnabled = false;
        StatusText.Text = "Loading…";
        var (points, error, protection, limit) = await Task.Run(() =>
        {
            var (list, why) = ReadPoints();
            return (list, why, ReadProtection(), ReadLimitMinutes());
        });
        StatusText.Text = "";
        _limitMinutes = limit;
        ShowProtection(protection);
        ShowLimit();
        Show(points, error);
    }

    void Show(List<RestorePointEntry> points, string? error)
    {
        _points = points;
        List.ItemsSource = points;
        Ui.SortBy(List, nameof(RestorePointEntry.Created), ListSortDirection.Descending);
        CreateButton.IsEnabled = MainWindow.IsAdmin && !_creating;
        EmptyText.Text = error ?? (_protectionOff
            ? "There are no restore points, and System Protection is off, so Windows won't save any until you turn it on."
            : "There are no restore points yet. Windows makes them before big changes, or you can create one now.");
        EmptyText.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Reads the list directly, because <see cref="Wmi.Query"/> can't tell "access denied" from "there are none".</summary>
    static (List<RestorePointEntry> Points, string? Error) ReadPoints()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber, CreationTime, Description, RestorePointType FROM SystemRestore");
            var points = searcher.Get().Cast<ManagementBaseObject>()
                .Select(o => new RestorePointEntry((uint)o.U64("SequenceNumber"), o.Date("CreationTime"), o.Str("Description"), TypeName(o.U64("RestorePointType"))))
                .ToList();
            return (points, null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return (new(), "Windows only shows the list of restore points to administrators. Restart Tman as administrator to see them.");
        }
        catch (UnauthorizedAccessException)
        {
            return (new(), "Windows only shows the list of restore points to administrators. Restart Tman as administrator to see them.");
        }
        catch (Exception ex) when (ex is ManagementException or COMException)
        {
            return (new(), $"Windows couldn't list the restore points: {ex.Message}");
        }
    }

    static string TypeName(ulong type) => type switch
    {
        0 => "App install",
        1 => "App uninstall",
        6 => "System Restore",
        7 => "Scheduled",
        10 => "Driver install",
        12 => "Manual",
        13 => "Cancelled change",
        14 => "Backup recovery",
        16 => "Manual",
        17 => "Windows Update",
        18 => "Critical update",
        _ => $"Type {type}",
    };

    /// <summary>
    /// The per-drive list is only readable as administrator. Standard users can still read the machine-wide switch,
    /// which is 0 when protection is off on every drive.
    /// </summary>
    static (bool? On, string Text) ReadProtection()
    {
        var drive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\');
        int? anyDrive = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey);
            anyDrive = key?.GetValue("RPSessionInterval") as int?;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        if (anyDrive == 0) return (false, "Off on every drive, so Windows isn't saving restore points. Turn it on in System Protection settings.");

        try
        {
            using var clients = Registry.LocalMachine.OpenSubKey(ClientsKey);
            if (clients != null)
            {
                var volume = VolumeId(drive + "\\");
                var volumes = clients.GetValue(SystemRestoreClient) as string[] ?? Array.Empty<string>();
                bool on = volumes.Any(v => v.Contains($"({drive[0]}%3A)", StringComparison.OrdinalIgnoreCase)
                                           || (volume != null && v.Contains(volume, StringComparison.OrdinalIgnoreCase)));
                return (on, on
                    ? $"On for the system drive ({drive}), so Windows saves restore points before big changes."
                    : $"Off for the system drive ({drive}), so Windows isn't saving restore points for it. Turn it on in System Protection settings.");
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        return anyDrive == null
            ? (null, "Windows didn't say whether it's on.")
            : (true, "On for at least one drive. Only an administrator can see whether that includes the system drive.");
    }

    static string? VolumeId(string root)
    {
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity)) return null;
        var text = name.ToString();
        int open = text.IndexOf('{'), close = text.IndexOf('}');
        return open >= 0 && close > open ? text[open..(close + 1)] : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int size);

    /// <summary>Windows' default applies when the value is missing. 0 means no limit.</summary>
    static int ReadLimitMinutes()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey);
            return key?.GetValue("SystemRestorePointCreationFrequency") is int minutes ? minutes : DefaultLimitMinutes;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return DefaultLimitMinutes;
        }
    }

    static string LimitName(int minutes) =>
        minutes % 60 == 0 ? minutes == 60 ? "1 hour" : $"{minutes / 60} hours" : minutes == 1 ? "1 minute" : $"{minutes} minutes";

    void ShowProtection((bool? On, string Text) p)
    {
        _protectionOff = p.On == false;
        ProtectionValue.Text = p.On switch { true => "On", false => "Off", _ => "Unknown" };
        ProtectionValue.Foreground = (Brush)FindResource(p.On switch { true => "GoodBrush", false => "BadBrush", _ => "WarnBrush" });
        ProtectionText.Text = p.Text;
    }

    void ShowLimit()
    {
        LimitValue.Text = _limitMinutes == 0 ? "No limit" : $"Every {LimitName(_limitMinutes)}";
        LimitText.Text = _limitMinutes == 0
            ? "This PC has the limit turned off, so a new one is created every time."
            : _limitMinutes == DefaultLimitMinutes
                ? "Windows' default. A request within 24 hours of the last restore point is quietly skipped."
                : $"Set on this PC. A request within {LimitName(_limitMinutes)} of the last restore point is quietly skipped.";
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    async void Create_Click(object sender, RoutedEventArgs e)
    {
        var last = _points.Where(p => p.Created != null).Select(p => p.Created!.Value).DefaultIfEmpty().Max();
        bool tooSoon = _limitMinutes > 0 && last != default && DateTime.Now - last < TimeSpan.FromMinutes(_limitMinutes);
        var warning = tooSoon ? $"\n\nWindows will probably skip this, because the last one was made {last:g} and it allows one every {LimitName(_limitMinutes)}." : "";
        if (!Ui.Confirm($"Create a restore point now?\n\nWindows saves a snapshot of system files, the registry and installed apps, which takes a minute or two and uses disk space. Your documents aren't included.{warning}")) return;

        _creating = true;
        CreateButton.IsEnabled = false;
        StatusText.Text = "Creating a restore point…";
        uint newest = _points.Count == 0 ? 0 : _points.Max(p => p.Sequence);
        var (code, error) = await Task.Run(() => CreatePoint($"Created in Tman {DateTime.Now:g}"));
        var (points, listError) = await Task.Run(ReadPoints);
        _creating = false;
        StatusText.Text = "";
        Show(points, listError);

        if (error != null) Ui.Error($"The restore point wasn't created.\n\n{error}");
        else if (code is 1058 or 0x80070422) Ui.Error("The restore point wasn't created, because System Protection is off. Turn it on in System Protection settings first.");
        else if (code != 0) Ui.Error($"The restore point wasn't created. Windows reported error 0x{code:X8}.");
        else if (points.Any(p => p.Sequence > newest)) StatusText.Text = "Restore point created.";
        else if (_limitMinutes > 0)
            Ui.Error($"Windows didn't create a new restore point. It reports success even when it skips one: it allows one every {LimitName(_limitMinutes)}, and there's already a recent one. The latest restore point is still there to use.");
        else
            Ui.Error("Windows reported success, but no new restore point appeared. Check that System Protection is on for the system drive.");
    }

    static (uint Code, string? Error) CreatePoint(string description)
    {
        try
        {
            using var systemRestore = new ManagementClass(@"root\default", "SystemRestore", null);
            using var input = systemRestore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = systemRestore.InvokeMethod("CreateRestorePoint", input, null);
            return (Convert.ToUInt32(output["ReturnValue"]), null);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return (0, ex.Message);
        }
    }

    void OpenRestore_Click(object sender, RoutedEventArgs e) => Open("rstrui.exe");

    void OpenSettings_Click(object sender, RoutedEventArgs e) => Open("SystemPropertiesProtection.exe");

    static void Open(string tool)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Cmd.System(tool)) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // the user said no to Windows' permission prompt
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Ui.Error($"Couldn't open {tool}: {ex.Message}");
        }
    }
}
