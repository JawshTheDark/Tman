using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

/// <summary>Add a variable, or change the value of an existing one. Nothing is written here: the page does that after asking.</summary>
public sealed class EnvVariableDialog : Window
{
    readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 12) };
    readonly TextBox _value = new()
    {
        Margin = new Thickness(0, 4, 0, 10),
        AcceptsReturn = false,
        TextWrapping = TextWrapping.Wrap,
        Height = 120,
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    readonly CheckBox _expand = new() { Content = "Expand %NAME% references in the value", MinWidth = 0 };
    readonly RadioButton _user = new() { Content = "User", GroupName = "EnvScope", IsChecked = true, MinWidth = 0, Margin = new Thickness(0, 0, 16, 0) };
    readonly RadioButton _system = new() { Content = "System", GroupName = "EnvScope", MinWidth = 0 };
    readonly bool _adding;

    public string VariableName => _name.Text.Trim();
    public string VariableValue => _value.Text;
    public bool Expandable => _expand.IsChecked == true;
    public EnvScope Scope => _system.IsChecked == true ? EnvScope.System : EnvScope.User;

    /// <param name="existing">The variable to edit, or null to add one.</param>
    public EnvVariableDialog(EnvVariable? existing)
    {
        _adding = existing == null;
        Title = _adding ? "Add environment variable" : $"Edit {existing!.Name}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        var root = new StackPanel { Margin = new Thickness(20) };
        if (_adding)
        {
            root.Children.Add(Label("Applies to"));
            var scopes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
            _system.IsEnabled = MainWindow.IsAdmin;
            _system.ToolTip = MainWindow.IsAdmin ? null : "System variables need administrator rights.";
            scopes.Children.Add(_user);
            scopes.Children.Add(_system);
            root.Children.Add(scopes);
        }
        root.Children.Add(Label("Name"));
        _name.Text = existing?.Name ?? "";
        _name.IsReadOnly = !_adding;
        root.Children.Add(_name);
        root.Children.Add(Label("Value"));
        _value.Text = existing?.Value ?? "";
        root.Children.Add(_value);
        _expand.IsChecked = existing?.Kind == RegistryValueKind.ExpandString;
        root.Children.Add(_expand);

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => (_adding ? _name : _value).Focus();
    }

    static TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.FindResource("CardTitle") };

    void Accept()
    {
        var problem = VariableName.Length == 0 ? "Enter a name."
            : VariableName.Contains('=') || VariableName.Contains('\\') || VariableName.Contains('\0') ? "A name can't contain =, \\ or a null character."
            : VariableName.Length > 255 ? "That name is too long. Windows allows 255 characters."
            : VariableValue.Length > 32766 ? "That value is too long. Windows allows 32,766 characters."
            : null;
        if (problem != null)
        {
            Ui.Error(problem);
            return;
        }
        // A new value that refers to another variable only works as REG_EXPAND_SZ, which is also what Windows' own dialog picks.
        if (_adding && System.Text.RegularExpressions.Regex.IsMatch(VariableValue, "%[^%]+%")) _expand.IsChecked = true;
        DialogResult = true;
    }
}

public sealed class PathEntryRow : ObservableObject
{
    string _text = "";
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value.Trim())) Changed?.Invoke(); }
    }

    string _status = "";
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(StatusBrush)); } }

    public bool Problem { get; set; }
    public Brush StatusBrush => (Brush)Application.Current.FindResource(Problem ? "WarnBrush" : _status == "OK" ? "GoodBrush" : "SubtleBrush");

    public event Action? Changed;
}

/// <summary>One PATH entry per row, flagging folders that don't exist and duplicates. Nothing is written here: the page does that after asking.</summary>
public sealed class PathEditorDialog : Window
{
    readonly ObservableCollection<PathEntryRow> _rows = new();
    readonly DataGrid _grid = new() { SelectionMode = DataGridSelectionMode.Single };
    readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    readonly Button _up = new() { Content = "Move up", Margin = new Thickness(0, 0, 8, 6) };
    readonly Button _down = new() { Content = "Move down", Margin = new Thickness(0, 0, 8, 6) };
    readonly Button _remove = new() { Content = "Remove", Margin = new Thickness(0, 0, 8, 6) };
    readonly bool _readOnly;
    int _checkRun;

    public IEnumerable<string> Entries => _rows.Select(r => r.Text);

