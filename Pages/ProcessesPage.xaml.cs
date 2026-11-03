using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public partial class ProcessesPage : UserControl, IPage
{
    readonly ObservableCollection<ProcessItem> _items = new();
    readonly Dictionary<(int, long), ProcessItem> _map = new();
    readonly ListCollectionView _view;
    string _filter = "";
    bool _visible;

    public ProcessesPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_items);
        _view.IsLiveSorting = true;
        _view.Filter = o => Matches((ProcessItem)o);
        Ui.SortBy(List, nameof(ProcessItem.Cpu), ListSortDirection.Descending);
        SetupColumnChooser();

        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
            UpdateCount();
        };
        List.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete)
            {
                EndSelected(tree: false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                ShowDetails();
                e.Handled = true;
            }
        };
        SystemMonitor.Instance.Updated += OnUpdated;
        Signatures.Verified += OnSignatureVerified;
    }

    public void OnShown()
    {
        _visible = true;
        if (SystemMonitor.Instance.Latest is { } s) OnUpdated(s);
    }

    public void OnHidden() => _visible = false;

    public void FocusSearch() => Search.FocusBox();

    bool Matches(ProcessItem p) =>
        _filter.Length == 0
        || Ui.Contains(p.Name, _filter)
        || Ui.Contains(p.Description, _filter)
        || Ui.Contains(p.User, _filter)
        || Ui.Contains(p.Signer, _filter)
        || p.Pid.ToString().StartsWith(_filter, StringComparison.Ordinal);

    void OnUpdated(Snapshot s)
    {
        if (!_visible) return;
        var seen = new HashSet<(int, long)>();
        foreach (var p in s.Processes)
        {
            if (p.Pid == 0) continue;
            var key = (p.Pid, p.CreateTime);
            seen.Add(key);
            if (_map.TryGetValue(key, out var item))
            {
                item.Update(p);
            }
            else
            {
                item = new ProcessItem(p);
                if (CheckSigners) item.SetSignature(item.Path.Length > 0 ? Signatures.GetOrQueue(item.Path) : null);
                _map[key] = item;
                _items.Add(item);
            }
        }
        if (_map.Count != seen.Count)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var it = _items[i];
                var key = (it.Pid, it.CreateTime);
                if (seen.Contains(key)) continue;
                _items.RemoveAt(i);
                _map.Remove(key);
            }
        }
        UpdateCount();
    }

    void UpdateCount() =>
        CountText.Text = _filter.Length == 0 ? $"{_items.Count} processes" : $"{_view.Count} of {_items.Count} processes";

    // ---- Column chooser (right-click a header) ----

    void SetupColumnChooser()
    {
        var hidden = Settings.Current.HiddenProcessColumns;
        var menu = new ContextMenu();
        foreach (var col in List.Columns)
        {
            var header = (string)col.Header;
            if (header == "Name") continue;
            if (hidden.Contains(header)) col.Visibility = Visibility.Collapsed;
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = col.Visibility == Visibility.Visible };
            item.Click += (_, _) =>
            {
                col.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                hidden.Remove(header);
                if (!item.IsChecked) hidden.Add(header);
                if (col == SignerColumn) QueueSignatures();
            };
            menu.Items.Add(item);
        }
        var baseStyle = List.ColumnHeaderStyle ?? TryFindResource(typeof(DataGridColumnHeader)) as Style;
        var style = new Style(typeof(DataGridColumnHeader), baseStyle);
        style.Setters.Add(new Setter(ContextMenuProperty, menu));
        List.ColumnHeaderStyle = style;
    }

    // ---- Selection / details ----

    List<ProcessItem> Selected => List.SelectedItems.Cast<ProcessItem>().ToList();

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EndButton.IsEnabled = List.SelectedItems.Count > 0;
        if (List.SelectedItem is not ProcessItem p)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsPane.Visibility = Visibility.Visible;
        DetailIcon.Source = p.Icon;
        DetailName.Text = p.Name;
        DetailDesc.Text = p.Description.Length > 0 ? "  " + p.Description + (p.Company.Length > 0 ? $"  ·  {p.Company}" : "") : "";
        DetailPath.Text = p.Path.Length > 0 ? p.Path : "Path unavailable (try running as administrator)";
        DetailPath.ToolTip = p.Path.Length > 0 ? p.Path : null;
        DetailCmd.Text = p.CommandLine;
        DetailCmd.ToolTip = p.CommandLine.Length > 0 ? p.CommandLine : null;
        DetailCmd.Visibility = p.CommandLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var parent = _items.FirstOrDefault(x => x.Pid == p.ParentPid && x.CreateTime <= p.CreateTime);
        var meta = new List<string> { $"PID {p.Pid}" };
        if (parent != null) meta.Add($"Parent {parent.Name} ({parent.Pid})");
        if (p.User.Length > 0) meta.Add($"User {p.User}");
        meta.Add($"Session {p.SessionId}");
        if (p.Started.Length > 0) meta.Add($"Started {p.Started}");
        DetailMeta.Text = string.Join("   ·   ", meta);
    }

    void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowDetails();

    void Details_Click(object sender, RoutedEventArgs e) => ShowDetails();

    void ShowDetails()
    {
        if (List.SelectedItem is ProcessItem p) new ProcessDetailsWindow(p) { Owner = Window.GetWindow(this) }.Show();
    }

    void Affinity_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is ProcessItem p) new AffinityDialog(p.Pid, p.Name) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    void Dump_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is ProcessItem p) ProcessActions.CreateDump(Window.GetWindow(this), p.Pid, p.Name);
    }

    // ---- Signatures ----

    bool CheckSigners => SignerColumn.Visibility == Visibility.Visible;

    void QueueSignatures()
    {
        if (!CheckSigners) return;
        foreach (var item in _items)
            if (item.Signer.Length == 0 || item.Signer == "Checking…") item.SetSignature(item.Path.Length > 0 ? Signatures.GetOrQueue(item.Path) : null);
    }

    void OnSignatureVerified(string path, SignatureInfo info) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var item in _items)
            if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) item.SetSignature(info);
    });

    // ---- Actions ----

    void End_Click(object sender, RoutedEventArgs e) => EndSelected(tree: false);

    void EndTree_Click(object sender, RoutedEventArgs e) => EndSelected(tree: true);

    void Run_Click(object sender, RoutedEventArgs e) => new RunDialog { Owner = Window.GetWindow(this) }.ShowDialog();

    void EndSelected(bool tree)
    {
        var targets = Selected;
        if (targets.Count == 0) return;

        var critical = targets.Where(t => ProcessActions.IsCritical(t.Name)).Select(t => t.Name).ToList();
        if (critical.Count > 0 && !Ui.Confirm(
                $"{string.Join(", ", critical)} is a critical Windows process.\n\nEnding it can make Windows unusable or force a restart, and you'll lose unsaved data. End it anyway?"))
            return;
        if (tree && !Ui.Confirm($"End {(targets.Count == 1 ? targets[0].Name : $"{targets.Count} processes")} and all of its child processes?"))
            return;

        var errors = new List<string>();
        foreach (var t in targets)
        {
            foreach (var pid in tree ? TreeOf(t) : new List<int> { t.Pid })
            {
                if (!ProcessActions.Terminate(pid, out var err))
                    errors.Add($"{_items.FirstOrDefault(i => i.Pid == pid)?.Name ?? "PID"} ({pid}): {err}");
            }
        }
        ReportErrors("Couldn't end", errors);
        SystemMonitor.Instance.RefreshNow();
    }

    /// <summary>Descendants first, root last. Create times guard against reused PIDs.</summary>
    List<int> TreeOf(ProcessItem root)
    {
        var result = new List<int>();
        var queue = new Queue<ProcessItem>();
        queue.Enqueue(root);
        var visited = new HashSet<int> { root.Pid };
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var child in _items.Where(x => x.ParentPid == cur.Pid && x.CreateTime >= cur.CreateTime && visited.Add(x.Pid)))
            {
                result.Add(child.Pid);
                queue.Enqueue(child);
            }
        }
        result.Reverse();
        result.Add(root.Pid);
        return result;
    }

    void Suspend_Click(object sender, RoutedEventArgs e) => SuspendSelected(resume: false);

    void Resume_Click(object sender, RoutedEventArgs e) => SuspendSelected(resume: true);

    void SuspendSelected(bool resume)
    {
        var targets = Selected;
        if (!resume && targets.Any(t => ProcessActions.IsCritical(t.Name)) &&
            !Ui.Confirm("Suspending a critical Windows process can freeze your PC. Continue?"))
            return;
        var errors = new List<string>();
        foreach (var t in targets)
            if (!ProcessActions.Suspend(t.Pid, resume, out var err)) errors.Add($"{t.Name} ({t.Pid}): {err}");
        ReportErrors(resume ? "Couldn't resume" : "Couldn't suspend", errors);
        SystemMonitor.Instance.RefreshNow();
    }

    void Priority_Click(object sender, RoutedEventArgs e)
    {
        var priority = Enum.Parse<ProcessPriorityClass>((string)((MenuItem)sender).Tag);
        if (priority == ProcessPriorityClass.RealTime &&
            !Ui.Confirm("Realtime priority can starve the rest of the system, including input. Set it anyway?"))
            return;
        var errors = new List<string>();
        foreach (var t in Selected)
            if (!ProcessActions.SetPriority(t.Pid, priority, out var err)) errors.Add($"{t.Name} ({t.Pid}): {err}");
        ReportErrors("Couldn't change priority", errors);
        SystemMonitor.Instance.RefreshNow();
    }

    static void ReportErrors(string what, List<string> errors)
    {
        if (errors.Count == 0) return;
        var tip = MainWindow.IsAdmin ? "" : "\n\nTip: File > Restart as administrator to manage system processes.";
        Ui.Error($"{what}:\n\n{string.Join("\n", errors.Take(10))}{tip}");
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is ProcessItem p) Ui.OpenFileLocation(p.Path);
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is ProcessItem p) Ui.SearchOnline(p.Description.Length > 0 ? $"{p.Name} {p.Description}" : p.Name);
    }

    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is ProcessItem p) Ui.ShowProperties(p.Path);
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var field = (string)((MenuItem)sender).Tag;
        var lines = Selected.Select(p => field switch
        {
            "pid" => p.Pid.ToString(),
            "path" => p.Path,
            "cmd" => p.CommandLine,
            _ => p.Name,
        });
        Ui.Copy(string.Join(Environment.NewLine, lines));
    }
}
