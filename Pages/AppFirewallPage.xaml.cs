using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

/// <summary>One app row. Replaced wholesale on each refresh, with the selection kept by path.</summary>
public sealed class FirewallAppRow : ObservableObject
{
    public required AppActivity Activity { get; init; }
    public string Path => Activity.Path;
    public string Name => Activity.Name;
    public ImageSource? Icon => Activity.Icon;
    public int Connections => Activity.Connections;
    public int Blocked => Activity.Blocked;
    public string BlockedText => Activity.Blocked > 0 ? Activity.Blocked.ToString() : "";
    public long Sent => Activity.Sent;
    public long Received => Activity.Received;
    public string SentText => Activity.Sent > 0 ? Fmt.Bytes(Activity.Sent) : "";
    public string ReceivedText => Activity.Received > 0 ? Fmt.Bytes(Activity.Received) : "";
    public DateTime Last => Activity.Last;
    public string LastText => Fmt.Ago(Activity.Last);
    public required string VerdictText { get; init; }
    public Brush VerdictBrush => AppFirewallPage.BrushFor(VerdictText);
    public string DestinationsText => string.Join(", ", Activity.Destinations.Values.OrderByDescending(d => d.Last)
        .Select(d => d.Host.Length > 0 ? d.Host : d.Ip).Distinct().Take(12));
}

public sealed class DestRow
{
    public required AppDestination D { get; init; }
    public string Host => D.Host;
    public string Ip => D.Ip;
    public int Port => D.Port;
    public string ProtocolText => D.Protocol + (D.Inbound ? " in" : "");
    public string VerdictText => D.Verdict.ToString();
    public Brush VerdictBrush => AppFirewallPage.BrushFor(VerdictText);
    public string Rule => D.Rule;
    public int Count => D.Count;
    public long Sent => D.Sent;
    public long Received => D.Received;
    public string SentText => D.Sent > 0 ? Fmt.Bytes(D.Sent) : "";
    public string ReceivedText => D.Received > 0 ? Fmt.Bytes(D.Received) : "";
    public DateTime Last => D.Last;
    public string LastText => Fmt.Ago(D.Last);
}

public sealed class HistoryRow
{
    public required ConnectionRecord R { get; init; }
    public DateTime Time => R.Time;
    public string TimeText => R.TimeText;
    public string App => R.App;
    public string Destination => R.Destination;
    public string Ip => R.Ip;
    public int Port => R.Port;
    public string PortText => R.PortText;
    public string Protocol => R.Protocol;
    public string DirectionText => R.DirectionText;
    public Verdict Verdict => R.Verdict;
    public string VerdictText => R.VerdictText;
    public Brush VerdictBrush => AppFirewallPage.BrushFor(VerdictText);
    public string Rule => R.Rule;
}

public partial class AppFirewallPage : UserControl, IPage
{
    const int AppsTab = 0, RulesTab = 1, ListsTab = 2, HistoryTab = 3;

    readonly AppFirewall _fw = AppFirewall.Instance;
    readonly ObservableCollection<FirewallAppRow> _apps = new();
    readonly ObservableCollection<HistoryRow> _history = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Dictionary<string, string> _listProblems = new();
    ICollectionView? _appsView, _rulesView, _historyView;
    string _filter = "";
    string? _selectedApp;
    bool _visible, _busy, _historyLoaded;

