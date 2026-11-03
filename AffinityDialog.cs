using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Tman.Core;

namespace Tman;

/// <summary>Choose which logical processors a process may run on (until it exits).</summary>
public sealed class AffinityDialog : Window
{
    readonly int _pid;
    readonly List<CheckBox> _boxes = new();

    public AffinityDialog(int pid, string name)
    {
        _pid = pid;
        Title = $"Processor affinity · {name} (PID {pid})";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = $"Which processors can {name} run on? This lasts until the process exits.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        if (!ProcessInspector.GetAffinity(pid, out var mask, out var system, out var error))
        {
            root.Children.Add(new TextBlock { Text = $"Can't read this process's affinity: {error}", TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("BadBrush") });
            var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            root.Children.Add(close);
            Content = root;
            return;
        }

        int count = Math.Min(64, Environment.ProcessorCount);
        var grid = new UniformGrid { Columns = 8 };
        for (int i = 0; i < count; i++)
        {
            ulong bit = 1UL << i;
            var box = new CheckBox { Content = $"CPU {i}", IsChecked = (mask & bit) != 0, IsEnabled = (system & bit) != 0, MinWidth = 0, Margin = new Thickness(0, 2, 6, 2) };
            _boxes.Add(box);
            grid.Children.Add(box);
        }
        root.Children.Add(grid);
        if (Environment.ProcessorCount > 64)
            root.Children.Add(new TextBlock { Text = "Only the first 64 processors (one processor group) can be set here.", Style = (Style)FindResource("Subtle"), Margin = new Thickness(0, 8, 0, 0) });

        var all = new Button { Content = "All", MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
        var none = new Button { Content = "None", MinWidth = 70 };
        all.Click += (_, _) => _boxes.ForEach(b => b.IsChecked = b.IsEnabled);
        none.Click += (_, _) => _boxes.ForEach(b => b.IsChecked = false);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => Apply();

        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(all);
        left.Children.Add(none);
        DockPanel.SetDock(left, Dock.Left);
        buttons.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        buttons.Children.Add(right);
        root.Children.Add(buttons);
        Content = root;
    }

    void Apply()
    {
        ulong mask = 0;
        for (int i = 0; i < _boxes.Count; i++)
            if (_boxes[i].IsChecked == true) mask |= 1UL << i;
        if (mask == 0)
        {
            MessageBox.Show(this, "Pick at least one processor.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ProcessInspector.SetAffinity(_pid, mask, out var error))
        {
            var tip = MainWindow.IsAdmin ? "" : "\n\nTip: File > Restart as administrator to change system processes.";
            MessageBox.Show(this, $"Couldn't set affinity: {error}{tip}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
