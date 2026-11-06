using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Everything a new VM needs. The defaults are what Windows 11 asks for, since that's what most people will install.</summary>
public sealed class NewVmDialog : Window
{
    const long Gb = 1L << 30;
    const int MaxDiskGb = 64512;
    const string NoNetwork = "Not connected";

    readonly TextBox _name = new() { VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 100, Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBox _iso = new() { IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
    readonly ComboBox _generation = new() { Margin = new Thickness(0, 4, 0, 2) };
    readonly TextBox _memory = Number("4");
    readonly TextBox _processors = Number(Math.Min(Environment.ProcessorCount, 4).ToString());
    readonly TextBox _disk = Number("64");
    readonly ComboBox _network = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly CheckBox _secureBoot = new() { Content = "Secure Boot", IsChecked = true, MinWidth = 0, Margin = new Thickness(0, 0, 16, 0) };
    readonly CheckBox _tpm = new() { Content = "Virtual TPM", IsChecked = true, MinWidth = 0 };
    readonly IReadOnlyList<string> _taken;

    public NewVmPlan? Plan { get; private set; }

    public NewVmDialog(NewVmOptions options, IReadOnlyList<string> takenNames)
    {
        _taken = takenNames;
        Title = "New virtual machine";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        _generation.Items.Add("Generation 2: UEFI, for Windows 11 and modern systems");
        _generation.Items.Add("Generation 1: BIOS, for older systems");
        _generation.SelectedIndex = 0;
        _generation.SelectionChanged += (_, _) => _secureBoot.IsEnabled = _tpm.IsEnabled = _generation.SelectedIndex == 0;

        var switches = options.Switches.Append(NoNetwork).ToList();
        _network.ItemsSource = switches;
        _network.SelectedItem = switches.FirstOrDefault(s => s.Equals("Default Switch", StringComparison.OrdinalIgnoreCase)) ?? switches[0];

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Label("Name"));
        root.Children.Add(_name);

        root.Children.Add(Label("ISO to install from"));
        var browse = new Button { Content = "Choose ISO", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        var isoRow = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
        isoRow.Children.Add(browse);
        isoRow.Children.Add(_iso);
        root.Children.Add(isoRow);

        root.Children.Add(Label("Generation"));
        root.Children.Add(_generation);
        root.Children.Add(Note("This can't be changed later."));

        var sizes = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (int i = 0; i < 3; i++) sizes.ColumnDefinitions.Add(new ColumnDefinition());
        AddSize(sizes, 0, "Memory (GB)", _memory);
        AddSize(sizes, 1, "Processors", _processors);
        AddSize(sizes, 2, "Disk size (GB)", _disk);
        root.Children.Add(sizes);

        root.Children.Add(Label("Network"));
        root.Children.Add(_network);

        var security = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        security.Children.Add(_secureBoot);
        security.Children.Add(_tpm);
        root.Children.Add(security);
        root.Children.Add(Note("Windows 11 needs both, plus 4 GB of memory, 2 processors and a 64 GB disk. Most Linux ISOs need Secure Boot off."));
        root.Children.Add(Note(options.DiskFolder.Length > 0
            ? $"The disk file goes in {options.DiskFolder}. It starts small and grows as the VM uses it, up to the size you choose."
            : "The disk file starts small and grows as the VM uses it, up to the size you choose."));

        var create = new Button { Content = "Create", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        create.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _name.Focus();
    }

    void Browse()
    {
        var dialog = new OpenFileDialog { Title = "Choose an ISO to install from", Filter = "Disc images (*.iso)|*.iso" };
        if (dialog.ShowDialog(this) == true) _iso.Text = dialog.FileName;
    }

    void Accept()
    {
        var problems = new List<string>();
        int memory = Whole(_memory, "Memory", 1, 1024 * 1024, problems);
        int processors = Whole(_processors, "Processors", 1, Environment.ProcessorCount, problems);
        int disk = Whole(_disk, "Disk size", 1, MaxDiskGb, problems);
        bool gen2 = _generation.SelectedIndex == 0;
        var plan = new NewVmPlan(_name.Text.Trim(), gen2 ? 2 : 1, memory * Gb, processors, disk * Gb, _iso.Text,
            _network.SelectedItem as string is { } network && network != NoNetwork ? network : null,
            gen2 && _secureBoot.IsChecked == true, gen2 && _tpm.IsChecked == true);
        problems.AddRange(HyperV.CheckPlan(plan, _taken, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));
        if (problems.Count > 0)
        {
            Ui.Error(string.Join("\n\n", problems), Title);
            return;
        }
        Plan = plan;
        DialogResult = true;
    }

    static int Whole(TextBox box, string label, int least, int most, List<string> problems)
    {
        if (int.TryParse(box.Text.Trim(), out var value) && value >= least && value <= most) return value;
        problems.Add($"{label} has to be a whole number from {least} to {Fmt.Num(most)}.");
        return least;
    }

    static void AddSize(Grid grid, int column, string label, TextBox box)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, column < 2 ? 12 : 0, 0) };
        cell.Children.Add(Label(label));
        cell.Children.Add(box);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    static TextBox Number(string text) => new() { Text = text, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };

    static TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.FindResource("Subtle") };

    static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Subtle"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };
}
