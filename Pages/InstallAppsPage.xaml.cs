using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public sealed class WingetAppRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Description { get; init; } = "";
    public string Group { get; init; } = "";
    public int Order { get; init; }
    public bool FromSearch { get; init; }

    bool _ticked;
    public bool Ticked { get => _ticked; set => Set(ref _ticked, value); }

    /// <summary>The winget search for the current text found this app, even if its name doesn't contain the text.</summary>
    public bool SearchHit { get; set; }

    string _version = "", _available = "";
    public bool Installed => _version.Length > 0;
    public bool HasUpdate => _available.Length > 0;
    public string State => HasUpdate ? "Update available" : Installed ? "Installed" : "";
    public string VersionNote => HasUpdate ? $"{_version} is installed. {_available} is available." : Installed ? $"{_version} is installed." : "Not installed";
    public Brush StateBrush => (Brush)Application.Current.FindResource(HasUpdate ? "WarnBrush" : "GoodBrush");

    public void SetVersions(string version, string available)
    {
        _version = version;
        _available = available;
        Raise(nameof(State));
        Raise(nameof(VersionNote));
        Raise(nameof(StateBrush));
    }
}

public sealed class WingetUpdateRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Version { get; init; } = "";
    public string Available { get; init; } = "";
    public string Note { get; init; } = "";
    public bool Explicit { get; init; }
    public bool CanUpdate { get; init; } = true;

    bool _ticked;
    public bool Ticked { get => _ticked; set => Set(ref _ticked, value); }
}

public sealed class BuiltInAppRow : ObservableObject
{
    public required string Name { get; init; }
    public required string Package { get; init; }
    public string FullName { get; init; } = "";
    public string Note { get; init; } = "";
    public bool Protected { get; init; }
    public bool CanRemove => !Protected;
    public string State => Protected ? "Protected" : "";

    bool _ticked;
    public bool Ticked { get => _ticked; set => Set(ref _ticked, value); }
}

public enum JobState { Waiting, Running, Done, NeedsRestart, Failed, Cancelled }

public sealed class QueueJob : ObservableObject
{
    public required string Key { get; init; }
    public required string Verb { get; init; }
    public required string Done { get; init; }
    public required string App { get; init; }
    public required Func<Action<string>, Task<int>> Run { get; init; }
    public required Func<int, bool> Succeeded { get; init; }
    public required Func<int, string> Describe { get; init; }

    public string Title => $"{Verb} {App}";
    public JobState State { get; private set; }
    public bool Finished => State > JobState.Running;

    string _result = "Waiting";
    public string Result { get => _result; private set => Set(ref _result, value); }

    public Brush ResultBrush => (Brush)Application.Current.FindResource(State switch
    {
        JobState.Done => "GoodBrush",
        JobState.NeedsRestart => "WarnBrush",
        JobState.Failed => "BadBrush",
        _ => "SubtleBrush",
    });

    public void SetState(JobState state, string text)
    {
        State = state;
        Result = text;
        Raise(nameof(ResultBrush));
    }
}

public partial class InstallAppsPage : UserControl, IPage
{
    const string SearchGroup = "FROM WINGET SEARCH", ImportGroup = "FROM YOUR LIST";
    const int SearchOrder = 1000, ImportOrder = 1001;

