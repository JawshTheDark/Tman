using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Tman.Core;

namespace Tman.Pages;

public partial class ScheduledTasksPage : UserControl, IPage
{
    readonly ObservableCollection<ScheduledTaskInfo> _items = new();
    readonly ICollectionView _view;
    string _filter = "";
    string? _loadError;
    bool _loading = true;
    bool _busy;

    public ScheduledTasksPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(ScheduledTaskInfo.Name), ListSortDirection.Ascending));
        _view.Filter = Matches;
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        SizeChanged += (_, _) => DetailsPane.MaxHeight = Math.Max(80, ActualHeight * 0.26);
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    async void Load()
    {
        _loading = true;
        CountText.Text = "Loading tasks…";
        var selected = Selected?.TaskPath;
        List<ScheduledTaskInfo> items = new();
        try
        {
            items = await Task.Run(ScheduledTaskList.Read);
            _loadError = null;
        }
        catch (Exception ex)
        {
            _loadError = "Couldn't read scheduled tasks: " + ex.Message;
        }
        _items.Clear();
        foreach (var i in items) _items.Add(i);
        _loading = false;
        if (selected != null) List.SelectedItem = _items.FirstOrDefault(i => i.TaskPath == selected);
        ApplyFilter();
        List_SelectionChanged(this, null!);
    }

    bool Matches(object o)
    {
        var t = (ScheduledTaskInfo)o;
        if (t.IsMicrosoft && ShowMicrosoft.IsChecked != true) return false;
        if (FailedOnly.IsChecked == true && !t.Failed) return false;
        return _filter.Length == 0 || Ui.Contains(t.Name, _filter) || Ui.Contains(t.Folder, _filter) || Ui.Contains(t.Author, _filter) || Ui.Contains(t.Action, _filter);
    }

    void ApplyFilter()
    {
        _view.Refresh();
        if (_loading) return;
        int shown = _view.Cast<object>().Count(), hidden = _items.Count(t => t.IsMicrosoft), failed = _items.Count(t => t.Failed && (!t.IsMicrosoft || ShowMicrosoft.IsChecked == true));
        CountText.Text = $"{shown} of {_items.Count} tasks" + (failed > 0 ? $" · {failed} failed last run" : "")
            + (ShowMicrosoft.IsChecked == true || hidden == 0 ? "" : $" · {hidden} Microsoft tasks hidden");

        EmptyText.Text = _loadError
            ?? (_items.Count == 0 ? "Task Scheduler didn't report any tasks you can see."
                : _filter.Length > 0 ? "No tasks match your search."
                : FailedOnly.IsChecked == true ? "None of the tasks shown failed their last run."
                : "Every task here is one of Microsoft's. Turn on Show Microsoft's tasks to see them.");
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    ScheduledTaskInfo? Selected => List.SelectedItem as ScheduledTaskInfo;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var t = Selected;
        RunButton.IsEnabled = RunMenu.IsEnabled = !_busy && t is { State: "Ready" or "Queued" };
        EndButton.IsEnabled = EndMenu.IsEnabled = !_busy && t is { State: "Running" };
        EnableButton.IsEnabled = EnableMenu.IsEnabled = !_busy && t is { State: "Disabled" };
        DisableButton.IsEnabled = DisableMenu.IsEnabled = !_busy && t is { State: not "Disabled" };
        if (t == null)
        {
            DetailsPane.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsPane.Visibility = Visibility.Visible;
        DetailTitle.Text = t.Name;
        DetailTitle.ToolTip = t.TaskPath;
        var lines = new List<string> { t.TaskPath, "Last result: " + t.ResultText };
        if (t.RunsAs.Length > 0) lines.Add("Runs as " + t.RunsAs);
        if (t.Description.Length > 0) lines.Add(t.Description);
        DetailInfo.Text = string.Join("\n", lines);
        DetailTriggers.Text = t.Triggers.Count > 0 ? string.Join("\n", t.Triggers) : "No triggers. It only runs when something starts it.";
        DetailAction.Text = t.Actions.Count > 0 ? string.Join("\n", t.Actions) : t.Action.Length > 0 ? t.Action : "This task has no actions.";
    }

    void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { State: "Ready" or "Queued" } t
            && Ui.Confirm($"Run the task \"{t.Name}\" now?\n\nIt does what it normally does on its schedule:\n{t.Action}"))
            Act(t, "run", ScheduledTaskList.Run);
    }

    void End_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { State: "Running" } t
            && Ui.Confirm($"End the running task \"{t.Name}\"?\n\nIts program stops right away, and any unfinished work may be lost."))
            Act(t, "end", ScheduledTaskList.End);
    }

    void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { State: "Disabled" } t
            && Ui.Confirm($"Enable the task \"{t.Name}\"?\n\nIt runs on its schedule again, starting with its next trigger."))
            Act(t, "enable", path => ScheduledTaskList.SetEnabled(path, true));
    }

    void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { State: not "Disabled" } t
            && Ui.Confirm($"Disable the task \"{t.Name}\"?\n\nIt won't run until you enable it again. Anything that depends on it, such as an app's updates or a Windows feature, may stop working."))
            Act(t, "disable", path => ScheduledTaskList.SetEnabled(path, false));
    }

    async void Act(ScheduledTaskInfo t, string verb, Action<string> action)
    {
        if (_busy) return;
        _busy = true;
        List_SelectionChanged(this, null!);
        var error = await Task.Run(() =>
        {
            try
            {
                action(t.TaskPath);
                return null;
            }
            catch (Exception ex)
            {
                return ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005)
                    ? "Windows didn't allow this. Tasks that belong to Windows or other accounts need administrator rights."
                      + (MainWindow.IsAdmin ? "" : "\n\nUse File > Restart as administrator.")
                    : ex.Message;
            }
        });
        _busy = false;
        if (error != null) Ui.Error($"Couldn't {verb} \"{t.Name}\":\n\n{error}");
        Load();
    }

    void OpenScheduler_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error("Couldn't open Task Scheduler: " + ex.Message); }
    }

    void SearchOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } t) Ui.SearchOnline($"{t.Name} scheduled task");
    }

    void CopyAction_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } t) Ui.Copy(string.Join(Environment.NewLine, t.Actions));
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } t) Ui.Copy(t.TaskPath);
    }
}
