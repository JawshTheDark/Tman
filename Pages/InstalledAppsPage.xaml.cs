using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public sealed class AppEntry : ObservableObject
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Installed { get; init; }
    public string InstalledText => Installed?.ToString("d") ?? "";
    public ulong Size { get; init; }
    public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "";
    public string Scope { get; init; } = "";
    public string? UninstallString { get; init; }
    public string? InstallLocation { get; init; }
    public string? DisplayIcon { get; init; }

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
}

public partial class InstalledAppsPage : UserControl, IPage
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    readonly ObservableCollection<AppEntry> _items = new();
    readonly ICollectionView _view;
    string _filter = "";

    public InstalledAppsPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(AppEntry.Name), ListSortDirection.Ascending));
        _view.Filter = o => _filter.Length == 0 || o is AppEntry a && (Ui.Contains(a.Name, _filter) || Ui.Contains(a.Publisher, _filter));
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
        };
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    async void Load()
    {
        CountText.Text = "Loading…";
        var apps = await Task.Run(Read);
        _items.Clear();
        foreach (var a in apps) _items.Add(a);
        var total = apps.Sum(a => (double)a.Size);
        CountText.Text = $"{apps.Count} apps · {Fmt.Bytes(total)} reported size";

        // Icons are the slow part; fill them in after the list is visible.
        var snapshot = apps.ToList();
        var icons = await Task.Run(() => snapshot.Select(a => Icons.ForSpec(a.DisplayIcon)).ToList());
        for (int i = 0; i < snapshot.Count; i++) snapshot[i].Icon = icons[i] ?? Icons.DefaultExe;
    }

    static List<AppEntry> Read()
    {
        var result = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        ReadKey(result, hklm, UninstallKey, "All users");
        ReadKey(result, hklm, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "All users (32-bit)");
        ReadKey(result, hkcu, UninstallKey, "Current user");
        return result.Values.ToList();
    }

    static void ReadKey(Dictionary<string, AppEntry> result, RegistryKey root, string path, string scope)
    {
        using var key = root.OpenSubKey(path);
        if (key == null) return;
        foreach (var subName in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (sub.GetValue("ParentKeyName") != null) continue;
                if (sub.GetValue("ReleaseType") is string rt && (rt.Contains("Update") || rt.Contains("Hotfix"))) continue;

                var version = sub.GetValue("DisplayVersion") as string ?? "";
                var dedupe = $"{name}|{version}";
                if (result.ContainsKey(dedupe)) continue;

                DateTime? installed = null;
                if (sub.GetValue("InstallDate") is string d && DateTime.TryParseExact(d.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    installed = dt;
                ulong size = sub.GetValue("EstimatedSize") is int kb && kb > 0 ? (ulong)kb * 1024 : 0;

                result[dedupe] = new AppEntry
                {
                    Name = name.Trim(),
                    Publisher = (sub.GetValue("Publisher") as string ?? "").Trim(),
                    Version = version,
                    Installed = installed,
                    Size = size,
                    Scope = scope,
                    UninstallString = sub.GetValue("UninstallString") as string,
                    InstallLocation = sub.GetValue("InstallLocation") as string,
                    DisplayIcon = sub.GetValue("DisplayIcon") as string,
                };
            }
            catch { }
        }
    }

    AppEntry? Selected => List.SelectedItem as AppEntry;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UninstallButton.IsEnabled = !string.IsNullOrWhiteSpace(Selected?.UninstallString);

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { UninstallString: { Length: > 0 } cmd } app) return;
        if (!Ui.Confirm($"Run the uninstaller for {app.Name}?\n\nThe app's own uninstaller will open; follow its prompts.")) return;
        try
        {
            var (file, args) = Ui.SplitCommand(cmd);
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't start the uninstaller:\n\n{ex.Message}");
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } app) return;
        var loc = app.InstallLocation?.Trim().Trim('"');
        if (string.IsNullOrEmpty(loc) && app.DisplayIcon is { } icon)
        {
            var f = icon.Split(',')[0].Trim().Trim('"');
            loc = File.Exists(f) ? Path.GetDirectoryName(f) : null;
        }
        if (string.IsNullOrEmpty(loc) || !Directory.Exists(loc)) Ui.Error($"{app.Name} doesn't list an install folder.");
        else Ui.OpenFileLocation(loc);
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } a) Ui.SearchOnline($"{a.Name} {a.Publisher}");
    }

    void CopyUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.UninstallString is { } s) Ui.Copy(s);
    }
}
