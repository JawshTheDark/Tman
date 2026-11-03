using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public enum StartupKind
{
    /// <summary>Run keys and Startup folders: switched on/off through StartupApproved, like Task Manager.</summary>
    Approved,
    /// <summary>Scheduled task with a sign-in or startup trigger: switched by enabling/disabling the task.</summary>
    Task,
    /// <summary>RunOnce, policies, Winlogon, AppInit: shown for awareness, can't be safely toggled.</summary>
    Fixed,
}

public sealed class StartupItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Source { get; init; }
    public string? FilePath { get; init; }
    public string Publisher { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public StartupKind Kind { get; init; }

    // Approved: where the enabled/disabled flag lives (same place Windows Task Manager and Settings use).
    public RegistryKey? ApprovedRoot { get; init; }
    public string? ApprovedKey { get; init; }
    public string? ValueName { get; init; }

    /// <summary>Full registry path for registry-based entries ("Open in Registry Editor").</summary>
    public string? RegistryPath { get; init; }
    /// <summary>Task Scheduler path for scheduled tasks.</summary>
    public string? TaskPath { get; init; }
    /// <summary>Windows' own scheduled tasks; hidden unless asked for.</summary>
    public bool IsWindows { get; init; }

    bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); } }
    }
    public string StatusText => Kind == StartupKind.Fixed ? "Always" : _enabled ? "Enabled" : "Disabled";
    public Brush StatusBrush => (Brush)Application.Current.FindResource(Kind == StartupKind.Fixed ? "LabelBrush" : _enabled ? "GoodBrush" : "SubtleBrush");
}

public partial class StartupPage : UserControl, IPage
{
    const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    readonly ObservableCollection<StartupItem> _items = new();
    readonly ICollectionView _view;
    string _filter = "";

