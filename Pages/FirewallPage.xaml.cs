using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Tman.Core;

namespace Tman.Pages;

public partial class FirewallPage : UserControl, IPage
{
    const double ShortPage = 520;

    List<FirewallRule> _all = new();
    ICollectionView? _view;
    string _filter = "";
    bool _loaded, _busy;

    public FirewallPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        SizeChanged += (_, _) => ShowDetails(Selected);
    }

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    async void Load()
    {
        CountText.Text = "Loading…";
        var selected = Selected.Select(r => r.Key).ToHashSet();
        try
        {
            var (profiles, rules) = await Task.Run(() => (FirewallStore.ReadProfiles(),
                FirewallStore.ReadRules().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()));
            Profiles.ItemsSource = profiles;
            _all = rules;
            List.ItemsSource = _all;
            _view = CollectionViewSource.GetDefaultView(_all);
            _view.Filter = Matches;
            ApplyFilter();
            foreach (var r in _all.Where(r => selected.Contains(r.Key) && Matches(r))) List.SelectedItems.Add(r);
        }
        catch (Exception ex)
        {
            CountText.Text = "";
            EmptyText.Text = $"Can't read the firewall, which usually means the Windows Defender Firewall service isn't running. {ex.Message}";
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    bool Matches(object o)
    {
        var r = (FirewallRule)o;
        if (DirectionFilter.SelectedIndex == 1 && !r.Inbound || DirectionFilter.SelectedIndex == 2 && r.Inbound) return false;
        if (ActionFilter.SelectedIndex == 1 && !r.Allow || ActionFilter.SelectedIndex == 2 && r.Allow) return false;
        if (EnabledFilter.SelectedIndex == 1 && !r.Enabled || EnabledFilter.SelectedIndex == 2 && r.Enabled) return false;
        if (NonMicrosoft.IsChecked == true && r.Microsoft) return false;
        return _filter.Length == 0 || Ui.Contains(r.Name, _filter) || Ui.Contains(r.Program, _filter) || Ui.Contains(r.Service, _filter)
               || Ui.Contains(r.Ports, _filter) || Ui.Contains(r.Group, _filter);
    }

    void ApplyFilter()
    {
        if (_view == null) return;
        _view.Refresh();
        int shown = _view.Cast<object>().Count();
        CountText.Text = shown == _all.Count ? $"{_all.Count} rules" : $"{shown} of {_all.Count} rules";
        EmptyText.Text = _all.Count == 0 ? "Windows reports no firewall rules." : "No rules match these filters.";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void OpenFirewall_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "firewall.cpl") { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error($"Couldn't open Windows Defender Firewall: {ex.Message}"); }
    }

    List<FirewallRule> Selected => List.SelectedItems.Cast<FirewallRule>().ToList();

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    void UpdateActions()
    {
        var rules = Selected;
        ShowDetails(rules);
        EnableButton.IsEnabled = !_busy && rules.Any(r => !r.Enabled);
        DisableButton.IsEnabled = !_busy && rules.Any(r => r.Enabled);
        DeleteButton.IsEnabled = !_busy && rules.Count > 0;
    }

    void ShowDetails(List<FirewallRule> rules)
    {
        // A short window keeps its room for the list.
        if (rules.Count != 1 || ActualHeight < ShortPage)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }
        var r = rules[0];
        DetailsPane.Visibility = Visibility.Visible;
        DetailTitle.ToolTip = DetailTitle.Text = r.Name;
        DetailDesc.ToolTip = DetailDesc.Text = r.Description;
        DetailDesc.Visibility = r.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailProgram.ToolTip = DetailProgram.Text = string.Join(" · ", new[] { r.ProgramFull, r.Addresses }.Where(s => s.Length > 0));
    }

    static bool NeedsAdmin(string action)
    {
        if (MainWindow.IsAdmin) return false;
        Ui.Error($"{action} needs administrator rights. Use File > Restart as administrator.");
        return true;
    }

    static string Names(List<FirewallRule> rules)
    {
        var names = rules.Take(8).Select(r => $"{r.Name} ({r.DirectionText.ToLowerInvariant()}, {r.ActionText.ToLowerInvariant()})");
        return string.Join("\n", names) + (rules.Count > 8 ? $"\nand {rules.Count - 8} more" : "");
    }

    void Enable_Click(object sender, RoutedEventArgs e)
    {
        var rules = Selected.Where(r => !r.Enabled).ToList();
        if (rules.Count == 0 || NeedsAdmin("Enabling a firewall rule")) return;
        if (!Ui.Confirm($"Enable these firewall rules?\n\n{Names(rules)}\n\nThey take effect straight away, so what they allow or block changes for every program they cover.")) return;
        Apply(rules, "enable", r => FirewallStore.SetEnabled(r, true));
    }

    void Disable_Click(object sender, RoutedEventArgs e)
    {
        var rules = Selected.Where(r => r.Enabled).ToList();
        if (rules.Count == 0 || NeedsAdmin("Disabling a firewall rule")) return;
        if (!Ui.Confirm($"Disable these firewall rules?\n\n{Names(rules)}\n\nPrograms that rely on an allow rule may lose network access, and a disabled block rule stops blocking.")) return;
        Apply(rules, "disable", r => FirewallStore.SetEnabled(r, false));
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rules = Selected;
        if (rules.Count == 0 || NeedsAdmin("Deleting a firewall rule")) return;
        var windows = rules.Any(r => r.Microsoft) ? "\n\nSome of these belong to Windows or a Microsoft app, and features that rely on them may stop working." : "";
        if (!Ui.Confirm($"Delete these firewall rules?\n\n{Names(rules)}\n\nThis can't be undone from Tman. Disabling a rule is safer if you may want it back.{windows}")) return;
        Apply(rules, "delete", FirewallStore.Delete);
    }

    async void Apply(List<FirewallRule> rules, string verb, Action<FirewallRule> change)
    {
        _busy = true;
        UpdateActions();
        var errors = await Task.Run(() =>
        {
            var failed = new List<string>();
            foreach (var r in rules)
            {
                try { change(r); }
                catch (Exception ex) { failed.Add($"{r.Name}: {ex.Message}"); }
            }
            return failed;
        });
        _busy = false;
        if (errors.Count > 0) Ui.Error($"Couldn't {verb} {errors.Count} of {rules.Count} rules:\n\n{string.Join("\n", errors.Take(8))}");
        List.SelectedItems.Clear();
        Load();
    }

    async void ProfileToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not FirewallProfile p || _busy) return;
        bool turnOn = !p.Enabled;
        if (NeedsAdmin("Turning the firewall on or off")) return;
        var active = p.Active ? " This PC is using this profile right now." : "";
        var message = turnOn
            ? $"Turn on Windows Firewall for the {p.Name} profile?\n\nConnections that its rules don't allow will be blocked, which may affect apps that have been working without it."
            : $"Turn OFF Windows Firewall for the {p.Name} profile?{active}\n\nThis is dangerous. On networks of this type, every other device can reach any service on this PC, and malware can spread to it. Only do this for a short test, and turn it back on afterwards.";
        if (!Ui.Confirm(message, "Firewall")) return;
        _busy = true;
        var error = await Task.Run(() =>
        {
            try
            {
                FirewallStore.SetProfileEnabled(p.Type, turnOn);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
        _busy = false;
        if (error != null) Ui.Error($"Couldn't change the {p.Name} profile:\n\n{error}");
        Load();
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e) =>
        Ui.OpenFileLocation(Selected.Select(r => Environment.ExpandEnvironmentVariables(r.Program)).FirstOrDefault(p => p.Length > 0));

    void CopyName_Click(object sender, RoutedEventArgs e) =>
        Ui.Copy(string.Join(Environment.NewLine, Selected.Select(r => r.Name)));
}
