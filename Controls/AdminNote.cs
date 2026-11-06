using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tman.Controls;

/// <summary>A strip telling a standard user what administrator rights would add, with a button to get them. Hidden when already elevated.</summary>
public sealed class AdminNote : Border
{
    readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public AdminNote()
    {
        Style = (Style)Application.Current.FindResource("Card");
        Padding = new Thickness(14, 10, 14, 10);
        // Space above only, like every block under a page title, so nothing shifts when this is hidden for administrators.
        Margin = new Thickness(0, 10, 0, 0);
        BorderBrush = (Brush)Application.Current.FindResource("WarnBrush");
        Visibility = MainWindow.IsAdmin ? Visibility.Collapsed : Visibility.Visible;

        var button = new Button { Content = "Restart as administrator", Margin = new Thickness(12, 0, 0, 0) };
        button.Click += (_, _) => MainWindow.RestartAsAdmin();
        DockPanel.SetDock(button, Dock.Right);
        var panel = new DockPanel();
        panel.Children.Add(button);
        panel.Children.Add(_text);
        Child = panel;
    }

    public string Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }
}
