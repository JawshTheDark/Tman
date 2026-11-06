using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Controls;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Layout shared by the deploy forms: a scrolling form over a preview of what will run.</summary>
public abstract class DockerFormDialog : Window
{
    readonly TextBlock _warnings = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF5, 0xF8)) };
    readonly Border _banner = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
    readonly TextBox _preview = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 76,
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 4, 0, 0),
    };
    readonly Button _action = new() { IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _saveTemplate = new() { Content = "Save as my template…", Visibility = Visibility.Collapsed };

    protected StackPanel Form { get; } = new() { Margin = new Thickness(20, 16, 20, 16) };

    /// <summary>Raised after a template is saved from this form, so Quick deploy can show it.</summary>
    public event Action? TemplateSaved;

    protected DockerFormDialog(string title, string action, string description = "", string note = "")
    {
        Title = title;
        Width = 760;
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 60);
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);
        _warnings.Foreground = (Brush)Application.Current.FindResource("WarnBrush");
        _banner.Background = (Brush)Application.Current.FindResource("CardBrush");
        _banner.BorderBrush = (Brush)Application.Current.FindResource("WarnBrush");
        _banner.Child = _bannerText;

        Form.Children.Add(_banner);
        if (description.Length > 0) Form.Children.Add(Note(description));
        if (note.Length > 0) Form.Children.Add(Note(note));
        Form.Children.Add(_warnings);

        _action.Content = action;
        AutomationProperties.SetName(_preview, "Commands that will run");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        _action.Click += (_, _) => Accept();
        _saveTemplate.Click += (_, _) => SaveTemplate();
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(_action, Dock.Right);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_action);
        buttons.Children.Add(_saveTemplate);

        var foot = new StackPanel { Margin = new Thickness(20, 8, 20, 16) };
        foot.Children.Add(Heading("WILL RUN"));
        foot.Children.Add(_preview);
        foot.Children.Add(buttons);
        DockPanel.SetDock(foot, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = Form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
    }

    protected abstract List<string> Problems();
    protected abstract List<string> Warnings();
    protected abstract string Preview();

    /// <summary>A last word before anything happens. Returning false leaves the form open.</summary>
    protected virtual bool Confirmed() => true;

    /// <summary>What this form would save as a template. Only called once <see cref="OfferSaving"/> has shown the button.</summary>
    protected virtual JsonObject? ToTemplate(string title, string description) => null;

    protected void OfferSaving() => _saveTemplate.Visibility = Visibility.Visible;

    protected void SetAction(string text) => _action.Content = text;

    protected void SetPreviewHeight(double height) => _preview.Height = height;

    protected void SetBanner(string text)
    {
        _bannerText.Text = text;
        _banner.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Accept()
    {
        var problems = Problems();
        if (problems.Count > 0)
        {
            Ui.Error(string.Join("\n\n", problems), Title);
            return;
        }
        if (Confirmed()) DialogResult = true;
    }

    void SaveTemplate()
    {
        var suggested = Title.Contains(' ') ? Title[(Title.IndexOf(' ') + 1)..] : Title;
        var name = new DockerNameDialog("Save as my template", suggested) { Owner = this };
        if (name.ShowDialog() != true) return;
        if (DockerMyTemplates.Exists(name.Value) && !Ui.Confirm($"Replace your template {name.Value}?", Title)) return;
        try
        {
            if (ToTemplate(name.Value, name.Description) is not { } entry) return;
            DockerMyTemplates.Save(entry);
            TemplateSaved?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error($"Couldn't save the template.\n\n{ex.Message}", Title);
        }
    }

    /// <summary>Redraws the warnings and the command. Call it after anything in the form changes.</summary>
    protected void Update()
    {
        var warnings = Warnings();
        _warnings.Text = string.Join("\n", warnings.Select(w => "•  " + w));
        _warnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _preview.Text = Preview();
    }

    protected static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.FindResource("PanelLabel") };

    protected static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Subtle"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };

    protected static Grid Row(params double[] widths)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var w in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    protected static T At<T>(T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    protected TextBox Field(string text, Action<string> changed, string? name = null)
    {
        var box = new TextBox { Text = text, VerticalContentAlignment = VerticalAlignment.Center };
        if (name != null) AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) =>
        {
            changed(box.Text);
            Update();
        };
        return box;
    }

    protected TextBlock Caption(string text, string? tip = null) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Subtle"),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        ToolTip = string.IsNullOrEmpty(tip) ? text : tip,
        Margin = new Thickness(0, 0, 10, 0),
    };

    /// <summary>A section heading with a button at its right-hand end, for adding a row.</summary>
    protected DockPanel Section(string heading, string add, Action added)
    {
        var button = new Button { Content = add, Padding = new Thickness(8, 1, 8, 1) };
        button.Click += (_, _) => added();
        DockPanel.SetDock(button, Dock.Right);
        var panel = new DockPanel { Margin = new Thickness(0, 14, 0, 2) };
        panel.Children.Add(button);
        var title = Heading(heading);
        title.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(title);
        return panel;
    }

    protected Button RemoveButton(string what, Action removed)
    {
        var button = new Button { Content = "×", Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = $"Remove {what}" };
        AutomationProperties.SetName(button, $"Remove {what}");
        button.Click += (_, _) =>
        {
            removed();
            Update();
        };
        return button;
    }

    /// <summary>Named settings, as either what a template calls them or a name you can edit.</summary>
    protected void AddPair(StackPanel rows, List<PlanEnv> list, PlanEnv e, string kind)
    {
        var row = Row(230, 0, 30);
        if (e.Typed)
            row.Children.Add(At(Spaced(Field(e.Name, v => e.Name = v, $"{kind} name"), new Thickness(0, 0, 8, 0)), 0));
        else
            row.Children.Add(At(Caption(e.Label, string.IsNullOrEmpty(e.Description) ? e.Name : $"{e.Name}\n{e.Description}"), 0));
        if (e.Choices.Count > 0)
        {
            var pick = new ComboBox { ItemsSource = e.Choices, DisplayMemberPath = nameof(TemplateChoice.Text), SelectedItem = e.Choices.FirstOrDefault(c => c.Value == e.Value) };
            pick.SelectionChanged += (_, _) =>
            {
                e.Value = (pick.SelectedItem as TemplateChoice)?.Value ?? "";
                Update();
            };
            row.Children.Add(At(pick, 1));
        }
        else
        {
            row.Children.Add(At(Field(e.Value, v => e.Value = v, e.Name.Length > 0 ? e.Name : $"{kind} value"), 1));
        }
        row.Children.Add(At(RemoveButton(e.Name.Length > 0 ? e.Name : kind, () =>
        {
            list.Remove(e);
            rows.Children.Remove(row);
        }), 2));
        rows.Children.Add(row);
    }

    protected static T Spaced<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 14, 0, 2);
        return element;
    }

    protected static T Spaced<T>(T element, Thickness margin) where T : FrameworkElement
    {
        element.Margin = margin;
        return element;
    }
}