    readonly ObservableCollection<WingetAppRow> _apps = new();
    readonly Dictionary<string, WingetAppRow> _byId = new(StringComparer.OrdinalIgnoreCase);
    readonly ObservableCollection<WingetUpdateRow> _updates = new();
    readonly ObservableCollection<BuiltInAppRow> _builtIn = new();
    readonly ObservableCollection<QueueJob> _jobs = new();
    readonly ICollectionView _appView, _builtInView;
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };

    Dictionary<string, WingetCli.Row> _installed = new(StringComparer.OrdinalIgnoreCase);
    List<WingetCli.Row> _installedCut = new();
    CancellationTokenSource? _searchCancel;
    string _filter = "", _builtInFilter = "", _wingetStatus = "";
    string? _wingetVersion;
    bool _missing, _refreshing, _searching, _builtInLoaded, _builtInLoading, _queueRunning, _cancelRest;

    public InstallAppsPage()
    {
        InitializeComponent();
        Ui.SetupGrid(AppList);
        Ui.SetupGrid(UpdateList);
        Ui.SetupGrid(BuiltInList);
        Ui.SetupGrid(QueueList);

        foreach (var a in GetAppsCatalog.Apps)
            AddApp(new WingetAppRow { Name = a.Name, Id = a.Id, Description = a.Description, Group = a.Category.ToUpperInvariant(), Order = a.Order });
        AppList.ItemsSource = _apps;
        _appView = CollectionViewSource.GetDefaultView(_apps);
        _appView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WingetAppRow.Group)));
        _appView.SortDescriptions.Add(new SortDescription(nameof(WingetAppRow.Order), ListSortDirection.Ascending));
        _appView.SortDescriptions.Add(new SortDescription(nameof(WingetAppRow.Name), ListSortDirection.Ascending));
        _appView.Filter = MatchesApp;

        UpdateList.ItemsSource = _updates;
        BuiltInList.ItemsSource = _builtIn;
        _builtInView = CollectionViewSource.GetDefaultView(_builtIn);
        _builtInView.Filter = o => _builtInFilter.Length == 0 || o is BuiltInAppRow b && (Ui.Contains(b.Name, _builtInFilter) || Ui.Contains(b.Package, _builtInFilter) || Ui.Contains(b.Note, _builtInFilter));
        QueueList.ItemsSource = _jobs;

        Search.TextChanged += SearchChanged;
        BuiltInSearch.TextChanged += t =>
        {
            _builtInFilter = t.Trim();
            _builtInView.Refresh();
            UpdateBuiltInStatus();
        };
        _searchDelay.Tick += (_, _) => RunWingetSearch();
        _ = RefreshWinget();
    }

    public void OnShown()
    {
        if (_searching && _searchCancel == null) _searchDelay.Start();
    }

    public void OnHidden() => _searchDelay.Stop();

    public void FocusSearch()
    {
        if (Tabs.SelectedItem == BuiltInTab) BuiltInSearch.FocusBox();
        else
        {
            Tabs.SelectedItem = BrowseTab;
            Search.FocusBox();
        }
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => FitHeight();

    /// <summary>The lists scroll by themselves, so the page gets a fixed height. A short window scrolls the page instead of squeezing the lists away.</summary>
    void FitHeight()
    {
        double needed = 470;
        if (QueuePane.Visibility == Visibility.Visible) needed += QueueBody.Visibility == Visibility.Visible ? 200 : 50;
        Root.Height = Math.Max(Scroller.ActualHeight, needed);
        Root.Margin = new Thickness(0, 0, needed > Scroller.ActualHeight ? 12 : 0, 0);
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && Tabs.SelectedItem == BuiltInTab && !_builtInLoaded) _ = LoadBuiltIn();
    }

    void AddApp(WingetAppRow row)
    {
        _apps.Add(row);
        _byId[row.Id] = row;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WingetAppRow.Ticked)) UpdateBrowseStatus();
        };
    }

    void RemoveApp(WingetAppRow row)
    {
        _apps.Remove(row);
        _byId.Remove(row.Id);
    }

    // ---- What's installed ----

    async Task RefreshWinget()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _wingetStatus = "Checking what's installed…";
            UpdateBrowseStatus();
            UpdatesStatus.Text = "Checking for updates…";
            UpdatesEmpty.Visibility = Visibility.Collapsed;
            _wingetVersion ??= await WingetCli.VersionAsync();
            if (_wingetVersion == null)
            {
                SetMissing(true);
                return;
            }
            if (_missing) SetMissing(false);

            var list = await WingetCli.ListAsync();
            var rows = WingetCli.ParseTables(list.Lines);
            _installed = new(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows.Where(r => !r.IdCut)) _installed[r.Id] = r;
            _installedCut = rows.Where(r => r.IdCut).ToList();
            foreach (var app in _apps) ApplyInstalled(app);
            _wingetStatus = list.Code == 0 || WingetCli.IsNoMatch(list.Code) ? "" : "Couldn't read what's installed. " + WingetCli.Describe(list.Code);
            UpdateBrowseStatus();

            await LoadUpdates();
        }
        catch (Exception ex)
        {
            _wingetStatus = "Couldn't talk to winget: " + ex.Message;
            UpdateBrowseStatus();
        }
        finally
        {
            _refreshing = false;
        }
    }

    void ApplyInstalled(WingetAppRow app)
    {
        var row = _installed.GetValueOrDefault(app.Id) ?? _installedCut.FirstOrDefault(r => app.Id.StartsWith(r.IdStart, StringComparison.OrdinalIgnoreCase));
        app.SetVersions(row?.Version ?? "", row?.Extra ?? "");
    }

    async Task LoadUpdates()
    {
        var result = await WingetCli.UpgradesAsync();
        var rows = WingetCli.ParseTables(result.Lines);
        var full = rows.Any(r => r.IdCut) ? await WingetCli.ExportedIdsAsync() : new List<string>();
        var ticked = _updates.Where(u => u.Ticked).Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _updates.Clear();
        foreach (var r in rows.DistinctBy(r => r.Id))
        {
            // Old versions of winget shorten IDs to fit 120 columns. An export has them in full.
            var matches = r.IdCut ? full.Where(id => id.StartsWith(r.IdStart, StringComparison.OrdinalIgnoreCase)).ToList() : new List<string> { r.Id };
            bool known = matches.Count == 1;
            _updates.Add(new WingetUpdateRow
            {
                Name = r.Name,
                Id = known ? matches[0] : r.Id,
                Version = r.Version,
                Available = r.Extra,
                Explicit = r.Table > 0,
                CanUpdate = known,
                Note = !known ? "Update it with winget" : r.Table > 0 ? "Not in Update all" : "",
                Ticked = known && ticked.Contains(matches[0]),
            });
        }
        UpdatesTab.Header = _updates.Count > 0 ? $"Updates ({_updates.Count})" : "Updates";
        bool failed = result.Code != 0 && !WingetCli.IsNoMatch(result.Code);
        UpdatesStatus.Text = failed && _updates.Count == 0 ? "" : UpdatesSummary();
        UpdatesEmpty.Text = failed ? "Couldn't check for updates. " + WingetCli.Describe(result.Code) : "Everything winget knows how to update is up to date.";
        UpdatesEmpty.Visibility = _updates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    string UpdatesSummary()
    {
        int skipped = _updates.Count(u => u.Explicit);
        var text = _updates.Count == 1 ? "1 update available." : _updates.Count == 0 ? "" : $"{_updates.Count} updates available.";
        if (skipped > 0) text += $" Update all leaves out {skipped} because the publisher wants {(skipped == 1 ? "it" : "them")} updated on purpose; tick {(skipped == 1 ? "it" : "them")} and use Update selected.";
        return text;
    }

    /// <summary>Without winget the catalog still shows, but only the Built-in apps tab can act. Refresh checks again.</summary>
    void SetMissing(bool missing)
    {
        _missing = missing;
        InstallButton.IsEnabled = UninstallButton.IsEnabled = UpdateSelectedButton.IsEnabled = UpdateAllButton.IsEnabled = !missing;
        MissingNote.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (!missing) return;
        _wingetStatus = "";
        MissingText.Text = WingetCli.MissingText + " The Built-in apps tab works without it.";
        UpdatesStatus.Text = "";
        UpdatesEmpty.Text = "Updates need winget.";
        UpdatesEmpty.Visibility = Visibility.Visible;
        UpdateBrowseStatus();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshWinget();

    // ---- Browse ----

    bool MatchesApp(object o)
    {
        var a = (WingetAppRow)o;
        return _filter.Length == 0 || a.SearchHit || Ui.Contains(a.Name, _filter) || Ui.Contains(a.Id, _filter)
               || Ui.Contains(a.Description, _filter) || Ui.Contains(a.Group, _filter);
    }

    void SearchChanged(string text)
    {
        _filter = text.Trim();
        _searchDelay.Stop();
        _searchCancel?.Cancel();
        _searchCancel = null;
        _searching = false;
        foreach (var a in _apps) a.SearchHit = false;
        foreach (var a in _apps.Where(a => a.FromSearch && !a.Ticked).ToList()) RemoveApp(a);
        _appView.Refresh();
        if (_filter.Length >= 2 && !_missing)
        {
            _searching = true;
            _searchDelay.Start();
        }
        UpdateBrowseStatus();
    }

    async void RunWingetSearch()
    {
        _searchDelay.Stop();
        var query = _filter;
        var cancel = _searchCancel = new CancellationTokenSource();
        try
        {
            var result = await WingetCli.SearchAsync(query, cancel.Token);
            if (cancel.IsCancellationRequested || query != _filter) return;
            _searching = false;
            var found = WingetCli.ParseTables(result.Lines);
            foreach (var r in found.Where(r => !r.IdCut && WingetCli.IsValidId(r.Id)))
            {
                if (!_byId.TryGetValue(r.Id, out var app))
                {
                    app = new WingetAppRow
                    {
                        Name = r.Name,
                        Id = r.Id,
                        Description = r.Extra.Length > 0 ? $"Version {r.Version} · {r.Extra}" : $"Version {r.Version}",
                        Group = SearchGroup,
                        Order = SearchOrder,
                        FromSearch = true,
                    };
                    AddApp(app);
                    ApplyInstalled(app);
                }
                app.SearchHit = true;
            }
            _appView.Refresh();
            bool failed = result.Code != 0 && !WingetCli.IsNoMatch(result.Code);
            UpdateBrowseStatus(failed ? "The winget search failed. " + WingetCli.Describe(result.Code)
                : found.Count >= WingetCli.SearchLimit ? $"Showing the first {WingetCli.SearchLimit} winget matches. Type more to narrow them down." : null);
        }
        catch (Exception ex)
        {
            _searching = false;
            UpdateBrowseStatus("The winget search failed: " + ex.Message);
        }
    }

    void UpdateBrowseStatus(string? note = null)
    {
        int shown = _appView?.Cast<object>().Count() ?? 0;
        int installed = _apps.Count(a => a.Installed), updates = _apps.Count(a => a.HasUpdate), ticked = _apps.Count(a => a.Ticked);
        var parts = new List<string> { _filter.Length > 0 ? $"{shown} of {AppCount(_apps.Count)}" : AppCount(_apps.Count) };
        if (_installed.Count > 0) parts.Add($"{installed} installed");
        if (updates > 0) parts.Add($"{updates} with an update");
        if (ticked > 0) parts.Add($"{ticked} selected");
        if (_searching) parts.Add("searching winget…");
        BrowseStatus.Text = string.Join(" · ", parts) + (note ?? _wingetStatus) switch { "" => "", var text => $" · {text}" };

        BrowseEmpty.Text = _searching ? "Nothing in this list matches. Searching all of winget…"
            : _missing ? "No apps match. Searching all of winget needs winget."
            : "No apps match, here or in the winget catalog.";
        BrowseEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Install_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _apps.Where(a => a.Ticked).ToList();
        if (ticked.Count == 0)
        {
            Ui.Error("Tick the apps you want first.");
            return;
        }
        var toInstall = ticked.Where(a => !a.Installed).ToList();
        var toUpdate = ticked.Where(a => a.HasUpdate).ToList();
        if (toInstall.Count + toUpdate.Count == 0)
        {
            Ui.Error("All the ticked apps are already installed and up to date.");
            return;
        }
        var names = toInstall.Select(a => a.Name).Concat(toUpdate.Select(a => a.Name + " (update)"));
        if (!Ui.Confirm($"Install these apps?\n\n{Names(names)}\n\nBy clicking Yes you accept each app's license and the terms of the winget source; " +
                        "you won't be shown them. Each installer comes from the app's publisher and runs without its usual windows. " +
                        "It may close the app if it's open, and may ask for permission.")) return;
        foreach (var a in toInstall) Enqueue(InstallJob(a.Id, a.Name));
        foreach (var a in toUpdate) Enqueue(UpdateJob(a.Id, a.Name));
        foreach (var a in ticked) a.Ticked = false;
    }

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _apps.Where(a => a.Ticked && a.Installed).ToList();
        if (ticked.Count == 0)
        {
            Ui.Error(_apps.Any(a => a.Ticked) ? "None of the ticked apps are installed." : "Tick the apps you want to uninstall first.");
            return;
        }
        if (!Ui.Confirm($"Uninstall these apps?\n\n{Names(ticked.Select(a => a.Name))}\n\nEach app's own uninstaller runs without its usual windows. " +
                        "Settings and files the app keeps may be deleted, and other programs that rely on it may stop working.")) return;
        foreach (var a in ticked)
        {
            Enqueue(UninstallJob(a.Id, a.Name));
            a.Ticked = false;
        }
    }

    void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _apps) a.Ticked = false;
    }

    WingetAppRow? SelectedApp => AppList.SelectedItem as WingetAppRow;
    string? SelectedId => Tabs.SelectedItem == UpdatesTab ? (UpdateList.SelectedItem as WingetUpdateRow)?.Id : SelectedApp?.Id;
    string? SelectedName => Tabs.SelectedItem == UpdatesTab ? (UpdateList.SelectedItem as WingetUpdateRow)?.Name : SelectedApp?.Name;

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedName is { } name) Ui.SearchOnline(name + " app");
    }

    void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is { } id) Ui.Copy(id);
    }

    // ---- Export and import ----

    /// <summary>The same format as winget export, so the file also works with winget import.</summary>
    string ExportJson(IEnumerable<string> ids) => new JsonObject
    {
        ["$schema"] = "https://aka.ms/winget-packages.schema.2.0.json",
        ["CreationDate"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
        ["Sources"] = new JsonArray(new JsonObject
        {
            ["Packages"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["PackageIdentifier"] = id }).ToArray()),
            ["SourceDetails"] = new JsonObject
            {
                ["Argument"] = "https://cdn.winget.microsoft.com/cache",
                ["Identifier"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                ["Name"] = "winget",
                ["Type"] = "Microsoft.PreIndexed.Package",
            },
        }),
        ["WinGetVersion"] = _wingetVersion ?? "1.0",
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var ids = _apps.Where(a => a.Ticked).Select(a => a.Id).ToList();
        if (ids.Count == 0)
        {
            Ui.Error("Tick the apps you want in the list first.");
            return;
        }
        var dlg = new SaveFileDialog { Title = "Export app list", FileName = "tman-apps.json", Filter = "App list (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ExportJson(ids));
            UpdateBrowseStatus($"Saved {AppCount(ids.Count)} to {Path.GetFileName(dlg.FileName)}.");
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't save the list:\n\n{ex.Message}");
        }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Import app list", Filter = "App list (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        ImportFile(dlg.FileName);
    }

    void ImportFile(string path)
    {
        List<string> ids;
        int other;
        try
        {
            (ids, other) = WingetCli.ReadPackageIds(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Ui.Error($"{Path.GetFileName(path)} isn't an app list exported from Tman or winget.");
            return;
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't read the list:\n\n{ex.Message}");
            return;
        }

        var valid = ids.Where(WingetCli.IsValidId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Search.Text = "";
        foreach (var id in valid)
        {
            if (!_byId.TryGetValue(id, out var app))
            {
                app = new WingetAppRow { Name = id, Id = id, Description = "From your imported list.", Group = ImportGroup, Order = ImportOrder };
                AddApp(app);
                ApplyInstalled(app);
            }
            app.Ticked = true;
        }
        _appView.Refresh();
        var note = $"Ticked {AppCount(valid.Count)} from {Path.GetFileName(path)}.";
        if (ids.Count > valid.Count) note += $" Left out {AppCount(ids.Count - valid.Count)} without a valid winget ID.";
        if (other > 0) note += $" Left out {AppCount(other)} from other sources, such as the Microsoft Store.";
        UpdateBrowseStatus(note);
    }

    // ---- Updates ----

    void UpdateSelected_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _updates.Where(u => u.Ticked && u.CanUpdate).ToList();
        if (ticked.Count == 0) Ui.Error("Tick the apps you want to update first.");
        else ConfirmUpdates(ticked);
    }

    void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var all = _updates.Where(u => !u.Explicit && u.CanUpdate).ToList();
        if (all.Count == 0) Ui.Error("There's nothing to update.");
        else ConfirmUpdates(all);
    }

    void ConfirmUpdates(List<WingetUpdateRow> rows)
    {
        if (!Ui.Confirm($"Update these apps?\n\n{Names(rows.Select(u => $"{u.Name}: {u.Version} to {u.Available}"))}\n\nBy clicking Yes you accept each app's license " +
                        "and the terms of the winget source; you won't be shown them. An app that's open may be closed, or the update may fail until you close it.")) return;
        // App Installer is winget itself, and winget is unavailable while it replaces itself.
        foreach (var u in rows.OrderBy(u => u.Id.Equals("Microsoft.AppInstaller", StringComparison.OrdinalIgnoreCase)))
        {
            Enqueue(UpdateJob(u.Id, u.Name));
            u.Ticked = false;
        }
    }

    // ---- Built-in apps ----

    // The script holds no double quotes, so it survives being passed as a command-line argument.
    const string ListAppxScript =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
        "$titles=@{};Get-StartApps|ForEach-Object{if($_.AppID -match '^(.+)!'){$titles[$Matches[1]]=$_.Name}};" +
        "$apps=@(Get-AppxPackage|Where-Object{-not $_.IsResourcePackage}|ForEach-Object{[pscustomobject]@{" +
        "Name=$_.Name;FullName=$_.PackageFullName;Publisher=$_.Publisher;Framework=$_.IsFramework;NonRemovable=$_.NonRemovable;" +
        "Signature=[string]$_.SignatureKind;Location=$_.InstallLocation;Title=$titles[$_.PackageFamilyName]}});" +
        "ConvertTo-Json -InputObject $apps -Compress";

    static string PowerShell => Cmd.System(@"WindowsPowerShell\v1.0\powershell.exe");

    async Task LoadBuiltIn()
    {
        if (_builtInLoading) return;
        _builtInLoading = true;
        BuiltInEmpty.Visibility = Visibility.Collapsed;
        BuiltInStatus.Text = "Reading Store apps…";
        try
        {
            var result = await WingetCli.RunAsync(PowerShell, new[] { "-NoProfile", "-NonInteractive", "-Command", ListAppxScript }, 60000);
            var ticked = _builtIn.Where(b => b.Ticked).Select(b => b.FullName).ToHashSet();
            var rows = result.Code == 0 ? await Task.Run(() => ParseBuiltIn(string.Concat(result.Lines))) : new List<BuiltInAppRow>();
            _builtIn.Clear();
            foreach (var row in rows)
            {
                row.Ticked = ticked.Contains(row.FullName);
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(BuiltInAppRow.Ticked)) UpdateBuiltInStatus();
                };
                _builtIn.Add(row);
            }
            _builtInLoaded = true;
            BuiltInEmpty.Text = result.Code == 0 ? "Windows reported no Store apps for this account." : "Couldn't read the Store apps. PowerShell's Appx commands didn't work on this PC.";
            UpdateBuiltInStatus();
        }
        catch (Exception ex)
        {
            BuiltInEmpty.Text = "Couldn't read the Store apps: " + ex.Message;
            BuiltInEmpty.Visibility = Visibility.Visible;
            BuiltInStatus.Text = "";
        }
        finally
        {
            _builtInLoading = false;
        }
    }

    static List<BuiltInAppRow> ParseBuiltIn(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<BuiltInAppRow>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            string Text(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool Flag(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            var name = Text("Name");
            var signature = Text("Signature");
            // Developer-signed packages from other publishers belong to desktop apps. They aren't Windows' apps to list here.
            if (signature == "Developer" && !Text("Publisher").Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) continue;

            GetAppsCatalog.BuiltIns.TryGetValue(name, out var known);
            var reason = GetAppsCatalog.ProtectedReason(name, Flag("Framework"), Flag("NonRemovable"), signature, Text("Location"));
            var title = known?.Name ?? (Text("Title") is { Length: > 0 } t ? t : GetAppsCatalog.PrettyName(name));
            rows.Add(new BuiltInAppRow
            {
                Name = title,
                Package = name,
                FullName = Text("FullName"),
                Protected = reason != null,
                Note = reason ?? known?.Note ?? "Store app. You can get it back from the Microsoft Store.",
            });
        }
        // Protected packages have no action, so one row per package is enough; frameworks come in several versions.
        return rows.Where(r => r.CanRemove).Concat(rows.Where(r => r.Protected).DistinctBy(r => r.Package))
            .OrderBy(r => r.Protected).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    void UpdateBuiltInStatus()
    {
        int removable = _builtIn.Count(b => b.CanRemove), ticked = _builtIn.Count(b => b.Ticked);
        BuiltInStatus.Text = $"{removable} can be removed · {_builtIn.Count - removable} protected" + (ticked > 0 ? $" · {ticked} selected" : "");
        BuiltInEmpty.Visibility = _builtInView.Cast<object>().Any() ? Visibility.Collapsed : Visibility.Visible;
        if (_builtIn.Count > 0 && _builtInFilter.Length > 0) BuiltInEmpty.Text = "No apps match.";
    }

    async void RefreshBuiltIn_Click(object sender, RoutedEventArgs e) => await LoadBuiltIn();

    void SelectExtras_Click(object sender, RoutedEventArgs e)
    {
        var extras = _builtIn.Where(b => b.CanRemove && GetAppsCatalog.CommonExtras.Contains(b.Package)).ToList();
        foreach (var b in extras) b.Ticked = true;
        if (extras.Count == 0 && _builtIn.Count > 0) BuiltInStatus.Text += " · None of the common extras are on this PC.";
    }

    void ClearBuiltIn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _builtIn) b.Ticked = false;
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        var ticked = _builtIn.Where(b => b.Ticked && b.CanRemove).ToList();
        if (ticked.Count == 0)
        {
            Ui.Error("Tick the apps you want to remove first.");
            return;
        }
        if (!Ui.Confirm($"Remove these apps from your account?\n\n{Names(ticked.Select(b => b.Name))}\n\nOther accounts on this PC keep them. Anything saved inside an app is deleted. " +
                        "To get an app back, open the Microsoft Store, search for its name and install it.")) return;
        foreach (var b in ticked)
        {
            Enqueue(RemoveJob(b));
            b.Ticked = false;
        }
    }

    void OpenStore_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-windows-store:") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Ui.Error($"Couldn't open the Microsoft Store:\n\n{ex.Message}"); }
    }

    void SearchOnlineBuiltIn_Click(object sender, RoutedEventArgs e)
    {
        if (BuiltInList.SelectedItem is BuiltInAppRow b) Ui.SearchOnline($"{b.Package} Windows app");
    }

    void CopyPackage_Click(object sender, RoutedEventArgs e)
    {
        if (BuiltInList.SelectedItem is BuiltInAppRow b) Ui.Copy(b.Package);
    }

    // ---- Queue ----

    static QueueJob InstallJob(string id, string app) => new()
    {
        Key = id, Verb = "Install", Done = "Installed.", App = app,
        Run = line => WingetCli.InstallAsync(id, line), Succeeded = WingetCli.Succeeded, Describe = WingetCli.Describe,
    };

    static QueueJob UpdateJob(string id, string app) => new()
    {
        Key = id, Verb = "Update", Done = "Updated.", App = app,
        Run = line => WingetCli.UpgradeAsync(id, line), Succeeded = WingetCli.Succeeded, Describe = WingetCli.Describe,
    };

    static QueueJob UninstallJob(string id, string app) => new()
    {
        Key = id, Verb = "Uninstall", Done = "Uninstalled.", App = app,
        Run = line => WingetCli.UninstallAsync(id, line), Succeeded = WingetCli.Succeeded, Describe = WingetCli.Describe,
    };

    static QueueJob RemoveJob(BuiltInAppRow app) => new()
    {
        Key = app.FullName, Verb = "Remove", Done = "Removed.", App = app.Name,
        Run = line => RemoveAppx(app.FullName, line),
        Succeeded = code => code == 0,
        Describe = code => code == Cmd.FailedToStart ? "PowerShell didn't start." : "Windows wouldn't remove it. The log has the reason.",
    };

    static Task<int> RemoveAppx(string fullName, Action<string> onLine)
    {
        if (!Regex.IsMatch(fullName, @"^[\w.\-~+]+$"))
        {
            onLine("This package name can't be passed to PowerShell safely.");
            return Task.FromResult(1);
        }
        return WingetCli.StreamAsync(PowerShell, new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            $"[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ErrorActionPreference='Stop';Remove-AppxPackage -Package '{fullName}'",
        }, onLine);
    }

    static string AppCount(int n) => n == 1 ? "1 app" : $"{n} apps";

    static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        var lines = list.Take(14).Select(n => "•  " + n).ToList();
        if (list.Count > 14) lines.Add($"and {list.Count - 14} more");
        return string.Join("\n", lines);
    }

    void Enqueue(QueueJob job)
    {
        if (_jobs.Any(j => j.Key == job.Key && !j.Finished)) return;
        _jobs.Add(job);
        QueuePane.Visibility = Visibility.Visible;
        FitHeight();
        if (!_queueRunning) RunQueue();
    }

    async void RunQueue()
    {
        _queueRunning = true;
        _cancelRest = false;
        CancelQueueButton.IsEnabled = true;
        try
        {
            while (_jobs.FirstOrDefault(j => j.State == JobState.Waiting) is { } job)
            {
                if (_cancelRest)
                {
                    job.SetState(JobState.Cancelled, "Cancelled");
                    continue;
                }
                QueueStatus.Text = $"{job.Title} ({_jobs.IndexOf(job) + 1} of {_jobs.Count})";
                job.SetState(JobState.Running, "Running…");
                AddLog($"{job.Title}:");
                int code;
                try { code = await job.Run(OnJobLine); }
                catch (Exception ex)
                {
                    AddLog(ex.Message);
                    code = Cmd.FailedToStart;
                }
                var text = code == 0 ? job.Done : job.Describe(code);
                job.SetState(code == 0 ? JobState.Done : job.Succeeded(code) ? JobState.NeedsRestart : JobState.Failed, text);
                AddLog(text + "\n");
            }
        }
        finally
        {
            _queueRunning = false;
            CancelQueueButton.IsEnabled = false;
        }
        int failed = _jobs.Count(j => j.State == JobState.Failed), cancelled = _jobs.Count(j => j.State == JobState.Cancelled);
        QueueStatus.Text = $"Finished. {_jobs.Count - failed - cancelled} done" + (failed > 0 ? $", {failed} failed" : "") + (cancelled > 0 ? $", {cancelled} cancelled" : "");
        if (_jobs.Any(j => j.Verb != "Remove")) await RefreshWinget();
        if (_jobs.Any(j => j.Verb == "Remove")) await LoadBuiltIn();
    }

    void OnJobLine(string line)
    {
        if (WingetCli.ProgressOf(line) is { } amount)
            Dispatcher.BeginInvoke(() =>
            {
                if (_jobs.FirstOrDefault(j => j.State == JobState.Running) is { } job) QueueStatus.Text = $"{job.Title}: {amount}";
            });
        else if (WingetCli.LogLine(line) is { } text)
            Dispatcher.BeginInvoke(() => AddLog(text));
    }

    void AddLog(string text)
    {
        LogBox.AppendText(text + "\n");
        if (LogBox.Text.Length > 100_000) LogBox.Text = LogBox.Text[^60_000..];
        LogBox.ScrollToEnd();
    }

    void CancelQueue_Click(object sender, RoutedEventArgs e)
    {
        _cancelRest = true;
        CancelQueueButton.IsEnabled = false;
        QueueStatus.Text = "Stopping after this app…";
    }

    void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = QueueBody.Visibility != Visibility.Visible;
        QueueBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogToggle.Content = show ? "Hide log" : "Show log";
        FitHeight();
    }

    void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in _jobs.Where(j => j.Finished).ToList()) _jobs.Remove(job);
        if (_jobs.Count > 0) return;
        LogBox.Clear();
        QueueStatus.Text = "";
        QueuePane.Visibility = Visibility.Collapsed;
        FitHeight();
    }
}
