using System.Text;
using System.Windows;
using System.Windows.Controls;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

/// <summary>Static hardware and OS facts, laid out as cards in two columns.</summary>
public sealed class SystemInfoPage : UserControl, IPage
{
    readonly StackPanel _left = new() { Margin = new Thickness(0, 0, 6, 0) };
    readonly StackPanel _right = new() { Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBlock _status = new() { Text = "Gathering system information…", Margin = new Thickness(0, 12, 0, 0) };

    public SystemInfoPage()
    {
        var copy = new Button { Content = "Copy all", HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += (_, _) => Ui.Copy(AsText());
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = "System info", Style = (Style)FindResource("PageTitle") });

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_right, 1);
        columns.Children.Add(_left);
        columns.Children.Add(_right);

        _status.Style = (Style)FindResource("Subtle");
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_status);
        stack.Children.Add(columns);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };

        if (StaticInfo.IsLoaded) Build();
        else StaticInfo.Loaded += Build;
    }

    void Build()
    {
        _status.Visibility = Visibility.Collapsed;
        _left.Children.Clear();
        _right.Children.Clear();
        int leftRows = 0, rightRows = 0;
        foreach (var section in StaticInfo.Current.Sections)
        {
            var card = Card(section);
            // Simple masonry: drop each card in the shorter column.
            if (leftRows <= rightRows)
            {
                _left.Children.Add(card);
                leftRows += section.Rows.Count + 2;
            }
            else
            {
                _right.Children.Add(card);
                rightRows += section.Rows.Count + 2;
            }
        }
    }

    Border Card(InfoSection section)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < section.Rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = section.Rows[i].Key, Style = (Style)FindResource("Subtle"), Margin = new Thickness(0, 3, 12, 3), TextTrimming = TextTrimming.CharacterEllipsis };
            var value = new TextBox
            {
                Text = section.Rows[i].Value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 3, 0, 3),
                MinHeight = 0,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(key);
            grid.Children.Add(value);
        }
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = section.Title, Style = (Style)FindResource("CardTitle"), Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(grid);
        return new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 12), Child = panel };
    }

    static string AsText()
    {
        var sb = new StringBuilder();
        foreach (var s in StaticInfo.Current.Sections)
        {
            sb.AppendLine($"[{s.Title}]");
            foreach (var r in s.Rows) sb.AppendLine($"{r.Key}: {r.Value}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