/// <summary>
/// Everything about a single container, whether it's about to be deployed from a template or already exists and is being
/// changed. Every setting can be edited here, not only the ones a template lists. Nothing happens until the button is pressed.
/// </summary>
public sealed class DockerDeployDialog : DockerFormDialog
{
    const string DefaultNetwork = "Default";

    readonly ContainerPlan _plan;
    readonly RecreatePlan? _change;
    readonly StackPanel _ports = new(), _volumes = new(), _env = new(), _labels = new();

    public DockerDeployDialog(ContainerPlan plan, DockerTemplate template, IReadOnlyList<string> networks)
        : base($"Deploy {template.Title}", "Deploy", template.Description, template.Note)
    {
        _plan = plan;
        BuildForm(networks);
        OfferSaving();
        Update();
    }

    public DockerDeployDialog(RecreatePlan change, IReadOnlyList<string> networks) : base($"Edit {change.OldName}", "Recreate")
    {
        _change = change;
        _plan = change.After;
        SetPreviewHeight(112);
        BuildForm(networks);
        OfferSaving();
        Update();
    }

    void BuildForm(IReadOnlyList<string> networks)
    {
        var plan = _plan;

        var name = Row(120, 0);
        name.Children.Add(At(Caption("Name"), 0));
        name.Children.Add(At(Field(plan.Name, v => plan.Name = v, "Name"), 1));
        Form.Children.Add(name);

        var image = Row(120, 0);
        image.Children.Add(At(Caption("Image"), 0));
        image.Children.Add(At(Field(plan.Image, v => plan.Image = v, "Image"), 1));
        Form.Children.Add(image);

        var restart = Row(120, 180, 0);
        var policy = new ComboBox { ItemsSource = ContainerPlan.RestartPolicies.Append(plan.Restart).Distinct().ToList(), SelectedItem = plan.Restart };
        AutomationProperties.SetName(policy, "Restart");
        policy.SelectionChanged += (_, _) =>
        {
            plan.Restart = (string)policy.SelectedItem;
            Update();
        };
        restart.Children.Add(At(Caption("Restart"), 0));
        restart.Children.Add(At(policy, 1));
        Form.Children.Add(restart);

        var network = Row(120, 180, 0);
        var choices = new List<string> { DefaultNetwork, "host", "none" };
        choices.AddRange(networks.Where(n => n is not ("bridge" or "host" or "none")));
        if (plan.Network.Length > 0 && !choices.Contains(plan.Network)) choices.Add(plan.Network);
        var pick = new ComboBox { ItemsSource = choices, SelectedItem = plan.Network.Length > 0 ? plan.Network : DefaultNetwork };
        AutomationProperties.SetName(pick, "Network");
        pick.SelectionChanged += (_, _) =>
        {
            var chosen = (string)pick.SelectedItem;
            plan.Network = chosen == DefaultNetwork ? "" : chosen;
            Update();
        };
        network.Children.Add(At(Caption("Network"), 0));
        network.Children.Add(At(pick, 1));
        Form.Children.Add(network);

        var hostname = Row(120, 0);
        hostname.Children.Add(At(Caption("Hostname", "The name the container calls itself. Leave it empty to let Docker choose."), 0));
        hostname.Children.Add(At(Field(plan.Hostname, v => plan.Hostname = v, "Hostname"), 1));
        Form.Children.Add(hostname);

        var command = Row(120, 0);
        command.Children.Add(At(Caption("Command", "Leave it empty to run what the image normally runs."), 0));
        command.Children.Add(At(Field(plan.Command, v => plan.Command = v, "Command"), 1));
        Form.Children.Add(command);

        if (_change != null)
        {
            Form.Children.Add(Spaced(Check("Start it once it's made", _change.StartAfter, v => _change.StartAfter = v, "Off, the new container waits, stopped."), new Thickness(120, 6, 0, 0)));
        }

        Form.Children.Add(Section("PORTS", "Add port", () => AddPort(Added(plan.Ports, new PlanPort()))));
        if (plan.Ports.Count > 0)
        {
            var heads = Row(90, 22, 0);
            heads.Children.Add(At(Caption("On this PC"), 0));
            heads.Children.Add(At(Caption("In the container"), 2));
            Form.Children.Add(heads);
        }
        Form.Children.Add(_ports);
        foreach (var p in plan.Ports) AddPort(p);
        Form.Children.Add(Spaced(Note("Leave a port empty on this PC to keep it private to the container."), new Thickness(0, 6, 0, 0)));
        Form.Children.Add(Check("Let other devices on my network connect", plan.OpenToNetwork, v => plan.OpenToNetwork = v,
            "Off, these ports only answer on this PC, at localhost."));

        Form.Children.Add(Section("STORAGE", "Add storage", () => AddVolume(Added(plan.Volumes, new PlanVolume()))));
        Form.Children.Add(_volumes);
        foreach (var v in plan.Volumes) AddVolume(v);
        if (plan.Volumes.Any(v => v.OriginalBind != null))
            Form.Children.Add(Spaced(Note("Paths in the template came from its author's server, so Tman chose Docker volumes instead. To keep files in a Windows folder, enter it here or use Browse."), new Thickness(0, 6, 0, 0)));
        if (plan.Volumes.Any(v => v.Unnamed))
            Form.Children.Add(Spaced(Note("The long names are volumes Docker named itself. Keeping them keeps their data."), new Thickness(0, 6, 0, 0)));

        Form.Children.Add(Section("SETTINGS", "Add setting", () => AddPair(_env, plan.Env, Added(plan.Env, new PlanEnv { Typed = true }), "setting")));
        Form.Children.Add(_env);
        foreach (var e in plan.Env) AddPair(_env, plan.Env, e, "setting");

        Form.Children.Add(Section("LABELS", "Add label", () => AddPair(_labels, plan.Labels, Added(plan.Labels, new PlanEnv { Typed = true }), "label")));
        Form.Children.Add(_labels);
        foreach (var l in plan.Labels) AddPair(_labels, plan.Labels, l, "label");

        Form.Children.Add(Spaced(Heading("ACCESS")));
        Form.Children.Add(Check("Privileged", plan.Privileged, v => plan.Privileged = v, "Gives the container almost full control of Docker's virtual machine. Leave this off unless the app needs it."));

        Form.Children.Add(Spaced(Heading("OTHER OPTIONS")));
        Form.Children.Add(Spaced(Note("Anything else docker run accepts, one option per line, such as --memory 512m."), new Thickness(0, 2, 0, 4)));
        var options = new TextBox
        {
            Text = plan.Options,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 66,
            MaxHeight = 200,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(options, "Other options");
        options.TextChanged += (_, _) =>
        {
            plan.Options = options.Text;
            Update();
        };
        Form.Children.Add(options);
    }

    static T Added<T>(List<T> list, T item)
    {
        list.Add(item);
        return item;
    }

    void AddPort(PlanPort p)
    {
        var row = Row(90, 22, 90, 84, 0, 30);
        row.Children.Add(At(Field(p.Host, v => p.Host = v, $"Port on this PC for {p.Target}"), 0));
        row.Children.Add(At(new TextBlock { Text = "→", Style = (Style)Application.Current.FindResource("Subtle"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, 1));
        row.Children.Add(At(Field(p.Target, v => p.Target = v, "Port in the container"), 2));
        var protocol = new ComboBox { ItemsSource = new[] { "tcp", "udp" }, SelectedItem = p.Protocol, Margin = new Thickness(6, 0, 0, 0) };
        AutomationProperties.SetName(protocol, "Protocol");
        protocol.SelectionChanged += (_, _) =>
        {
            p.Protocol = (string)protocol.SelectedItem;
            Update();
        };
        row.Children.Add(At(protocol, 3));
        if (p.Address.Length > 0) row.Children.Add(At(Indented(Caption($"only on {p.Address}")), 4));
        row.Children.Add(At(RemoveButton($"port {p.Target}", () =>
        {
            _plan.Ports.Remove(p);
            _ports.Children.Remove(row);
        }), 5));
        _ports.Children.Add(row);
    }

    static TextBlock Indented(TextBlock text)
    {
        text.Margin = new Thickness(10, 0, 0, 0);
        return text;
    }

    void AddVolume(PlanVolume v)
    {
        var row = Row(0, 82, 150, 92, 30);
        var source = Field(v.Source, s => v.Source = s, $"Volume or folder for {v.Target}");
        var browse = new Button { Content = "Browse", Margin = new Thickness(6, 0, 6, 0) };
        browse.Click += (_, _) =>
        {
            var pick = new OpenFolderDialog { Title = v.Target.Length > 0 ? $"Folder for {v.Target}" : "Choose a folder" };
            if (pick.ShowDialog(this) == true) source.Text = pick.FolderName;
        };
        var target = Field(v.Target, t => v.Target = t, "Path in the container");
        target.ToolTip = "Where this appears inside the container";
        row.Children.Add(At(source, 0));
        row.Children.Add(At(browse, 1));
        row.Children.Add(At(target, 2));
        var readOnly = Check("Read only", v.ReadOnly, value => v.ReadOnly = value, "The container can read this but not change it.");
        readOnly.Margin = new Thickness(8, 0, 0, 0);
        readOnly.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(At(readOnly, 3));
        row.Children.Add(At(RemoveButton(v.Target.Length > 0 ? v.Target : "storage", () =>
        {
            _plan.Volumes.Remove(v);
            _volumes.Children.Remove(row);
        }), 4));
        _volumes.Children.Add(row);
    }

    CheckBox Check(string text, bool value, Action<bool> changed, string tip)
    {
        var box = new CheckBox { Content = text, IsChecked = value, ToolTip = tip, MinWidth = 0, Margin = new Thickness(0, 6, 0, 0) };
        box.Click += (_, _) =>
        {
            changed(box.IsChecked == true);
            Update();
        };
        return box;
    }

    protected override List<string> Problems() => _change?.Problems() ?? _plan.Problems();
    protected override List<string> Warnings() => _change?.Warnings() ?? _plan.Warnings();

    protected override string Preview()
    {
        if (_change == null) return _plan.Preview();
        SetBanner(_change.Unchanged ? "Change anything below. Docker can't change most settings on an existing container, so Tman will show what that means before it does anything." : _change.Explanation);
        SetAction(_change.Live ? "Apply" : "Recreate");
        return _change.Unchanged ? "Nothing yet. Change a setting to see the commands." : _change.Preview();
    }

    protected override bool Confirmed() => _change == null || _change.Live || Ui.Confirm(_change.ConfirmText(), Title);

    protected override JsonObject? ToTemplate(string title, string description) => DockerMyTemplates.ToEntry(_plan, title, description);
}

/// <summary>
/// A Compose stack, either new from a template or already deployed. A new one's file comes from a stranger's repository, so
/// it's shown in full and can be edited, and a deployed one's files are edited where they are.
/// </summary>
public sealed class DockerStackDialog : DockerFormDialog
{
    readonly StackPlan _plan;
    readonly string _yaml = "", _env = "";

    public DockerStackDialog(StackPlan plan, DockerTemplate template) : base($"Deploy {template.Title}", "Deploy", template.Description, template.Note)
    {
        _plan = plan;

        var name = Row(120, 0);
        name.Children.Add(At(Caption("Stack name"), 0));
        name.Children.Add(At(Field(plan.Project, v => plan.Project = v, "Stack name"), 1));
        Form.Children.Add(name);

        Form.Children.Add(Section("SETTINGS", "Add setting", () =>
        {
            var row = new PlanEnv { Typed = true };
            plan.Env.Add(row);
            AddPair(_rows, plan.Env, row, "setting");
        }));
        Form.Children.Add(_rows);
        foreach (var e in plan.Env) AddPair(_rows, plan.Env, e, "setting");

        Form.Children.Add(Spaced(Heading("COMPOSE FILE")));
        if (template.StackUrl != null) Form.Children.Add(Note($"From {template.StackUrl}"));
        Form.Children.Add(Editor(CodeLanguage.Yaml, plan.Yaml, v => plan.Yaml = v, 300, "Compose file"));

        OfferSaving();
        Update();
    }

    /// <summary>For a stack that's already deployed.</summary>
    public DockerStackDialog(StackPlan plan) : base($"Edit {plan.Project}", "Apply")
    {
        _plan = plan;
        _yaml = plan.Yaml;
        _env = plan.EnvText;
        SetPreviewHeight(92);
        SetBanner("Compose replaces any service whose settings changed, and leaves the others running. Its named volumes and folders stay, while anything written inside a replaced container is lost, along with its logs. "
            + "Services you take out of the file are removed. If the change fails, the old files go back.");

        var deployed = plan.Deployed!;
        Form.Children.Add(Spaced(Heading("COMPOSE FILE"), new Thickness(0, 0, 0, 2)));
        Form.Children.Add(Note(deployed.Files.Count > 1 ? $"{deployed.Files[0]}, the first of {deployed.Files.Count} files. The others stay as they are." : deployed.Files[0]));
        Form.Children.Add(Editor(CodeLanguage.Yaml, plan.Yaml, v => plan.Yaml = v, 280, "Compose file"));

        Form.Children.Add(Spaced(Heading("SETTINGS FILE")));
        Form.Children.Add(Note($"{deployed.EnvFile}. The values the Compose file fills in, as NAME=value, one per line."));
        Form.Children.Add(Editor(CodeLanguage.Env, plan.EnvText, v => plan.EnvText = v, 110, "Settings file"));

        OfferSaving();
        Update();
    }

    readonly StackPanel _rows = new();

    /// <summary>A highlighted editor with a button under it that tidies the layout and says what it couldn't settle.</summary>
    StackPanel Editor(CodeLanguage language, string text, Action<string> changed, double height, string name)
    {
        var editor = new CodeEditor(language, name) { Text = text, Height = height };
        editor.TextChanged += value =>
        {
            changed(value);
            Update();
        };

        var fix = new Button
        {
            Content = "Fix formatting",
            ToolTip = language == CodeLanguage.Yaml
                ? "Sets two spaces a level, indents lists under their key and evens out blank lines. Only spacing changes, never a value."
                : "Removes stray spaces round names and doubled blank lines. Values are left as typed.",
        };
        var status = new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Subtle"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        fix.Click += (_, _) =>
        {
            var result = language == CodeLanguage.Yaml ? ComposeFormat.Yaml(editor.Text) : ComposeFormat.Env(editor.Text);
            bool moved = Unix(result.Text) != Unix(editor.Text);
            if (moved) editor.Replace(result.Text);
            bool trouble = result.Problems.Count > 0;
            status.Foreground = (Brush)Application.Current.FindResource(trouble ? "WarnBrush" : "SubtleBrush");
            status.Text = trouble ? string.Join("\n", result.Problems.Take(4)) + (result.Problems.Count > 4 ? $"\nAnd {result.Problems.Count - 4} more." : "")
                : moved ? "Tidied. Ctrl+Z puts it back."
                : "Already tidy.";
        };
        DockPanel.SetDock(fix, Dock.Left);
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(fix);
        row.Children.Add(status);

        var panel = new StackPanel();
        panel.Children.Add(editor);
        panel.Children.Add(row);
        return panel;
    }

    static string Unix(string text) => text.Replace("\r\n", "\n");

    bool Unchanged => _plan.Deployed != null && _plan.Yaml == _yaml && _plan.EnvText == _env;

    protected override List<string> Problems()
    {
        var problems = _plan.Problems();
        if (Unchanged) problems.Add("You haven't changed anything yet.");
        return problems;
    }

    protected override List<string> Warnings() => DockerTemplates.StackWarnings(_plan.Yaml);
    protected override string Preview() => _plan.Preview();

    protected override bool Confirmed() => _plan.Deployed == null
        || Ui.Confirm($"Apply these changes to {_plan.Project}?\n\nCompose replaces the services whose settings changed, which loses anything written inside those containers and their logs. Named volumes and folders stay. Services taken out of the file are removed.\n\nThe old files are kept beside the new ones as .bak.", Title);

    protected override JsonObject? ToTemplate(string title, string description) => DockerMyTemplates.ToEntry(_plan, title, description);
}

/// <summary>Getting the newest image with the same settings. There's nothing to fill in, so this is the confirmation and the preview together.</summary>
public sealed class DockerUpdateDialog : DockerFormDialog
{
    readonly string _preview;

    public DockerUpdateDialog(RecreatePlan change) : base($"Update {change.OldName}", "Update")
    {
        _preview = change.Preview();
        SetPreviewHeight(150);
        SizeToForm();
        SetBanner(change.ConfirmText());
        Form.Children.Add(Note("If the pull brings nothing new, Tman stops there and the container isn't touched."));
        Warned = change.Warnings();
        Update();
    }

    public DockerUpdateDialog(DockerStackFiles files) : base($"Update {files.Project}", "Update")
    {
        _preview = DockerStackChange.UpdatePreview(files);
        SizeToForm();
        SetBanner($"Update {files.Project} to the newest images?\n\nCompose pulls each image, then replaces only the services that got a newer one. Their named volumes and folders stay, while anything written inside a replaced container is lost, along with its logs.");
        Update();
    }

    List<string> Warned { get; } = new();

    void SizeToForm() => Height = 460;

    protected override List<string> Problems() => new();
    protected override List<string> Warnings() => Warned;
    protected override string Preview() => _preview;
}

/// <summary>A new network for the map. Nothing is made until the map's changes are applied.</summary>
public sealed class DockerNetworkDialog : DockerFormDialog
{
    readonly IReadOnlyCollection<string> _taken;
    string _name = "", _subnet = "", _gateway = "";
    bool _internal;

    public NewNetwork Result => new(_name.Trim(), _subnet, _gateway, _internal);

    public DockerNetworkDialog(IReadOnlyCollection<string> taken) : base("New network", "Add to the map")
    {
        _taken = taken;
        Height = 420;
        Form.Children.Add(Spaced(Note("Containers on the same network can reach each other by name. Containers on different networks can't, which is the usual reason to make one."), new Thickness(0, 0, 0, 10)));

        var name = Row(120, 0);
        name.Children.Add(At(Caption("Name"), 0));
        name.Children.Add(At(Field("", v => _name = v, "Network name"), 1));
        Form.Children.Add(name);

        var subnet = Row(120, 0);
        subnet.Children.Add(At(Caption("Address range", "Optional. Docker picks one if you leave this empty."), 0));
        subnet.Children.Add(At(Field("", v => _subnet = v, "Address range"), 1));
        Form.Children.Add(subnet);

        var gateway = Row(120, 0);
        gateway.Children.Add(At(Caption("Gateway", "Optional."), 0));
        gateway.Children.Add(At(Field("", v => _gateway = v, "Gateway"), 1));
        Form.Children.Add(gateway);

        var isolated = new CheckBox { Content = "No internet: containers on it can only reach each other", MinWidth = 0, Margin = new Thickness(0, 10, 0, 0) };
        isolated.Click += (_, _) =>
        {
            _internal = isolated.IsChecked == true;
            Update();
        };
        Form.Children.Add(isolated);
        Update();
    }

    protected override List<string> Problems() => Result.Problems(_taken);
    protected override List<string> Warnings() => new();
    protected override string Preview() => "docker " + string.Join(" ", Result.CreateArgs().Select(DockerTemplates.Quote));
}

/// <summary>Asks for a template's name and a line about it.</summary>
public sealed class DockerNameDialog : Window
{
    readonly TextBox _name = new() { VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBox _description = new() { VerticalContentAlignment = VerticalAlignment.Center };

    public string Value => _name.Text.Trim();
    public string Description => _description.Text.Trim();

    public DockerNameDialog(string title, string suggested)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);
        AutomationProperties.SetName(_name, "Template name");
        AutomationProperties.SetName(_description, "Description");

        var subtle = (Style)Application.Current.FindResource("Subtle");
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Name", Style = subtle, Margin = new Thickness(0, 0, 0, 3) });
        _name.Text = suggested;
        root.Children.Add(_name);
        root.Children.Add(new TextBlock { Text = "Description (optional)", Style = subtle, Margin = new Thickness(0, 10, 0, 3) });
        root.Children.Add(_description);
        root.Children.Add(new TextBlock
        {
            Style = subtle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Text = "It appears first in Quick deploy. Settings are saved in your Windows profile as you typed them, passwords included.",
        });

        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            if (Value.Length == 0) Ui.Error("Give the template a name.", Title);
            else DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) =>
        {
            _name.Focus();
            _name.SelectAll();
        };
    }
}

/// <summary>The template lists Quick deploy reads, one address per line. Earlier lists win when two describe the same thing.</summary>
public sealed class DockerListsDialog : Window
{
    readonly TextBox _box = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = 230,
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public List<string> Urls { get; private set; } = new();

