using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Tman.Core;

public interface IPage
{
    void OnShown() { }
    void OnHidden() { }
    void FocusSearch() { }
}

public static class Ui
{
    /// <summary>Shared look for every list grid in the app.</summary>
    public static void SetupGrid(DataGrid g)
    {
        g.AutoGenerateColumns = false;
        g.IsReadOnly = true;
        g.HeadersVisibility = DataGridHeadersVisibility.Column;
        g.GridLinesVisibility = DataGridGridLinesVisibility.None;
        g.CanUserAddRows = false;
        g.CanUserDeleteRows = false;
        g.CanUserResizeRows = false;
        g.SelectionUnit = DataGridSelectionUnit.FullRow;
        g.Background = Brushes.Transparent;
        g.RowBackground = Brushes.Transparent;
        g.BorderThickness = new Thickness(0);
        g.RowHeaderWidth = 0;
        g.MinRowHeight = 30;
        g.EnableRowVirtualization = true;
        VirtualizingPanel.SetIsVirtualizing(g, true);
        VirtualizingPanel.SetVirtualizationMode(g, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(g, ScrollUnit.Pixel);

        // Instrument-style headers: small, bold, all caps. Based on the theme's own header style so sorting arrows etc. stay.
        var header = new Style(typeof(DataGridColumnHeader), g.TryFindResource(typeof(DataGridColumnHeader)) as Style);
        header.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
        if (g.TryFindResource("LabelBrush") is Brush label) header.Setters.Add(new Setter(Control.ForegroundProperty, label));
        header.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, UpperCaseTemplate));
        g.ColumnHeaderStyle = header;
    }

    static readonly DataTemplate UpperCaseTemplate = MakeUpperCaseTemplate();

    static DataTemplate MakeUpperCaseTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding { Converter = new UpperCase() });
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var template = new DataTemplate { VisualTree = text };
        template.Seal();
        return template;
    }

    sealed class UpperCase : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value?.ToString()?.ToUpperInvariant() ?? "";

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Gives a secondary window the main window's chassis color and Tahoma text.</summary>
    public static void Dress(Window w)
    {
        w.Background = (Brush)Application.Current.FindResource("WindowBrush");
        w.FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        w.FontSize = 12;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
    }

    /// <summary>Sets an initial sort and shows the arrow on the matching column.</summary>
    public static void SortBy(DataGrid g, string member, ListSortDirection dir)
    {
        var view = CollectionViewSource.GetDefaultView(g.ItemsSource);
        if (view == null) return;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(member, dir));
        foreach (var c in g.Columns)
            c.SortDirection = c.SortMemberPath == member ? dir : null;
    }

    public static bool Contains(string? haystack, string needle) =>
        haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public static void Error(string message, string title = "Tman") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(string message, string title = "Tman") =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public static void OpenFileLocation(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { }
    }

    public static void ShowProperties(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Native.SHObjectProperties(IntPtr.Zero, 0x2 /* SHOP_FILEPATH */, path, null);
    }

    public static void SearchOnline(string query)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.bing.com/search?q=" + Uri.EscapeDataString(query)) { UseShellExecute = true });
        }
        catch { }
    }

    public static void Copy(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    /// <summary>Splits a command like <c>"C:\x\app.exe" /S</c> or <c>MsiExec.exe /X{...}</c> into file and arguments.</summary>
    public static (string File, string Args) SplitCommand(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            if (end > 0) return (c[1..end], c[(end + 1)..].Trim());
        }
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (c[..(exe + 4)], c[(exe + 4)..].Trim());
        var space = c.IndexOf(' ');
        return space > 0 ? (c[..space], c[(space + 1)..]) : (c, "");
    }
}
