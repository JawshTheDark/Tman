using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Layout shared by the two deploy forms: a scrolling form over a preview of what will run.</summary>
public abstract class DockerFormDialog : Window
{
    readonly TextBlock _warnings = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    readonly TextBox _preview = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 76,
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 4, 0, 0),
    };

    protected StackPanel Form { get; } = new() { Margin = new Thickness(20, 16, 20, 16) };

    protected DockerFormDialog(string title, DockerTemplate template)
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

        if (template.Description.Length > 0) Form.Children.Add(Note(template.Description));
        if (template.Note.Length > 0) Form.Children.Add(Note(template.Note));
        Form.Children.Add(_warnings);

        var deploy = new Button { Content = "Deploy", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        deploy.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(deploy);
        buttons.Children.Add(cancel);

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

    void Accept()
    {
        var problems = Problems();
        if (problems.Count > 0)
        {
            Ui.Error(string.Join("\n\n", problems), Title);
            return;
        }
        DialogResult = true;
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

    protected TextBox Field(string text, Action<string> changed)
    {
        var box = new TextBox { Text = text, VerticalContentAlignment = VerticalAlignment.Center };
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

    protected void AddEnvRows(List<PlanEnv> env)
    {
        if (env.Count == 0) return;
        Form.Children.Add(Spaced(Heading("SETTINGS")));
        foreach (var e in env)
        {
            var row = Row(250, 0);
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
                row.Children.Add(At(Field(e.Value, v => e.Value = v), 1));
            }
            Form.Children.Add(row);
        }
    }

    protected static T Spaced<T>(T element) where T : FrameworkElement
    {
        element.Margin = new Thickness(0, 14, 0, 2);
        return element;
    }
}

/// <summary>Everything about a single container before it runs. Nothing starts until Deploy.</summary>
public sealed class DockerDeployDialog : DockerFormDialog
{
    readonly ContainerPlan _plan;

    public DockerDeployDialog(ContainerPlan plan, DockerTemplate template) : base($"Deploy {template.Title}", template)
    {
        _plan = plan;

        var name = Row(120, 0);
        name.Children.Add(At(Caption("Name"), 0));
        name.Children.Add(At(Field(plan.Name, v => plan.Name = v), 1));
        Form.Children.Add(name);

        var image = Row(120, 0);
        image.Children.Add(At(Caption("Image"), 0));
        image.Children.Add(At(Field(plan.Image, v => plan.Image = v), 1));
        Form.Children.Add(image);

        var restart = Row(120, 180, 0);
        var policy = new ComboBox { ItemsSource = ContainerPlan.RestartPolicies, SelectedItem = plan.Restart };
        policy.SelectionChanged += (_, _) =>
        {
            plan.Restart = (string)policy.SelectedItem;
            Update();
        };
        restart.Children.Add(At(Caption("Restart"), 0));
        restart.Children.Add(At(policy, 1));
        Form.Children.Add(restart);

        if (plan.Command.Length > 0)
        {
            var command = Row(120, 0);
            command.Children.Add(At(Caption("Command"), 0));
            command.Children.Add(At(Field(plan.Command, v => plan.Command = v), 1));
            Form.Children.Add(command);
        }

        if (plan.Ports.Count > 0)
        {
            Form.Children.Add(Spaced(Heading("PORTS")));
            foreach (var p in plan.Ports)
            {
                var row = Row(120, 0);
                row.Children.Add(At(Field(p.Host, v => p.Host = v), 0));
                row.Children.Add(At(Indented(Caption($"on this PC → {p.Target}/{p.Protocol} in the container")), 1));
                Form.Children.Add(row);
            }
            Form.Children.Add(Note("Leave a port empty to keep it private to the container."));
            Form.Children.Add(Check("Let other devices on my network connect", plan.OpenToNetwork, v => plan.OpenToNetwork = v,
                "Off, these ports only answer on this PC, at localhost."));
        }

        if (plan.Volumes.Count > 0)
        {
            Form.Children.Add(Spaced(Heading("STORAGE")));
            foreach (var v in plan.Volumes) AddVolume(v);
            if (plan.Volumes.Any(v => v.OriginalBind != null))
                Form.Children.Add(Note("Paths in the template came from its author's server, so Tman chose Docker volumes instead. To keep files in a Windows folder, enter it here or use Browse."));
        }

        AddEnvRows(plan.Env);

        Form.Children.Add(Spaced(Heading("ACCESS")));
        Form.Children.Add(Check("Privileged", plan.Privileged, v => plan.Privileged = v, "Gives the container almost full control of Docker's virtual machine. Leave this off unless the app needs it."));
        Form.Children.Add(Check("Use the host network", plan.HostNetwork, v => plan.HostNetwork = v, "Shares Docker's own network. Published ports are ignored."));

        Update();
    }

    static TextBlock Indented(TextBlock text)
    {
        text.Margin = new Thickness(10, 0, 0, 0);
        return text;
    }

    void AddVolume(PlanVolume v)
    {
        var row = Row(0, 80, 220);
        var source = Field(v.Source, s => v.Source = s);
        var browse = new Button { Content = "Browse", Margin = new Thickness(6, 0, 6, 0) };
        browse.Click += (_, _) =>
        {
            var pick = new OpenFolderDialog { Title = $"Folder for {v.Target}" };
            if (pick.ShowDialog(this) == true) source.Text = pick.FolderName;
        };
        row.Children.Add(At(source, 0));
        row.Children.Add(At(browse, 1));
        row.Children.Add(At(Caption(v.ReadOnly ? $"→ {v.Target} (read only)" : $"→ {v.Target}"), 2));
        Form.Children.Add(row);
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

    protected override List<string> Problems() => _plan.Problems();
    protected override List<string> Warnings() => _plan.Warnings();
    protected override string Preview() => _plan.Preview();
}

/// <summary>A Compose stack before it runs. The file comes from a stranger's repository, so it's shown in full and can be edited.</summary>
public sealed class DockerStackDialog : DockerFormDialog
{
    readonly StackPlan _plan;

    public DockerStackDialog(StackPlan plan, DockerTemplate template) : base($"Deploy {template.Title}", template)
    {
        _plan = plan;

        var name = Row(120, 0);
        name.Children.Add(At(Caption("Stack name"), 0));
        name.Children.Add(At(Field(plan.Project, v => plan.Project = v), 1));
        Form.Children.Add(name);

        AddEnvRows(plan.Env);

        Form.Children.Add(Spaced(Heading("COMPOSE FILE")));
        Form.Children.Add(Note($"From {template.StackUrl}"));
        var yaml = new TextBox
        {
            Text = plan.Yaml,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 300,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        yaml.TextChanged += (_, _) =>
        {
            plan.Yaml = yaml.Text;
            Update();
        };
        Form.Children.Add(yaml);

        Update();
    }

    protected override List<string> Problems() => _plan.Problems();
    protected override List<string> Warnings() => DockerTemplates.StackWarnings(_plan.Yaml);
    protected override string Preview() => _plan.Preview();
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
