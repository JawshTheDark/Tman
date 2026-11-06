using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Adds a command to the right-click menu, or changes one added earlier. Nothing is written here: the page does that.</summary>
public sealed class ContextMenuDialog : Window
{
    /// <summary>The places a new command can go. Programs, shortcuts and media types are left to the file-type settings of the apps that own them.</summary>
    static readonly (string Label, string ClassName, bool Background)[] Places =
    {
        ("All files", "*", false),
        ("Folders", "Directory", false),
        ("Folder background", @"Directory\Background", true),
        ("Desktop", "DesktopBackground", true),
        ("Drives", "Drive", true),
    };

    readonly TextBox _title = new() { Margin = new Thickness(0, 4, 0, 12) };
    readonly TextBox _command = new() { Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox _icon = new() { Margin = new Thickness(0, 4, 0, 12) };
    readonly CheckBox _shift = new() { Content = "Only show when Shift is held", MinWidth = 0, Margin = new Thickness(0, 4, 0, 0) };
    readonly List<(CheckBox Box, string ClassName, bool Background)> _places = new();
    readonly bool _adding;

    public MenuCommand Result => new(_title.Text.Trim(), _command.Text.Trim(), _icon.Text.Trim(), _shift.IsChecked == true);
    public IEnumerable<string> ClassNames => _places.Where(p => p.Box.IsChecked == true).Select(p => p.ClassName);

    /// <param name="existing">The entry to change, or null to add one.</param>
    public ContextMenuDialog(MenuEntry? existing)
    {
        _adding = existing == null;
        Title = _adding ? "Add a right-click command" : $"Edit {existing!.Name}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Label("Name in the menu"));
        _title.Text = existing?.Name ?? "";
        root.Children.Add(_title);

        root.Children.Add(Label("Command"));
        var line = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var browse = new Button { Content = "Browse…" };
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        line.Children.Add(browse);
        _command.Text = existing?.RawCommand ?? "";
        line.Children.Add(_command);
        root.Children.Add(line);
        root.Children.Add(new TextBlock
        {
            Text = "%1 is the file you clicked, and %V is the folder or background you clicked.",
            Style = (Style)Application.Current.FindResource("Subtle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        root.Children.Add(Label("Icon (optional)"));
        _icon.Text = existing?.RawIcon ?? "";
        root.Children.Add(_icon);

        if (_adding)
        {
            root.Children.Add(Label("Show on"));
            var boxes = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
            foreach (var (label, className, background) in Places)
            {
                var box = new CheckBox { Content = label, MinWidth = 0, Margin = new Thickness(0, 0, 16, 4) };
                _places.Add((box, className, background));
                boxes.Children.Add(box);
            }
            root.Children.Add(boxes);
        }

        _shift.IsChecked = existing?.ShiftOnly == true;
        root.Children.Add(_shift);

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold };

    void Browse()
    {
        var dialog = new OpenFileDialog { Filter = "Programs (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        bool background = _places.Any(p => p.Box.IsChecked == true && p.Background);
        _command.Text = $"\"{dialog.FileName}\" \"{(background ? "%V" : "%1")}\"";
        if (_icon.Text.Length == 0) _icon.Text = dialog.FileName;
    }

    void Accept()
    {
        var problem = _title.Text.Trim().Length == 0 ? "Give the command a name."
            : _command.Text.Trim().Length == 0 ? "Enter the command to run."
            : _adding && !ClassNames.Any() ? "Choose at least one place to show it."
            : null;
        if (problem != null)
        {
            Ui.Error(problem);
            return;
        }
        DialogResult = true;
    }
}
