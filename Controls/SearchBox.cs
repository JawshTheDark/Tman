using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Tman.Controls;

/// <summary>TextBox with a placeholder hint. Esc clears it.</summary>
public sealed class SearchBox : Grid
{
    readonly TextBox _box = new() { VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _hint = new()
    {
        Text = "Search",
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 0, 0),
        Opacity = 0.55,
    };

    public event Action<string>? TextChanged;

    public SearchBox()
    {
        Children.Add(_box);
        Children.Add(_hint);
        _box.TextChanged += (_, _) =>
        {
            _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            TextChanged?.Invoke(_box.Text);
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _box.Clear();
        };
    }

    public string Placeholder
    {
        get => _hint.Text;
        set => _hint.Text = value;
    }

    public string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public void FocusBox()
    {
        _box.Focus();
        _box.SelectAll();
    }
}
