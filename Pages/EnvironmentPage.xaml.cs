using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public partial class EnvironmentPage : UserControl, IPage
{
    /// <summary>Windows and many apps stop working properly without these.</summary>
    static readonly HashSet<string> Essential = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path", "PATHEXT", "ComSpec", "windir", "OS", "TEMP", "TMP", "PSModulePath", "DriverData",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
    };

    readonly ObservableCollection<EnvVariable> _items = new();
    readonly ICollectionView _view;
    string _filter = "";
    string? _loadError;
    bool _busy;

    public EnvironmentPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(EnvVariable.ScopeText), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(EnvVariable.Name), ListSortDirection.Ascending));
        _view.Filter = o => o is EnvVariable v && (_filter.Length == 0 || Ui.Contains(v.Name, _filter) || Ui.Contains(v.Value, _filter));
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        SizeChanged += (_, _) => DetailsPane.MaxHeight = Math.Max(72, ActualHeight * 0.24);
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    async void Load()
    {
        var selected = Selected;
        var loaded = await Task.Run(() =>
        {
            try
            {
                return (Items: EnvironmentStore.Read(EnvScope.User).Concat(EnvironmentStore.Read(EnvScope.System)).ToList(), Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Items: new List<EnvVariable>(), Error: "Couldn't read environment variables: " + ex.Message);
            }
        });
        _loadError = loaded.Error;
        _items.Clear();
        foreach (var v in loaded.Items) _items.Add(v);
        if (selected != null) List.SelectedItem = _items.FirstOrDefault(v => v.Scope == selected.Scope && v.Name == selected.Name);
        ApplyFilter();
        List_SelectionChanged(this, null!);
    }

    void ApplyFilter()
    {
        _view.Refresh();
        int shown = _view.Cast<object>().Count();
        int user = _items.Count(v => v.Scope == EnvScope.User), system = _items.Count - user;
        CountText.Text = shown == _items.Count ? $"{user} user · {system} system" : $"{shown} of {_items.Count} variables";
        EmptyText.Text = _loadError ?? (_items.Count == 0 ? "Windows has no environment variables stored here." : "No variables match your search.");
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    EnvVariable? Selected => List.SelectedItem as EnvVariable;

    static bool CanWrite(EnvScope scope) => scope == EnvScope.User || MainWindow.IsAdmin;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var v = Selected;
        // The PATH editor opens read-only without rights, so looking at a system PATH is still allowed.
        EditButton.IsEnabled = EditMenu.IsEnabled = !_busy && v != null && v.IsString && (v.IsPath || CanWrite(v.Scope));
        DeleteButton.IsEnabled = DeleteMenu.IsEnabled = !_busy && v != null && CanWrite(v.Scope);
        if (v == null)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsPane.Visibility = Visibility.Visible;
        DetailTitle.Text = $"{v.Scope} variable: {v.Name}";
        DetailValue.Text = v.IsPath ? string.Join("\n", EnvironmentStore.SplitPath(v.Value)) : v.Value;
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is DataGridRow && EditButton.IsEnabled) Edit_Click(sender, e);
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EnvVariableDialog(null) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        string name = dialog.VariableName;
        var scope = dialog.Scope;
        if (_items.Any(v => v.Scope == scope && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Error($"A {scope.ToString().ToLowerInvariant()} variable named {name} already exists. Select it and choose Edit instead.");
            return;
        }
        if (!Ui.Confirm($"Add the {scope.ToString().ToLowerInvariant()} variable \"{name}\"?\n\nNew apps will see it. If it has the same name as a variable a program already relies on, that program may behave differently."))
            return;
        // Read now: the dialog's values can't be touched from the background thread that does the saving.
        string value = dialog.VariableValue;
        bool expandable = dialog.Expandable;
        Write($"add {name}", () => EnvironmentStore.Save(scope, name, value, expandable));
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsString: true } v) return;
        if (v.IsPath)
        {
            EditPath(v.Scope);
            return;
        }
        if (!CanWrite(v.Scope)) return;
        var dialog = new EnvVariableDialog(v) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        string value = dialog.VariableValue;
        bool expandable = dialog.Expandable;
        var kindChanged = expandable != (v.Kind == RegistryValueKind.ExpandString);
        if (value == v.Value && !kindChanged) return;
        if (!Ui.Confirm($"Change the {v.Scope.ToString().ToLowerInvariant()} variable \"{v.Name}\"?\n\nThe old value is saved to a backup file first.{EssentialWarning(v.Name)}")) return;
        Write($"change {v.Name}", () => EnvironmentStore.Save(v.Scope, v.Name, value, expandable));
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } v || !CanWrite(v.Scope)) return;
        if (!Ui.Confirm($"Delete the {v.Scope.ToString().ToLowerInvariant()} variable \"{v.Name}\"?\n\nApps that rely on it may stop working. The old value is saved to a backup file first.{EssentialWarning(v.Name)}")) return;
        Write($"delete {v.Name}", () => EnvironmentStore.Delete(v.Scope, v.Name));
    }

    static string EssentialWarning(string name) =>
        Essential.Contains(name) ? $"\n\n{name} is one that Windows and many apps depend on. A wrong value can stop programs from starting." : "";

    void UserPath_Click(object sender, RoutedEventArgs e) => EditPath(EnvScope.User);

    void SystemPath_Click(object sender, RoutedEventArgs e) => EditPath(EnvScope.System);

    void EditPath(EnvScope scope)
    {
        // Read again so the editor can't start from a stale copy.
        EnvVariable? current;
        try
        {
            current = EnvironmentStore.Find(scope, EnvironmentStore.PathName);
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't read PATH: {ex.Message}");
            return;
        }
        if (current is { IsString: false })
        {
            Ui.Error("This PATH isn't stored as text, so Tman won't edit it.");
            return;
        }
        var old = EnvironmentStore.SplitPath(current?.Value ?? "");
        bool canWrite = CanWrite(scope);
        var dialog = new PathEditorDialog(scope, old, !canWrite) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || !canWrite) return;

        var entries = dialog.Entries.Where(t => t.Length > 0).ToList();
        if (entries.SequenceEqual(old)) return;
        if (entries.Count == 0)
        {
            Ui.Error("PATH can't be left empty. To remove it completely, select it and choose Delete.");
            return;
        }
        var value = EnvironmentStore.JoinPath(entries);
        int removed = old.Except(entries, StringComparer.OrdinalIgnoreCase).Count(), added = entries.Except(old, StringComparer.OrdinalIgnoreCase).Count();
        if (!Ui.Confirm($"Save the {scope.ToString().ToLowerInvariant()} PATH?\n\n{entries.Count} entries: {added} added, {removed} removed.\n\nPrograms in a removed folder can no longer be started by name alone. The old value is saved to a backup file first."))
            return;
        // Keep REG_EXPAND_SZ. A plain REG_SZ PATH only needs it if an entry now refers to another variable.
        bool expandable = current == null || current.Kind == RegistryValueKind.ExpandString || value.Contains('%');
        Write("save PATH", () => EnvironmentStore.Save(scope, EnvironmentStore.PathName, value, expandable));
    }

    async void Write(string what, Action write)
    {
        if (_busy) return;
        _busy = true;
        List_SelectionChanged(this, null!);
        StatusText.Text = "Saving…";
        var error = await Task.Run(() =>
        {
            try
            {
                write();
            }
            catch (Exception ex)
            {
                return ex is UnauthorizedAccessException or System.Security.SecurityException ? "Windows refused the change. System variables need administrator rights." : ex.Message;
            }
            EnvironmentStore.Broadcast();
            return null;
        });
        _busy = false;
        if (error != null)
        {
            StatusText.Text = "Nothing was changed.";
            Ui.Error($"Couldn't {what}.\n\n{error}");
        }
        else
        {
            StatusText.Text = "Saved, and the old value is in the Backups folder. Running apps need restarting to see the change.";
        }
        Load();
    }

    void Backups_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(EnvironmentStore.BackupFolder))
        {
            Ui.Error("There are no backups yet. Tman makes one before each change.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(EnvironmentStore.BackupFolder) { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error("Couldn't open the backups folder: " + ex.Message); }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } v) Ui.Copy(v.Value);
    }

    void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } v) Ui.Copy(v.Name);
    }
}