    public PathEditorDialog(EnvScope scope, IEnumerable<string> entries, bool readOnly)
    {
        _readOnly = readOnly;
        Title = $"{scope} PATH";
        Width = 700;
        Height = 560;
        MinWidth = 500;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        foreach (var e in entries) AddRow(e);

        var root = new DockPanel { Margin = new Thickness(20) };

        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(_summary);
        var close = new Button { Content = readOnly ? "Close" : "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (!readOnly)
        {
            var save = new Button { Content = "Save", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (_, _) =>
            {
                _grid.CommitEdit(DataGridEditingUnit.Row, true);
                DialogResult = true;
            };
            buttons.Children.Add(save);
        }
        buttons.Children.Add(close);
        bottom.Children.Add(buttons);
        root.Children.Add(bottom);

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock
        {
            Text = readOnly
                ? "You can look but not change this list: system variables need administrator rights."
                : "Programs search these folders in order, top first. Double-click an entry to edit it.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("Subtle"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        if (!readOnly)
        {
            var add = new Button { Content = "Add folder…", Margin = new Thickness(0, 0, 8, 6) };
            add.Click += (_, _) => AddFolder();
            _up.Click += (_, _) => Move(-1);
            _down.Click += (_, _) => Move(1);
            _remove.Click += (_, _) => Remove();
            var tools = new WrapPanel();
            tools.Children.Add(add);
            tools.Children.Add(_up);
            tools.Children.Add(_down);
            tools.Children.Add(_remove);
            top.Children.Add(tools);
        }
        root.Children.Add(top);

        Ui.SetupGrid(_grid);
        _grid.IsReadOnly = readOnly;
        _grid.ItemsSource = _rows;
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Folder",
            Binding = new Binding(nameof(PathEntryRow.Text)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 240,
            ElementStyle = (Style)FindResource("CellText"),
            CanUserSort = false,
        });
        var status = new FrameworkElementFactory(typeof(TextBlock));
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(PathEntryRow.Status)));
        status.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(PathEntryRow.StatusBrush)));
        status.SetValue(StyleProperty, FindResource("CellText"));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Status",
            IsReadOnly = true,
            CanUserSort = false,
            Width = 170,
            MinWidth = 170,
            CellTemplate = new DataTemplate { VisualTree = status },
        });
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        root.Children.Add(_grid);
        Content = root;

        UpdateButtons();
        Recheck();
    }

    PathEntryRow AddRow(string text, int? at = null)
    {
        var row = new PathEntryRow { Text = text };
        row.Changed += Recheck;
        _rows.Insert(at ?? _rows.Count, row);
        return row;
    }

    PathEntryRow? Selected => _grid.SelectedItem as PathEntryRow;

    void UpdateButtons()
    {
        int i = Selected == null ? -1 : _rows.IndexOf(Selected);
        _up.IsEnabled = i > 0;
        _down.IsEnabled = i >= 0 && i < _rows.Count - 1;
        _remove.IsEnabled = i >= 0;
    }

    void AddFolder()
    {
        var pick = new OpenFolderDialog { Title = "Add a folder to PATH" };
        if (pick.ShowDialog(this) != true) return;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var at = Selected == null ? _rows.Count : _rows.IndexOf(Selected) + 1;
        _grid.SelectedItem = AddRow(pick.FolderName, at);
        Recheck();
    }

    void Move(int delta)
    {
        if (Selected is not { } row) return;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        int from = _rows.IndexOf(row), to = from + delta;
        if (to < 0 || to >= _rows.Count) return;
        _rows.Move(from, to);
        _grid.ScrollIntoView(row);
        UpdateButtons();
        Recheck();
    }

    void Remove()
    {
        if (Selected is not { } row) return;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        int at = _rows.IndexOf(row);
        _rows.RemoveAt(at);
        if (_rows.Count > 0) _grid.SelectedIndex = Math.Min(at, _rows.Count - 1);
        Recheck();
    }

    /// <summary>Checks every folder off the UI thread, since a dead network path can take many seconds to answer.</summary>
    async void Recheck()
    {
        int run = ++_checkRun;
        var texts = _rows.Select(r => r.Text).ToList();
        var results = await Task.Run(() => Check(texts));
        if (run != _checkRun) return;
        for (int i = 0; i < _rows.Count && i < results.Count; i++)
        {
            _rows[i].Problem = results[i].Problem;
            _rows[i].Status = results[i].Status;
        }
        int missing = results.Count(r => r.Status == "Folder not found"), dupes = results.Count(r => r.Status.StartsWith("Duplicate", StringComparison.Ordinal));
        _summary.Text = $"{results.Count} entries" + (missing > 0 ? $" · {missing} not found" : "") + (dupes > 0 ? $" · {dupes} duplicate" : "")
            + $" · {texts.Sum(t => t.Length + 1)} characters";
    }

    static List<(string Status, bool Problem)> Check(List<string> entries)
    {
        var results = new List<(string, bool)>();
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < entries.Count; i++)
        {
            var key = EnvironmentStore.NormalizePathEntry(entries[i]);
            if (seen.TryGetValue(key, out var first))
            {
                results.Add(($"Duplicate of row {first + 1}", true));
                continue;
            }
            seen[key] = i;
            bool exists;
            try { exists = Directory.Exists(Environment.ExpandEnvironmentVariables(entries[i]).Trim('"')); }
            catch { exists = false; }
            results.Add(exists ? ("OK", false) : ("Folder not found", true));
        }
        return results;
    }
}