    public StartupPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => o is StartupItem s
            && (!s.IsWindows || ShowWindows.IsChecked == true)
            && (_filter.Length == 0 || Ui.Contains(s.Name, _filter) || Ui.Contains(s.Publisher, _filter) || Ui.Contains(s.Command, _filter) || Ui.Contains(s.Source, _filter));
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
            UpdateCount();
        };
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    async void Load()
    {
        CountText.Text = "Loading…";
        var items = await Task.Run(Read);
        _items.Clear();
        foreach (var i in items.OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)) _items.Add(i);
        UpdateCount();
    }

    void UpdateCount()
    {
        int shown = _view.Cast<object>().Count(), hidden = _items.Count(i => i.IsWindows);
        CountText.Text = $"{shown} startup items · {_view.Cast<StartupItem>().Count(i => i.Enabled)} enabled"
            + (ShowWindows.IsChecked == true || hidden == 0 ? "" : $" · {hidden} Windows tasks hidden");
    }

    static List<StartupItem> Read()
    {
        var list = new List<StartupItem>();
        var hkcu = Registry.CurrentUser;
        var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        ReadRun(list, hkcu, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry · current user");
        ReadRun(list, hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "Registry · all users");
        ReadRun(list, hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Registry · all users (32-bit)");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), hkcu, "Startup folder · current user");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), hklm, "Startup folder · all users");

        // Less obvious places programs (and malware) use to start with Windows.
        ReadRun(list, hkcu, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", null, "RunOnce · next sign-in");
        ReadRun(list, hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", null, "RunOnce · next startup");
        ReadRun(list, hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", null, "RunOnce · next startup (32-bit)");
        ReadRun(list, hkcu, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", null, "Group Policy · current user");
        ReadRun(list, hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", null, "Group Policy · all users");
        ReadWinlogon(list, hklm, hkcu);
        ReadAppInit(list, hklm, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit DLLs");
        ReadAppInit(list, hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit DLLs (32-bit)");
        try { ReadTasks(list); } catch { }
        return list;
    }

    static string Hive(RegistryKey root) => root.Name.StartsWith("HKEY_CURRENT_USER", StringComparison.Ordinal) ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";

    static void ReadRun(List<StartupItem> list, RegistryKey root, string path, string? approvedKey, string source)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string cmd || cmd.Trim().Length == 0) continue;
            var file = ExistingFile(Ui.SplitCommand(cmd).File);
            list.Add(Make(name, cmd, file, source, approvedKey == null ? StartupKind.Fixed : StartupKind.Approved,
                $@"{Hive(root)}\{path}", root, approvedKey, name));
        }
    }

    static void ReadFolder(List<StartupItem> list, string folder, RegistryKey root, string source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(f);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(f) : f;
            list.Add(Make(Path.GetFileNameWithoutExtension(f), target ?? f, target != null && File.Exists(target) ? target : f, source,
                StartupKind.Approved, null, root, "StartupFolder", fileName));
        }
    }

    /// <summary>Replacement shells and extra Userinit programs (anything other than the Windows defaults).</summary>
    static void ReadWinlogon(List<StartupItem> list, RegistryKey hklm, RegistryKey hkcu)
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        foreach (var (root, label) in new[] { (hklm, "all users"), (hkcu, "current user") })
        {
            using var key = root.OpenSubKey(path);
            if (key == null) continue;
            if (key.GetValue("Shell") is string shell && shell.Trim().Length > 0 && !shell.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                list.Add(Make("Winlogon shell", shell, ExistingFile(Ui.SplitCommand(shell).File), $"Winlogon · {label}", StartupKind.Fixed, $@"{Hive(root)}\{path}"));
            if (root == hklm && key.GetValue("Userinit") is string init)
                foreach (var part in init.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!Path.GetFileName(part.Trim('"')).Equals("userinit.exe", StringComparison.OrdinalIgnoreCase))
                        list.Add(Make("Winlogon userinit", part, ExistingFile(Ui.SplitCommand(part).File), $"Winlogon · {label}", StartupKind.Fixed, $@"{Hive(root)}\{path}"));
        }
    }

    /// <summary>DLLs injected into every program that loads user32 (only when LoadAppInit_DLLs is on).</summary>
    static void ReadAppInit(List<StartupItem> list, RegistryKey hklm, string path, string source)
    {
        using var key = hklm.OpenSubKey(path);
        if (key?.GetValue("AppInit_DLLs") is not string dlls || dlls.Trim().Length == 0) return;
        if (key.GetValue("LoadAppInit_DLLs") is int load && load == 0) return;
        foreach (var dll in dlls.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            list.Add(Make(Path.GetFileName(dll.Trim('"')), dll, ExistingFile(dll.Trim('"')), source, StartupKind.Fixed, $@"HKEY_LOCAL_MACHINE\{path}"));
    }

    /// <summary>Scheduled tasks that run at sign-in or at startup, through the Task Scheduler COM API.</summary>
    static void ReadTasks(List<StartupItem> list)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null) return;
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            WalkTasks(list, service.GetFolder("\\"));
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    static void WalkTasks(List<StartupItem> list, dynamic folder)
    {
        try
        {
            foreach (dynamic task in folder.GetTasks(1 /* include hidden */))
            {
                try { AddTask(list, task); } catch { }
            }
        }
        catch { }
        try
        {
            foreach (dynamic sub in folder.GetFolders(0)) WalkTasks(list, sub);
        }
        catch { }
    }

    static void AddTask(List<StartupItem> list, dynamic task)
    {
        dynamic def = task.Definition;
        string? when = null;
        foreach (dynamic trigger in def.Triggers)
        {
            int kind = trigger.Type;
            if (kind == 9) { when = "at sign-in"; break; } // TASK_TRIGGER_LOGON
            if (kind == 8) when ??= "at startup";         // TASK_TRIGGER_BOOT
        }
        if (when == null) return;

        string exe = "", command = "";
        foreach (dynamic action in def.Actions)
        {
            if ((int)action.Type != 0) continue; // only "start a program" actions
            exe = Environment.ExpandEnvironmentVariables(((string?)action.Path ?? "").Trim().Trim('"'));
            string args = (string?)action.Arguments ?? "";
            command = args.Length > 0 ? $"\"{exe}\" {args}" : exe;
            break;
        }
        if (command.Length == 0) command = "(runs a COM handler)";
        string taskPath = task.Path;
        var file = ExistingFile(exe);
        string author = "";
        try { author = (string?)def.RegistrationInfo.Author ?? ""; } catch { }
        var item = Make((string)task.Name, command, file, $"Scheduled task · {when}", StartupKind.Task, null, taskPath: taskPath,
            isWindows: taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase), fallbackPublisher: author.StartsWith("$(") ? "" : author);
        item.Enabled = (bool)task.Enabled;
        list.Add(item);
    }

    /// <summary>A full path that exists, also trying bare names like "rundll32.exe" in System32.</summary>
    static string? ExistingFile(string file)
    {
        if (file.Length == 0) return null;
        try
        {
            if (File.Exists(file)) return file;
            if (!Path.IsPathRooted(file))
            {
                var sys = Path.Combine(Environment.SystemDirectory, file);
                if (File.Exists(sys)) return sys;
                if (File.Exists(sys + ".exe")) return sys + ".exe";
            }
        }
        catch { }
        return null;
    }

    static StartupItem Make(string name, string cmd, string? file, string source, StartupKind kind, string? registryPath,
        RegistryKey? root = null, string? approvedKey = null, string? valueName = null, string? taskPath = null,
        bool isWindows = false, string fallbackPublisher = "")
    {
        string publisher = "";
        if (file != null)
        {
            try { publisher = FileVersionInfo.GetVersionInfo(file).CompanyName?.Trim() ?? ""; } catch { }
        }
        return new StartupItem
        {
            Name = name,
            Command = cmd,
            Source = source,
            FilePath = file,
            Publisher = publisher.Length > 0 ? publisher : fallbackPublisher,
            Icon = file != null ? Icons.ForFile(file) : Icons.DefaultExe,
            Kind = kind,
            ApprovedRoot = root,
            ApprovedKey = approvedKey,
            ValueName = valueName,
            RegistryPath = registryPath,
            TaskPath = taskPath,
            IsWindows = isWindows,
            Enabled = kind != StartupKind.Approved || IsApproved(root!, approvedKey!, valueName!),
        };
    }

    static bool IsApproved(RegistryKey root, string approvedKey, string valueName)
    {
        using var key = root.OpenSubKey(ApprovedBase + approvedKey);
        // Missing value = enabled. Odd first byte (0x03/0x07...) = disabled.
        return key?.GetValue(valueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
    }

    static string? ResolveShortcut(string lnk)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            dynamic sc = ((dynamic)shell!).CreateShortcut(lnk);
            string target = sc.TargetPath;
            string args = sc.Arguments;
            Marshal.FinalReleaseComObject(sc);
            return string.IsNullOrEmpty(target) ? null : string.IsNullOrEmpty(args) ? target : $"\"{target}\" {args}";
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    StartupItem? Selected => List.SelectedItem as StartupItem;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ToggleButton.IsEnabled = Selected is { Kind: not StartupKind.Fixed };
        ToggleButton.Content = Selected?.Enabled == false ? "Enable" : "Disable";
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        if (item.Kind == StartupKind.Fixed)
        {
            Ui.Error($"Entries from \"{item.Source}\" can't be switched off here. Remove it with the program that added it, " +
                     "or right-click it and choose Open in Registry Editor.");
            return;
        }
        var enable = !item.Enabled;
        try
        {
            if (item.Kind == StartupKind.Task) SetTaskEnabled(item.TaskPath!, enable);
            else
            {
                using var key = item.ApprovedRoot!.CreateSubKey(ApprovedBase + item.ApprovedKey, writable: true);
                var data = new byte[12];
                data[0] = (byte)(enable ? 0x02 : 0x03);
                if (!enable) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
                key.SetValue(item.ValueName, data, RegistryValueKind.Binary);
            }
            item.Enabled = enable;
            List_SelectionChanged(this, null!);
            UpdateCount();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or COMException)
        {
            Ui.Error(item.Kind == StartupKind.Task
                ? "Windows didn't allow changing this scheduled task. Tasks created by Windows or other users need administrator rights.\n\nUse File > Restart as administrator."
                : "Changing startup apps for all users needs administrator rights.\n\nUse File > Restart as administrator.");
        }
    }

    static void SetTaskEnabled(string taskPath, bool enable)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")!;
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            int cut = taskPath.LastIndexOf('\\');
            dynamic folder = service.GetFolder(cut <= 0 ? "\\" : taskPath[..cut]);
            dynamic task = folder.GetTask(taskPath[(cut + 1)..]);
            task.Enabled = enable;
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    void ShowWindows_Click(object sender, RoutedEventArgs e)
    {
        _view.Refresh();
        UpdateCount();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void OpenLocation_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(Selected?.FilePath);

    /// <summary>Jumps to where the entry is defined: Registry Editor at its key, or Task Scheduler.</summary>
    void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        try
        {
            if (s.TaskPath != null)
            {
                Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true });
                return;
            }
            var path = s.RegistryPath;
            if (s.Kind == StartupKind.Approved && s.FilePath != null && s.Source.StartsWith("Startup folder"))
            {
                Ui.OpenFileLocation(Path.Combine(s.Source.EndsWith("all users")
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                    : Environment.GetFolderPath(Environment.SpecialFolder.Startup), s.ValueName ?? ""));
                return;
            }
            if (path == null) return;
            // Registry Editor opens at the last key it showed, so point that at our key first.
            using (var regedit = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                regedit.SetValue("LastKey", @"Computer\" + path);
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC prompt cancelled.
        }
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Ui.SearchOnline($"{s.Name} startup");
    }

    void Properties_Click(object sender, RoutedEventArgs e) => Ui.ShowProperties(Selected?.FilePath);

    void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } s) Ui.Copy(s.Command);
    }
}
