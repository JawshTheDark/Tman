using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Tman.Core;

namespace Tman.Pages;

public partial class ContextMenusPage : UserControl, IPage
{
    readonly ICollectionView _view;
    readonly List<MenuEntry> _entries = new();
    string _filter = "";
    bool _busy;

    public ContextMenusPage()
    {
        InitializeComponent();
        Ui.SetupGrid(Entries);
        foreach (var (label, _) in ContextMenus.Locations) PlaceFilter.Items.Add(label);
        _view = new CollectionViewSource { Source = _entries }.View;
        _view.Filter = Matches;
        Entries.ItemsSource = _view;
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            Refilter();
        };
    }

    public async void OnShown() => await Reload();
    public void FocusSearch() => Search.FocusBox();

    MenuEntry? Selected => Entries.SelectedItem as MenuEntry;

    bool Matches(object o)
    {
        var e = (MenuEntry)o;
        bool source = SourceFilter.SelectedIndex switch
        {
            1 => !e.WindowsOwned && !e.Custom,
            2 => e.WindowsOwned,
            3 => e.Custom,
            4 => e.Hidden,
            _ => true,
        };
        if (!source) return false;
        if (PlaceFilter.SelectedIndex > 0 && !e.Places.Contains(PlaceFilter.SelectedItem)) return false;
        return _filter.Length == 0 || Ui.Contains(e.Name, _filter) || Ui.Contains(e.From, _filter) || Ui.Contains(e.Detail, _filter) || Ui.Contains(e.PlacesText, _filter);
    }

    void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view != null) Refilter();
    }

    void Refilter()
    {
        _view.Refresh();
        UpdateCount();
    }

    void UpdateCount()
    {
        int shown = _view.Cast<object>().Count(), hidden = _entries.Count(e => e.Hidden);
        CountText.Text = $"{shown} of {_entries.Count} entries · {hidden} hidden";
        EmptyText.Text = _entries.Count == 0 ? "Tman couldn't read any menu entries." : "Nothing matches these filters.";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task Reload()
    {
        var key = Selected?.Key;
        CountText.Text = "Loading…";
        var entries = await Task.Run(ContextMenus.Read);
        _entries.Clear();
        _entries.AddRange(entries.OrderBy(e => e.WindowsOwned).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase));
        _view.Refresh();
        UpdateCount();
        if (key != null && _entries.FirstOrDefault(e => e.Key == key) is { } again)
        {
            Entries.SelectedItem = again;
            Entries.ScrollIntoView(again);
        }
        UpdateButtons();
    }

    void Entries_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    void UpdateButtons()
    {
        var entry = Selected;
        bool free = !_busy;
        EditButton.IsEnabled = DeleteButton.IsEnabled = free && entry?.Custom == true;
        HideButton.Content = entry?.Hidden == true ? "Show again" : "Hide";
        HideButton.IsEnabled = free && entry != null && (entry.Hidden || entry.LockReason == null);
        HideButton.ToolTip = entry is { Hidden: false, LockReason: not null } ? entry.LockReason : null;
        ToolTipService.SetShowOnDisabled(HideButton, true);
        ShiftButton.Content = entry?.ShiftOnly == true ? "Always show" : "Show only with Shift";
        ShiftButton.IsEnabled = free && entry is { Kind: MenuEntryKind.Command, Hidden: false, LockReason: null };
    }

    async Task Apply(Func<string?> change, bool needsExplorer = false)
    {
        _busy = true;
        UpdateButtons();
        var error = await Task.Run(change);
        _busy = false;
        await Reload();
        if (error != null) Ui.Error(error);
        else if (needsExplorer) PendingBar.Visibility = Visibility.Visible;
    }

    async void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        bool hide = !entry.Hidden;
        if (hide)
        {
            var warnings = new List<string>();
            if (entry.WindowsOwned) warnings.Add($"\"{entry.Name}\" is part of Windows.");
            if (entry.Kind != MenuEntryKind.Command) warnings.Add($"This blocks it everywhere it appears for your account ({entry.PlacesText}), not just in one menu.");
            if (warnings.Count > 0 && !Ui.Confirm(string.Join(" ", warnings) + "\n\nHide it? You can show it again here.", "Context menus")) return;
        }
        await Apply(() => ContextMenus.SetHidden(entry, hide), entry.NeedsExplorerRestart);
    }

    async void Shift_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        await Apply(() => ContextMenus.SetShiftOnly(entry, !entry.ShiftOnly));
    }

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContextMenuDialog(null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        // Controls belong to the UI thread, so take the values before leaving it.
        var command = dialog.Result;
        var classNames = dialog.ClassNames.ToList();
        await Apply(() => ContextMenus.Add(command, classNames));
    }

    async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Custom: true } entry) return;
        var dialog = new ContextMenuDialog(entry) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var command = dialog.Result;
        await Apply(() => ContextMenus.Update(entry, command));
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Custom: true } entry) return;
        if (!Ui.Confirm($"Delete \"{entry.Name}\" from {entry.PlacesText}?", "Context menus")) return;
        await Apply(() => ContextMenus.Delete(entry));
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();

    async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Restart Explorer? The taskbar disappears for a moment and open File Explorer windows close.", "Context menus")) return;
        ExplorerButton.IsEnabled = false;
        await Task.Run(TweaksPage.RestartExplorer);
        ExplorerButton.IsEnabled = true;
        PendingBar.Visibility = Visibility.Collapsed;
    }
}
