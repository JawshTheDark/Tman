using System.Windows;
using System.Windows.Controls;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Asks about a list that can be too long for a message box, so every line can still be read before saying yes.</summary>
public sealed class ListConfirmDialog : Window
{
    public ListConfirmDialog(string title, string question, IEnumerable<string> items, string? footer)
    {
        var lines = items.ToList();
        Title = title;
        Width = 560;
        Height = Math.Min(620, 220 + lines.Count * 20);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        var root = new DockPanel { Margin = new Thickness(20) };

        var heading = new TextBlock { Text = question, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var ok = new Button { Content = "Apply", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 90 };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        if (!string.IsNullOrEmpty(footer))
        {
            var note = new TextBlock { Text = footer, Style = (Style)Application.Current.FindResource("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(note, Dock.Bottom);
            root.Children.Add(note);
        }

        var list = new StackPanel();
        foreach (var line in lines)
            list.Children.Add(new TextBlock { Text = "•  " + line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) });

        Content = root;
    }
}