    public AppFirewallPage()
    {
        InitializeComponent();
        foreach (var g in new[] { AppsGrid, DestGrid, RulesGrid, ListsGrid, HistoryGrid }) Ui.SetupGrid(g);
        AppsGrid.ItemsSource = _apps;
        _appsView = CollectionViewSource.GetDefaultView(_apps);
        _appsView.Filter = AppMatches;
        _appsView.SortDescriptions.Add(new SortDescription(nameof(FirewallAppRow.Last), ListSortDirection.Descending));
        HistoryGrid.ItemsSource = _history;
        _historyView = CollectionViewSource.GetDefaultView(_history);
        _historyView.Filter = HistoryMatches;
        _historyView.SortDescriptions.Add(new SortDescription(nameof(HistoryRow.Time), ListSortDirection.Descending));
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilters();
        };
        _timer.Tick += (_, _) => Tick();
        PromptBox.IsChecked = _fw.Store.Settings.PromptMode;
        StartBox.IsChecked = _fw.Store.Settings.StartWithTman;
        LiveBlockBox.IsChecked = _fw.LiveBlocklist;
        foreach (ComboBoxItem item in KeepBox.Items)
            if ((string)item.Tag == _fw.Store.Settings.HistoryDays.ToString()) KeepBox.SelectedItem = item;
        if (KeepBox.SelectedIndex < 0) KeepBox.SelectedIndex = 1;
        _fw.Prompt += OnPrompt;
        if (!MainWindow.IsAdmin)
        {
            MonitorButton.IsEnabled = RemoveAllButton.IsEnabled = HostsApplyButton.IsEnabled = HostsRemoveButton.IsEnabled = false;
            PromptBox.IsEnabled = StartBox.IsEnabled = false;
        }
    }

    internal void ShowTab(int index) => Tabs.SelectedIndex = index;

    public void OnShown()
    {
        _visible = true;
        _timer.Start();
        if (MainWindow.IsAdmin && !_fw.Monitoring) StartMonitoring();
        LoadRules();
        LoadLists();
        Tick();
    }

    public void OnHidden()
    {
        _visible = false;
        _timer.Stop();
    }

    public void FocusSearch() => Search.FocusBox();

    internal static Brush BrushFor(string verdict) => (Brush)Application.Current.FindResource(verdict switch
    {
        "Blocked" => "BadBrush",
        "Partly blocked" or "Asked" => "WarnBrush",
        _ => "GoodBrush",
    });

    // ---- The status strip and timer ----

    async void StartMonitoring()
    {
        _busy = true;
        UpdateStatus();
        var error = await Task.Run(_fw.Start);
        _busy = false;
        UpdateStatus();
        if (error != null) StatusText.Text = error;
    }

    void Tick()
    {
        if (!_visible) return;
        if (!_fw.Monitoring) Task.Run(_fw.RefreshFromTables).ContinueWith(_ => Dispatcher.BeginInvoke(RefreshApps));
        else RefreshApps();
        UpdateStatus();
        if (Tabs.SelectedIndex == HistoryTab) RefreshHistory();
    }

    void UpdateStatus()
    {
        var s = _fw.Store.Settings;
        int rules = _fw.Store.Rules.Count(r => r.Enabled);
        if (!MainWindow.IsAdmin)
        {
            StatusTitle.Text = "Showing what's open right now";
            StatusText.Text = $"Read from the connection tables every two seconds, with names from reverse DNS. {rules} {(rules == 1 ? "rule is" : "rules are")} saved" + (rules > 0 ? ", and will be enforced the next time Tman runs as administrator." : ".");
            MonitorButton.Content = "Start watching";
            return;
        }
        if (_busy)
        {
            StatusTitle.Text = "Starting…";
            StatusText.Text = "";
            return;
        }
        if (_fw.Monitoring)
        {
            var (allowed, blocked, asked, apps) = _fw.Counters();
            StatusTitle.Text = $"Watching since {_fw.Since:t}: {apps} apps, {allowed:#,0} connections allowed, {blocked:#,0} blocked" + (asked > 0 ? $", {asked} asked about" : "");
            var parts = new List<string> { $"{rules} {(rules == 1 ? "rule" : "rules")} enforced through {_fw.FiltersInEngine} filters in the Windows Filtering Platform" };
            if (_fw.FilterError != null) parts.Add(_fw.FilterError);
            parts.Add(_fw.DropSource != null ? $"Blocked verdicts come from {_fw.DropSource}" : _fw.DropError != null
                ? $"Blocked verdicts aren't available: {_fw.DropError} Turn on audit of blocks to get them from the Security log instead"
                : "Blocked verdicts aren't available");
            AuditButton.Visibility = _fw.DropError != null ? Visibility.Visible : Visibility.Collapsed;
            AuditButton.Content = _fw.Auditing ? "Turn off audit of blocks" : "Turn on audit of blocks";
            if (s.HostsBlockOn) parts.Add($"{s.HostsBlockCount:#,0} blocklist names in the hosts file");
            StatusText.Text = string.Join(". ", parts) + ".";
            MonitorButton.Content = "Stop watching";
        }
        else
        {
            StatusTitle.Text = _fw.MonitorError ?? "Not watching";
            StatusText.Text = (_fw.MonitorError == null ? "The connection tables are shown instead. " : "")
                + $"{rules} {(rules == 1 ? "rule" : "rules")} enforced" + (_fw.FilterError != null ? $" ({_fw.FilterError})" : "") + ".";
            MonitorButton.Content = "Start watching";
        }
    }

    async void Audit_Click(object sender, RoutedEventArgs e)
    {
        if (NeedsAdmin("Changing the audit policy")) return;
        bool on = !_fw.Auditing;
        var message = on
            ? "Ask Windows to audit blocked connections?\n\nThis runs auditpol to enable failure auditing of \"Filtering Platform Connection\" events. Each connection the filter engine refuses is then written to the Security event log (event 5157), where Tman reads it for the Blocked verdicts and history. The log grows a little faster. The same button turns it off again."
            : "Stop Windows auditing blocked connections?\n\nThis runs auditpol to disable failure auditing of \"Filtering Platform Connection\" events. Blocked verdicts stop arriving on this PC.";
        if (!Ui.Confirm(message, "Audit of blocks")) return;
        var error = await Task.Run(() => _fw.SetAuditing(on));
        UpdateStatus();
        if (error != null) Ui.Error($"Couldn't change the audit policy:\n\n{error}");
    }

    void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_fw.Monitoring)
        {
            _fw.Stop();
            UpdateStatus();
        }
        else StartMonitoring();
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Tick();
        LoadRules();
        LoadLists();
        RefreshHistory();
    }

    void Prompt_Click(object sender, RoutedEventArgs e)
    {
        _fw.Store.Settings.PromptMode = PromptBox.IsChecked == true;
        _fw.Store.Save();
        if (PromptBox.IsChecked == true && !_fw.Monitoring) StartMonitoring();
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        _fw.Store.Settings.StartWithTman = StartBox.IsChecked == true;
        _fw.Store.Save();
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        ApplyFilters();
        if (Tabs.SelectedIndex == HistoryTab) RefreshHistory();
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    void ApplyFilters()
    {
        if (_appsView == null) return; // XAML is still loading
        _appsView.Refresh();
        _rulesView?.Refresh();
        _historyView?.Refresh();
        UpdateCounts();
    }

    void UpdateCounts()
    {
        switch (Tabs.SelectedIndex)
        {
            case AppsTab:
                int shown = _appsView?.Cast<object>().Count() ?? 0;
                CountText.Text = shown == _apps.Count ? $"{_apps.Count} apps" : $"{shown} of {_apps.Count} apps";
                AppsEmpty.Text = _apps.Count == 0
                    ? (_fw.Monitoring ? "Nothing has connected since Tman started watching." : MainWindow.IsAdmin ? "Not watching. Press Start watching to see every app's connections as they happen." : "Nothing is connected right now.")
                    : "No apps match.";
                AppsEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
            case RulesTab:
                int rulesShown = _rulesView?.Cast<object>().Count() ?? 0;
                CountText.Text = $"{_fw.Store.Rules.Count} rules";
                RulesCount.Text = rulesShown == _fw.Store.Rules.Count ? "" : $"{rulesShown} shown";
                RulesEmpty.Text = _fw.Store.Rules.Count == 0 ? "No rules yet. Select an app above and choose Allow or Block, or press Add." : "No rules match.";
                RulesEmpty.Visibility = rulesShown == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
            case ListsTab:
                CountText.Text = $"{_fw.Blocklist.Count:#,0} names in the lists in use";
                break;
            case HistoryTab:
                int historyShown = _historyView?.Cast<object>().Count() ?? 0;
                CountText.Text = historyShown == _history.Count ? $"{_history.Count:#,0} connections" : $"{historyShown:#,0} of {_history.Count:#,0} connections";
                HistoryCount.Text = "";
                HistoryEmpty.Text = _history.Count == 0 ? "No connections recorded yet. History fills while Tman is watching as administrator." : "No connections match.";
                HistoryEmpty.Visibility = historyShown == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    // ---- Apps ----

    bool AppMatches(object o)
    {
        var a = (FirewallAppRow)o;
        switch (VerdictFilter.SelectedIndex)
        {
            case 1 when !a.VerdictText.StartsWith("Allowed"): return false;
            case 2 when !a.VerdictText.Contains("locked"): return false;
            case 3 when a.VerdictText != "Asked": return false;
        }
        return _filter.Length == 0 || Ui.Contains(a.Name, _filter) || Ui.Contains(a.Path, _filter) || Ui.Contains(a.DestinationsText, _filter)
               || a.Activity.Destinations.Values.Any(d => Ui.Contains(d.Ip, _filter) || d.Port.ToString() == _filter);
    }

    void RefreshApps()
    {
        if (!_visible) return;
        var selected = (AppsGrid.SelectedItem as FirewallAppRow)?.Path ?? _selectedApp;
        var rows = _fw.Apps().Select(a => new FirewallAppRow { Activity = a, VerdictText = _fw.VerdictFor(a) }).ToList();
        _apps.Clear();
        foreach (var r in rows) _apps.Add(r);
        if (selected != null && rows.FirstOrDefault(r => r.Path.Equals(selected, StringComparison.OrdinalIgnoreCase)) is { } keep)
            AppsGrid.SelectedItem = keep;
        ShowDestinations();
        if (Tabs.SelectedIndex == AppsTab) UpdateCounts();
    }

    void Apps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedApp = (AppsGrid.SelectedItem as FirewallAppRow)?.Path;
        ShowDestinations();
    }

    void ShowDestinations()
    {
        var app = AppsGrid.SelectedItem as FirewallAppRow;
        bool canRule = app != null && !app.Path.StartsWith("PID ");
        AllowAppButton.IsEnabled = BlockAppButton.IsEnabled = canRule;
        if (app == null)
        {
            DestTitle.Visibility = DestGrid.Visibility = Visibility.Collapsed;
            BlockDestButton.IsEnabled = BlockDestAllButton.IsEnabled = false;
            return;
        }
        var selectedKey = (DestGrid.SelectedItem as DestRow)?.D.Key;
        var rows = app.Activity.Destinations.Values.OrderByDescending(d => d.Last).Select(d => new DestRow { D = d }).ToList();
        DestGrid.ItemsSource = rows;
        if (selectedKey != null) DestGrid.SelectedItem = rows.FirstOrDefault(r => r.D.Key == selectedKey);
        DestTitle.Text = $"{app.Name.ToUpperInvariant()} · {rows.Count} {(rows.Count == 1 ? "DESTINATION" : "DESTINATIONS")}";
        DestTitle.Visibility = DestGrid.Visibility = Visibility.Visible;
        Dest_SelectionChanged(this, null!);
    }

    void Dest_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool has = DestGrid.SelectedItem is DestRow && AppsGrid.SelectedItem is FirewallAppRow { Path: var p } && !p.StartsWith("PID ");
        BlockDestButton.IsEnabled = BlockDestAllButton.IsEnabled = has;
    }

    static bool NeedsAdmin(string action)
    {
        if (MainWindow.IsAdmin) return false;
        Ui.Error($"{action} needs administrator rights. Use File > Restart as administrator.");
        return true;
    }

    void AllowApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not FirewallAppRow app || NeedsAdmin("Adding a rule")) return;
        AddRule(new AppRule { App = app.Path, Action = RuleAction.Allow, Direction = RuleDirection.Outgoing }, $"Allow {app.Name} through Tman's rules?\n\n{app.Path}\n\nThis only affects Tman's own blocks; Windows Firewall's rules still apply.");
    }

    void BlockApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not FirewallAppRow app || NeedsAdmin("Adding a rule")) return;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        var risk = app.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            ? "\n\nThis is part of Windows. Blocking it can break Windows features such as updates, sign-in or name lookups." : "";
        AddRule(new AppRule { App = app.Path, Action = RuleAction.Block, Direction = RuleDirection.Outgoing },
            $"Block everything {app.Name} sends?\n\n{app.Path}\n\nIt won't reach the internet or your network until the rule is removed or disabled on the Rules tab. Connections already open may keep working for a while.{risk}");
    }

    void BlockDest_Click(object sender, RoutedEventArgs e) => BlockDestination(forAll: false);
    void BlockDestAll_Click(object sender, RoutedEventArgs e) => BlockDestination(forAll: true);

    void BlockDestination(bool forAll)
    {
        if (AppsGrid.SelectedItem is not FirewallAppRow app || DestGrid.SelectedItem is not DestRow dest || NeedsAdmin("Adding a rule")) return;
        var rule = new AppRule { App = forAll ? null : app.Path, Action = RuleAction.Block, Direction = dest.D.Inbound ? RuleDirection.Incoming : RuleDirection.Outgoing };
        string what;
        if (dest.Host.Length > 0 && !dest.D.Inbound)
        {
            rule.Domain = dest.Host;
            what = $"{dest.Host} (and its subdomains)";
        }
        else
        {
            rule.Addresses = dest.Ip;
            what = dest.Ip;
        }
        var scope = forAll ? "every app" : app.Name;
        var note = rule.IsDomainRule ? "\n\nDomain rules act on the names apps ask for while Tman is watching, so this applies while Tman runs. For a PC-wide block that outlives Tman, add the name to the hosts file." : "";
        AddRule(rule, $"Block {what} for {scope}?{note}");
    }

    async void AddRule(AppRule rule, string confirm)
    {
        if (rule.Validate() is { } problem)
        {
            Ui.Error(problem);
            return;
        }
        if (!Ui.Confirm(confirm, "App firewall")) return;
        await Task.Run(() => _fw.AddRule(rule));
        AfterRuleChange();
    }

    void AfterRuleChange()
    {
        LoadRules();
        RefreshApps();
        UpdateStatus();
        if (_fw.FilterError != null) Ui.Error(_fw.FilterError);
    }

    void NewRule_Click(object sender, RoutedEventArgs e)
    {
        if (NeedsAdmin("Adding a rule")) return;
        var rule = new AppRule { App = (AppsGrid.SelectedItem as FirewallAppRow)?.Path is { } p && !p.StartsWith("PID ") ? p : null };
        if (DestGrid.SelectedItem is DestRow d && AppsGrid.SelectedItem != null)
        {
            if (d.Host.Length > 0) rule.Domain = d.Host;
            else rule.Addresses = d.Ip;
        }
        var dialog = new AppRuleDialog(rule) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        AddRule(dialog.Rule, $"Add this rule?\n\n{dialog.Rule.Summary}");
    }

    void ClearLive_Click(object sender, RoutedEventArgs e)
    {
        _fw.ClearLive();
        RefreshApps();
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation((AppsGrid.SelectedItem as FirewallAppRow)?.Path);
    void CopyPath_Click(object sender, RoutedEventArgs e) => Ui.Copy((AppsGrid.SelectedItem as FirewallAppRow)?.Path ?? "");

    async void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (NeedsAdmin("Removing filters")) return;
        int hosts = _fw.Store.Settings.HostsBlockOn ? _fw.Store.Settings.HostsBlockCount : 0;
        var message = "Remove every filter Tman added to the Windows Filtering Platform, along with Tman's provider and sublayer?\n\nEvery rule on the Rules tab is turned off (the rules themselves are kept, so you can turn them back on). Nothing Tman blocked stays blocked."
                      + (hosts > 0 ? $"\n\nThe {hosts:#,0} blocklist names in the hosts file are separate: use Remove from hosts file on the Blocklists tab for those." : "");
        if (!Ui.Confirm(message, "Remove all Tman filters")) return;
        _busy = true;
        UpdateStatus();
        string? error = null;
        int removed = 0;
        await Task.Run(() =>
        {
            try { removed = _fw.RemoveAllFilters(); }
            catch (Exception ex) { error = ex.Message; }
        });
        _busy = false;
        LoadRules();
        UpdateStatus();
        if (error != null) Ui.Error($"Couldn't remove the filters:\n\n{error}");
        else StatusText.Text = $"Removed {removed} filters. Nothing of Tman's is left in the filter engine.";
    }

    // ---- Rules ----

    void LoadRules()
    {
        var selected = RulesGrid.SelectedItems.Cast<AppRule>().Select(r => r.Id).ToHashSet();
        var rules = _fw.Store.Rules.OrderByDescending(r => r.Created).ToList();
        RulesGrid.ItemsSource = rules;
        _rulesView = CollectionViewSource.GetDefaultView(rules);
        _rulesView.Filter = RuleMatches;
        foreach (var r in rules.Where(r => selected.Contains(r.Id))) RulesGrid.SelectedItems.Add(r);
        Rules_SelectionChanged(this, null!);
        if (Tabs.SelectedIndex == RulesTab) UpdateCounts();
    }

    bool RuleMatches(object o)
    {
        var r = (AppRule)o;
        switch (VerdictFilter.SelectedIndex)
        {
            case 1 when r.Action != RuleAction.Allow: return false;
            case 2 when r.Action != RuleAction.Block: return false;
            case 3: return false;
        }
        return _filter.Length == 0 || Ui.Contains(r.AppName, _filter) || Ui.Contains(r.App, _filter) || Ui.Contains(r.TargetText, _filter) || Ui.Contains(r.Note, _filter);
    }

    List<AppRule> SelectedRules => RulesGrid.SelectedItems.Cast<AppRule>().ToList();

    void Rules_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var rules = SelectedRules;
        EditRuleButton.IsEnabled = rules.Count == 1;
        RemoveRuleButton.IsEnabled = rules.Count > 0;
        EnableRuleButton.IsEnabled = rules.Any(r => !r.Enabled);
        DisableRuleButton.IsEnabled = rules.Any(r => r.Enabled);
    }

    void Rules_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditRule_Click(sender, e);

    async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRules.FirstOrDefault() is not { } rule || NeedsAdmin("Changing a rule")) return;
        var dialog = new AppRuleDialog(rule) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        await Task.Run(() => _fw.UpdateRule(dialog.Rule));
        AfterRuleChange();
    }

    static string Names(List<AppRule> rules) =>
        string.Join("\n", rules.Take(8).Select(r => r.Summary)) + (rules.Count > 8 ? $"\nand {rules.Count - 8} more" : "");

    async void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        var rules = SelectedRules;
        if (rules.Count == 0 || NeedsAdmin("Removing a rule")) return;
        if (!Ui.Confirm($"Remove these rules?\n\n{Names(rules)}\n\nTheir filters leave the Windows Filtering Platform straight away.", "App firewall")) return;
        await Task.Run(() => _fw.RemoveRules(rules));
        AfterRuleChange();
    }

    void EnableRule_Click(object sender, RoutedEventArgs e) => SetEnabled(true);
    void DisableRule_Click(object sender, RoutedEventArgs e) => SetEnabled(false);

    async void SetEnabled(bool on)
    {
        var rules = SelectedRules.Where(r => r.Enabled != on).ToList();
        if (rules.Count == 0 || NeedsAdmin(on ? "Enabling a rule" : "Disabling a rule")) return;
        if (!Ui.Confirm($"{(on ? "Enable" : "Disable")} these rules?\n\n{Names(rules)}", "App firewall")) return;
        await Task.Run(() => _fw.SetEnabled(rules, on));
        AfterRuleChange();
    }

    async void ShowFilters_Click(object sender, RoutedEventArgs e)
    {
        if (NeedsAdmin("Reading the filter engine")) return;
        List<WfpFilterInfo>? filters = null;
        string? error = null;
        await Task.Run(() =>
        {
            try { filters = _fw.FiltersNow(); }
            catch (Exception ex) { error = ex.Message; }
        });
        if (error != null || filters == null)
        {
            Ui.Error(error ?? "The filter engine gave no answer.");
            return;
        }
        var lines = filters.OrderBy(f => f.Name).Select(f => $"{f.Id}\t{(f.Block ? "Block" : "Permit")}\t{(f.Persistent ? "persistent" : "this session")}\t{LayerName(f.Layer)}\t{f.Name}");
        var text = filters.Count == 0 ? "Tman has no filters in the Windows Filtering Platform." : $"{filters.Count} filters under Tman's provider:\n\n" + string.Join("\n", lines);
        MessageBox.Show(Window.GetWindow(this), text, "Tman filters in the Windows Filtering Platform", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    static string LayerName(Guid layer) =>
        layer == WfpEngine.LayerConnectV4 ? "connect v4" : layer == WfpEngine.LayerConnectV6 ? "connect v6" : layer == WfpEngine.LayerAcceptV4 ? "accept v4" : layer == WfpEngine.LayerAcceptV6 ? "accept v6" : layer.ToString();

    // ---- Blocklists ----

    void LoadLists()
    {
        var s = _fw.Store.Settings;
        var selected = (ListsGrid.SelectedItem as BlocklistInfo)?.Url;
        var lists = Blocklists.Describe(s.BlocklistUrls, s.DisabledLists, _listProblems);
        ListsGrid.ItemsSource = lists;
        if (selected != null) ListsGrid.SelectedItem = lists.FirstOrDefault(l => l.Url == selected);
        int inHosts = s.HostsBlockOn ? s.HostsBlockCount : 0;
        HostsTitle.Text = inHosts > 0 ? $"{inHosts:#,0} names blocked PC-wide through the hosts file" : "Blocklists are downloaded but not enforced yet";
        HostsText.Text = inHosts > 0
            ? $"Written {Fmt.Ago(s.HostsBlockWritten ?? DateTime.Now)}. Each resolves to 0.0.0.0 for every app, whether Tman runs or not. Write again after updating the lists to pick up changes."
            : $"Write to hosts file puts the {_fw.Blocklist.Count:#,0} names from the lists in use into the hosts file, so every app on this PC gets 0.0.0.0 for them, with Tman running or not. A dated backup is made first, and Remove from hosts file takes the section out again.";
        HostsApplyButton.IsEnabled = MainWindow.IsAdmin && _fw.Blocklist.Count > 0 && !_busy;
        HostsRemoveButton.IsEnabled = MainWindow.IsAdmin && !_busy;
        Lists_SelectionChanged(this, null!);
        if (Tabs.SelectedIndex == ListsTab) UpdateCounts();
    }

    void Lists_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = ListsGrid.SelectedItem as BlocklistInfo;
        ListToggleButton.IsEnabled = list != null;
        ListToggleButton.Content = list is { Enabled: false } ? "Turn on" : "Turn off";
    }

    void ListToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ListsGrid.SelectedItem is not BlocklistInfo list) return;
        var s = _fw.Store.Settings;
        if (list.Enabled) s.DisabledLists.Add(list.Url);
        else s.DisabledLists.Remove(list.Url);
        _fw.Store.Save();
        Task.Run(_fw.ReloadBlocklist).ContinueWith(_ => Dispatcher.BeginInvoke(LoadLists));
        LoadLists();
    }

    void EditLists_Click(object sender, RoutedEventArgs e)
    {
        var s = _fw.Store.Settings;
        var dialog = new BlocklistsDialog(s.BlocklistUrls, _listProblems.Select(p => $"{Blocklists.Name(p.Key)}: {p.Value}").ToList()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        s.BlocklistUrls = dialog.Urls;
        s.DisabledLists.RemoveAll(u => !dialog.Urls.Contains(u));
        _fw.Store.Save();
        UpdateLists(force: false);
    }

    void UpdateLists_Click(object sender, RoutedEventArgs e) => UpdateLists(force: true);

    async void UpdateLists(bool force)
    {
        if (_busy) return;
        _busy = true;
        UpdateListsButton.IsEnabled = false;
        CheckResult.Text = "Downloading…";
        try
        {
            var problems = await _fw.UpdateBlocklistsAsync(force);
            _listProblems.Clear();
            foreach (var (k, v) in problems) _listProblems[k] = v;
            CheckResult.Text = problems.Count == 0 ? $"Lists are up to date: {_fw.Blocklist.Count:#,0} names." : $"{problems.Count} {(problems.Count == 1 ? "list" : "lists")} couldn't be downloaded; see Status.";
        }
        finally
        {
            _busy = false;
            UpdateListsButton.IsEnabled = true;
        }
        LoadLists();
    }

    void CheckBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CheckName_Click(sender, e);
    }

    void CheckName_Click(object sender, RoutedEventArgs e)
    {
        var name = CheckNameBox.Text.Trim().TrimEnd('.');
        if (name.Length == 0) return;
        var list = _fw.Blocklist.Blocks(name);
        var protectedNote = Blocklists.IsProtected(name) ? " (a protected name: it would be kept out of the hosts file)" : "";
        CheckResult.Text = list != null ? $"{name} is blocked by {list}{protectedNote}." : $"{name} isn't on any list in use.";
    }

    void LiveBlock_Click(object sender, RoutedEventArgs e) => _fw.LiveBlocklist = LiveBlockBox.IsChecked == true;

    async void HostsApply_Click(object sender, RoutedEventArgs e)
    {
        if (NeedsAdmin("Writing the hosts file")) return;
        if (_fw.Blocklist.Count == 0)
        {
            Ui.Error("The lists haven't been downloaded yet. Press Update now first.");
            return;
        }
        int skipped = _fw.Blocklist.Domains.Count(Blocklists.IsProtected);
        var message = $"Write {_fw.Blocklist.Count - skipped:#,0} names into the hosts file, each pointing at 0.0.0.0?\n\n"
                      + "Every app on this PC then gets no address for them, with Tman running or not. A dated copy of the hosts file goes to the backups folder first, and the DNS cache is flushed afterwards.\n\n"
                      + $"Kept out: {skipped:#,0} names under Microsoft, Windows Update, Office and Xbox domains, and any name a global Allow rule covers. "
                      + "Microsoft Defender may still flag hosts edits for popular domains. Windows reads a long hosts file a little more slowly at startup, and apps then get no address at all for a listed name.";
        if (!Ui.Confirm(message, "Write blocklist to hosts file")) return;
        await RunHosts(() => _fw.WriteHostsBlock(), "write the hosts file");
    }

    async void HostsRemove_Click(object sender, RoutedEventArgs e)
    {
        if (NeedsAdmin("Writing the hosts file")) return;
        int now = await Task.Run(AppFirewall.HostsBlockCountNow);
        if (!Ui.Confirm($"Remove Tman's section ({now:#,0} names) from the hosts file? Your own entries stay. A dated copy is made first.", "Remove blocklist from hosts file")) return;
        await RunHosts(() => { _fw.RemoveHostsBlock(); return 0; }, "change the hosts file");
    }

    async Task RunHosts(Func<int> action, string verb)
    {
        _busy = true;
        LoadLists();
        string? error = null;
        await Task.Run(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex is UnauthorizedAccessException ? "Windows refused the write. Security software can protect this file." : ex.Message; }
        });
        _busy = false;
        LoadLists();
        UpdateStatus();
        if (error != null) Ui.Error($"Couldn't {verb}:\n\n{error}");
    }

    // ---- History ----

    bool HistoryMatches(object o)
    {
        var h = (HistoryRow)o;
        switch (VerdictFilter.SelectedIndex)
        {
            case 1 when h.Verdict != Verdict.Allowed: return false;
            case 2 when h.Verdict != Verdict.Blocked: return false;
            case 3 when h.Verdict != Verdict.Asked: return false;
        }
        return _filter.Length == 0 || Ui.Contains(h.App, _filter) || Ui.Contains(h.Destination, _filter) || Ui.Contains(h.Ip, _filter) || Ui.Contains(h.Rule, _filter) || h.PortText == _filter;
    }

    void RefreshHistory()
    {
        var records = _fw.History();
        if (_historyLoaded && records.Count == _history.Count) { UpdateHistoryCounters(); return; }
        _historyLoaded = true;
        _history.Clear();
        foreach (var r in records) _history.Add(new HistoryRow { R = r });
        UpdateHistoryCounters();
        if (Tabs.SelectedIndex == HistoryTab) UpdateCounts();
    }

    void UpdateHistoryCounters()
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int allowed = 0, blocked = 0, asked = 0;
        foreach (var h in _history)
        {
            apps.Add(h.R.Path);
            switch (h.Verdict)
            {
                case Verdict.Allowed: allowed++; break;
                case Verdict.Blocked: blocked++; break;
                default: asked++; break;
            }
        }
        AllowedCount.Text = allowed.ToString("#,0");
        BlockedCount.Text = blocked.ToString("#,0");
        AskedCount.Text = asked.ToString("#,0");
        AppsCount.Text = apps.Count.ToString("#,0");
    }

    void Keep_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (KeepBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var days) || days == _fw.Store.Settings.HistoryDays) return;
        _fw.Store.Settings.HistoryDays = days;
        _fw.Store.Save();
        Task.Run(() => _fw.SaveHistory(force: true));
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var rows = (_historyView?.Cast<HistoryRow>() ?? _history).Select(h => h.R).ToList();
        if (rows.Count == 0)
        {
            Ui.Error("There's nothing to export.");
            return;
        }
        var dialog = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = $"tman-connections-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, AppFirewall.ToCsv(rows), System.Text.Encoding.UTF8);
            HistoryCount.Text = $"Exported {rows.Count:#,0} rows.";
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't write the file:\n\n{ex.Message}");
        }
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm($"Clear all {_history.Count:#,0} recorded connections? This can't be undone.", "Clear history")) return;
        _fw.ClearHistory();
        _history.Clear();
        _historyLoaded = false;
        RefreshHistory();
    }

    // ---- Prompts ----

    void OnPrompt(AppActivity app, AppDestination dest)
    {
        if (MainWindow.SnapshotMode) return;
        AppFirewallPrompt.Show(app, dest);
    }
}
