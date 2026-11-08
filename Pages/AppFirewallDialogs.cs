using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Adds or edits one rule.</summary>
public sealed class AppRuleDialog : Window
{
    readonly RadioButton _thisApp = new() { Content = "This program", Margin = new Thickness(0, 0, 14, 0) };
    readonly RadioButton _allApps = new() { Content = "All programs" };
    readonly TextBox _app = new() { Margin = new Thickness(0, 4, 0, 10), VerticalContentAlignment = VerticalAlignment.Center };
    readonly ComboBox _action = new() { Margin = new Thickness(0, 4, 8, 10), MinWidth = 120 };
    readonly ComboBox _direction = new() { Margin = new Thickness(0, 4, 0, 10), MinWidth = 140 };
    readonly TextBox _domain = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBox _addresses = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBox _ports = new() { Margin = new Thickness(0, 4, 8, 10), MinWidth = 180 };
    readonly ComboBox _protocol = new() { Margin = new Thickness(0, 4, 0, 10), MinWidth = 100 };
    readonly CheckBox _force = new() { Content = "Also apply to loopback, DHCP and the DNS servers, which Tman otherwise always lets through", Margin = new Thickness(0, 0, 0, 10) };
    readonly TextBox _note = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };

    public AppRule Rule { get; }

    public AppRuleDialog(AppRule rule)
    {
        Rule = rule;
        Title = rule.Created > DateTime.Now.AddSeconds(-5) && rule.Note.Length == 0 ? "New rule" : "Edit rule";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);
        _error.Foreground = (Brush)FindResource("BadBrush");

        _thisApp.IsChecked = rule.App != null;
        _allApps.IsChecked = rule.App == null;
        _app.Text = rule.App ?? "";
        _app.IsEnabled = rule.App != null;
        _thisApp.Checked += (_, _) => _app.IsEnabled = true;
        _allApps.Checked += (_, _) => _app.IsEnabled = false;
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 4, 0, 10) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Programs|*.exe|All files|*.*", Title = "Choose the program" };
            if (dialog.ShowDialog(this) == true)
            {
                _app.Text = dialog.FileName;
                _thisApp.IsChecked = true;
            }
        };
        foreach (var a in new[] { "Block", "Allow" }) _action.Items.Add(a);
        _action.SelectedIndex = rule.Action == RuleAction.Allow ? 1 : 0;
        foreach (var d in new[] { "Outgoing", "Incoming", "Both" }) _direction.Items.Add(d);
        _direction.SelectedIndex = (int)rule.Direction;
        foreach (var p in new[] { "Any", "TCP", "UDP" }) _protocol.Items.Add(p);
        _protocol.SelectedIndex = (int)rule.Protocol;
        _domain.Text = rule.Domain;
        _addresses.Text = rule.Addresses;
        _ports.Text = rule.Ports;
        _force.IsChecked = rule.Force;
        _note.Text = rule.Note;

        var subtle = (Style)FindResource("Subtle");
        var root = new StackPanel { Margin = new Thickness(20) };
        var scope = new StackPanel { Orientation = Orientation.Horizontal };
        scope.Children.Add(_thisApp);
        scope.Children.Add(_allApps);
        root.Children.Add(scope);
        var appRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        appRow.Children.Add(browse);
        appRow.Children.Add(_app);
        root.Children.Add(appRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Labeled("Action", _action));
        actions.Children.Add(Labeled("Direction", _direction));
        root.Children.Add(actions);
        root.Children.Add(new TextBlock { Text = "Leave the three target boxes empty to cover all of the program's traffic.", Style = subtle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(new TextBlock { Text = "Domain: a name covers its subdomains too; * is a wildcard, as in *.ads.example.com" });
        root.Children.Add(_domain);
        root.Children.Add(new TextBlock { Text = "IP addresses or ranges, separated by commas, such as 203.0.113.5, 198.51.100.0/24" });
        root.Children.Add(_addresses);
        var portRow = new StackPanel { Orientation = Orientation.Horizontal };
        portRow.Children.Add(Labeled("Remote ports, such as 80, 443, 6000-6100", _ports));
        portRow.Children.Add(Labeled("Protocol", _protocol));
        root.Children.Add(portRow);
        root.Children.Add(_force);
        root.Children.Add(new TextBlock { Text = "Note (optional)" });
        root.Children.Add(_note);
        root.Children.Add(_error);

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(control);
        return panel;
    }

    void Accept()
    {
        var draft = new AppRule
        {
            Id = Rule.Id,
            App = _thisApp.IsChecked == true ? _app.Text.Trim().Trim('"') : null,
            Action = _action.SelectedIndex == 1 ? RuleAction.Allow : RuleAction.Block,
            Direction = (RuleDirection)Math.Max(0, _direction.SelectedIndex),
            Domain = _domain.Text.Trim().ToLowerInvariant(),
            Addresses = _addresses.Text.Trim(),
            Ports = _ports.Text.Trim(),
            Protocol = (RuleProtocol)Math.Max(0, _protocol.SelectedIndex),
            Force = _force.IsChecked == true,
            Note = _note.Text.Trim(),
            Created = Rule.Created,
            Enabled = Rule.Enabled,
            Hits = Rule.Hits,
        };
        var problem = draft.Validate() ?? (draft.App != null && !File.Exists(draft.App) ? $"There's no file at {draft.App}." : null);
        if (problem == null && draft.App != null && draft.Action == RuleAction.Block && draft.CoversEverything
            && draft.App.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show(this, "This program is part of Windows. Blocking all of its traffic can break Windows features such as updates, sign-in or name lookups.\n\nKeep the rule?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (problem != null)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            return;
        }
        Rule.App = draft.App;
        Rule.Action = draft.Action;
        Rule.Direction = draft.Direction;
        Rule.Domain = draft.Domain;
        Rule.Addresses = draft.Addresses;
        Rule.Ports = draft.Ports;
        Rule.Protocol = draft.Protocol;
        Rule.Force = draft.Force;
        Rule.Note = draft.Note;
        DialogResult = true;
    }
}

/// <summary>The blocklist addresses, one per line, like the Docker page's template lists.</summary>
public sealed class BlocklistsDialog : Window
{
    readonly TextBox _box = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 200,
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public List<string> Urls { get; private set; } = new();

    public BlocklistsDialog(IEnumerable<string> urls, IReadOnlyList<string> problems)
    {
        Title = "Blocklists";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        var subtle = (Style)Application.Current.FindResource("Subtle");
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Style = subtle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "One https address per line. Hosts files (0.0.0.0 name), plain domain lists and Adblock-style ||name^ lists all work; anything else in them is ignored. Lists are refreshed weekly. Anyone can publish a list, so add only ones you trust.",
        });
        _box.Text = string.Join(Environment.NewLine, urls);
        root.Children.Add(_box);
        if (problems.Count > 0)
            root.Children.Add(new TextBlock { Text = "Last update:\n" + string.Join("\n", problems), Foreground = (Brush)Application.Current.FindResource("WarnBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });

        var reset = new Button { Content = "Use Tman's lists", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => _box.Text = string.Join(Environment.NewLine, Blocklists.DefaultUrls);
        var ok = new Button { Content = "OK", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    void Accept()
    {
        var lines = _box.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bad = lines.Where(l => !Uri.TryCreate(l, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps).ToList();
        if (bad.Count > 0)
        {
            Ui.Error("These aren't https addresses:\n\n" + string.Join("\n", bad), Title);
            return;
        }
        Urls = lines;
        DialogResult = true;
    }
}

/// <summary>
/// The prompt-mode card: a small window at the bottom right that never takes focus, for an app connecting for the first
/// time. Tman has no kernel driver, so the connection it reports has already gone through; the answer decides the rest.
/// Left alone, it closes after a while and the app stays allowed. One at a time; the rest wait their turn.
/// </summary>
public sealed class AppFirewallPrompt : Window
{
    const int Seconds = 30;
    static readonly Queue<(AppActivity App, AppDestination Dest)> Waiting = new();
    static AppFirewallPrompt? _open;

    readonly string _path;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly TextBlock _countdown = new() { VerticalAlignment = VerticalAlignment.Center };
    int _left = Seconds;
    bool _decided;

    public static void Show(AppActivity app, AppDestination dest)
    {
        if (_open != null)
        {
            if (!Waiting.Any(w => w.App.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase))) Waiting.Enqueue((app, dest));
            return;
        }
        _open = new AppFirewallPrompt(app, dest);
        _open.Show();
    }

    AppFirewallPrompt(AppActivity app, AppDestination dest)
    {
        _path = app.Path;
        Title = "Tman app firewall";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Ui.Dress(this);
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Loaded += (_, _) => Top = area.Bottom - ActualHeight - 16;

        var subtle = (Style)FindResource("Subtle");
        var root = new StackPanel { Margin = new Thickness(16) };
        var head = new DockPanel();
        head.Children.Add(new Image { Source = app.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"{app.Name} connected for the first time", Style = (Style)FindResource("CardHeading") });
        text.Children.Add(new TextBlock { Text = app.Path, Style = subtle, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), ToolTip = app.Path });
        var where = dest.Host.Length > 0 ? $"{dest.Host} ({dest.Ip})" : dest.Ip;
        text.Children.Add(new TextBlock { Text = $"To {where}, port {dest.Port}, {dest.Protocol}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        text.Children.Add(new TextBlock { Text = "This connection has already gone through. Your answer applies from now on; Allow and Block make a rule.", Style = subtle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        head.Children.Add(text);
        root.Children.Add(head);

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var allow = new Button { Content = "Allow", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var block = new Button { Content = "Block", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var once = new Button { Content = "Allow once", MinWidth = 90 };
        allow.Click += (_, _) => Decide(PromptChoice.Allow);
        block.Click += (_, _) => Decide(PromptChoice.Block);
        once.Click += (_, _) => Decide(PromptChoice.AllowOnce);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(allow);
        row.Children.Add(block);
        row.Children.Add(once);
        DockPanel.SetDock(row, Dock.Right);
        buttons.Children.Add(row);
        _countdown.Style = subtle;
        buttons.Children.Add(_countdown);
        root.Children.Add(buttons);
        Content = root;

        Tick();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (!_decided) Task.Run(() => AppFirewall.Instance.PromptTimedOut(_path));
            _open = null;
            if (Waiting.TryDequeue(out var next)) Show(next.App, next.Dest);
        };
    }

    void Tick()
    {
        _countdown.Text = $"Stays allowed in {_left} s";
        if (_left-- <= 0) Close();
    }

    void Decide(PromptChoice choice)
    {
        _decided = true;
        Task.Run(() => AppFirewall.Instance.Decide(_path, choice));
        Close();
    }
}