    public DockerListsDialog(IEnumerable<string> urls, IReadOnlyList<string> problems)
    {
        Title = "Template lists";
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
            Text = "One https address per line, each a Portainer template file. Duplicates are merged across lists, and earlier lists come first. Anyone can publish a list, so add only ones you trust.",
        });
        _box.Text = string.Join(Environment.NewLine, urls);
        root.Children.Add(_box);
        if (problems.Count > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Last update:\n" + string.Join("\n", problems),
                Foreground = (Brush)Application.Current.FindResource("WarnBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        var reset = new Button { Content = "Use Tman's lists", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => _box.Text = string.Join(Environment.NewLine, DockerTemplates.DefaultUrls);
        var ok = new Button { Content = "OK", IsDefault = false, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
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
        if (lines.Count == 0)
        {
            Ui.Error("Quick deploy needs at least one list.", Title);
            return;
        }
        Urls = lines;
        DialogResult = true;
    }
}

/// <summary>Output of one Docker command in its own window, such as following a container's logs. Closing it stops the command.</summary>
public sealed class DockerOutputWindow : Window
{
    readonly CancellationTokenSource _stop = new();

    public DockerOutputWindow(string title, string[] args)
    {
        Title = title;
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Ui.Dress(this);

        var panel = new JobOutputPanel { Margin = new Thickness(12), PlainLog = true };
        Content = panel;
        Closed += (_, _) => _stop.Cancel();
        Loaded += async (_, _) =>
        {
            panel.Start(title, () => _stop.Cancel());
            int code = await DockerCli.StreamAsync(args, line =>
            {
                if (WingetCli.LogLine(line) is { } clean) panel.Line(clean);
            }, _stop.Token);
            if (!IsLoaded) return;
            panel.Finish(code == 0 ? new JobResult(JobLevel.Good, "Finished.")
                : code == Cmd.Cancelled ? new JobResult(JobLevel.Warn, "Stopped.")
                : new JobResult(JobLevel.Bad, "Docker reported a problem."));
        };
    }
}
