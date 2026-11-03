using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tman.Core;

namespace Tman;

/// <summary>File > Run new task.</summary>
public sealed class RunDialog : Window
{
    readonly TextBox _command = new() { Margin = new Thickness(0, 8, 0, 12) };
    readonly CheckBox _elevated = new() { Content = "Create this task with administrative privileges" };

    public RunDialog()
    {
        Title = "Run new task";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        var ok = new Button { Content = "Run", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var browse = new Button { Content = "Browse…", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Left };
        ok.Click += (_, _) => Run();
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs|*.exe;*.com;*.bat;*.cmd;*.msc|All files|*.*" };
            if (dlg.ShowDialog(this) == true) _command.Text = $"\"{dlg.FileName}\"";
        };

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(browse, Dock.Left);
        buttons.Children.Add(browse);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        buttons.Children.Add(right);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Type the name of a program, folder, document, or Internet resource.", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_command);
        root.Children.Add(_elevated);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => Keyboard.Focus(_command);
    }

    void Run()
    {
        var text = _command.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            var (file, args) = File.Exists(text) || Directory.Exists(text) ? (text, "") : Ui.SplitCommand(text);
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
            if (_elevated.IsChecked == true) psi.Verb = "runas";
            Process.Start(psi);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Run new task", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
