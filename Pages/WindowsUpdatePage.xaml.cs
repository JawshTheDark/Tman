using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

public partial class WindowsUpdatePage : UserControl, IPage
{
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    List<UpdateHistoryItem> _history = new();
    ICollectionView? _view;
    UpdateModeState? _mode;
    string _filter = "";
    string? _historyError;
    DateTime _searchStarted;
    DateTime? _recordedInstall;
    bool _searching;
    bool _firstShown = true;

    public WindowsUpdatePage()
    {
        InitializeComponent();
        Ui.SetupGrid(History);
        Ui.SetupGrid(Pending);
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        _searchTimer.Tick += (_, _) => ShowSearchProgress();
        SizeChanged += (_, _) => HistoryDetails.MaxHeight = PendingDetails.MaxHeight = Math.Max(64, ActualHeight * 0.2);
    }

    public void FocusSearch()
    {
        Tabs.SelectedIndex = 0;
        Search.FocusBox();
    }

    public void OnShown()
    {
        if (_firstShown) LoadHistory();
        _firstShown = false;
        LoadTimes();
        LoadMode();
        if (_searching) _searchTimer.Start();
    }

    public void OnHidden() => _searchTimer.Stop();

    // ---- History ----

    async void LoadHistory()
    {
        HistoryCount.Text = "Loading history…";
        List<UpdateHistoryItem> items = new();
        try
        {
            items = await Task.Run(() => WindowsUpdateInfo.ReadHistory());
            _historyError = null;
        }
        catch (Exception ex)
        {
            _historyError = "Couldn't read update history: " + Plain(ex);
        }
        _history = items;
        History.ItemsSource = _history;
        _view = CollectionViewSource.GetDefaultView(_history);
        _view.Filter = Matches;
        ApplyFilter();
        ShowLastInstall();
    }

    /// <summary>Windows' own record of the last install skips Store and some other updates, so the newest history entry counts too.</summary>
    void ShowLastInstall()
    {
        var newest = _history.FirstOrDefault(h => h.Result == "Installed")?.Date;
        var last = _recordedInstall == null || newest > _recordedInstall ? newest : _recordedInstall;
        LastInstallText.Text = last?.ToString("g") ?? "Not recorded";
    }

    bool Matches(object o)
    {
        var h = (UpdateHistoryItem)o;
        if (FailedOnly.IsChecked == true && !h.Failed) return false;
        return _filter.Length == 0 || Ui.Contains(h.Title, _filter) || Ui.Contains(h.Result, _filter) || Ui.Contains(h.ErrorText, _filter);
    }

    void ApplyFilter()
    {
        if (_view == null) return;
        _view.Refresh();
        int shown = _view.Cast<object>().Count(), failed = _history.Count(h => h.Failed);
        HistoryCount.Text = (shown == _history.Count ? $"{shown} updates" : $"{shown} of {_history.Count}") + (failed > 0 ? $" · {failed} failed" : "");
        HistoryEmpty.Text = _historyError
            ?? (_history.Count == 0 ? "Windows has no update history on this PC yet."
                : _filter.Length > 0 ? "No updates match your search."
                : "No update has failed in the history Windows keeps.");
        HistoryEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FailedOnly_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadHistory();
        LoadTimes();
        LoadMode();
    }

    void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (History.SelectedItem is not UpdateHistoryItem h)
        {
            HistoryDetails.Visibility = Visibility.Collapsed;
            return;
        }
        HistoryDetails.Visibility = Visibility.Visible;
        HistoryTitle.Text = h.Title;
        HistoryError.Text = h.ErrorText;
        HistoryError.Visibility = h.ErrorText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryDesc.Text = h.Description;
        HistoryDesc.Visibility = h.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SearchHistory_Click(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is UpdateHistoryItem h) Ui.SearchOnline(h.ErrorCode != 0 ? $"Windows Update error 0x{h.ErrorCode:X8}" : h.Title);
    }

    void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is UpdateHistoryItem h) Ui.Copy($"{h.Title}\t{h.DateText}\t{h.Result}\t{h.ErrorText}");
    }

    async void LoadTimes()
    {
        var (check, install) = await Task.Run(WindowsUpdateInfo.ReadLastTimes);
        LastCheckText.Text = check?.ToString("g") ?? "Not recorded";
        _recordedInstall = install;
        ShowLastInstall();
    }

    void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error("Couldn't open Windows Update settings: " + ex.Message); }
    }

    // ---- Pending updates ----

    async void SearchUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_searching) return;
        _searching = true;
        _searchStarted = DateTime.Now;
        SearchButton.IsEnabled = false;
        SearchProgress.Visibility = Visibility.Visible;
        PendingEmpty.Visibility = Visibility.Collapsed;
        Pending.ItemsSource = null;
        PendingDetails.Visibility = Visibility.Collapsed;
        ShowSearchProgress();
        _searchTimer.Start();

        List<PendingUpdate> found = new();
        string? error = null;
        try
        {
            // Can run for minutes, so it gets its own thread rather than holding one from the pool.
            found = await Task.Factory.StartNew(WindowsUpdateInfo.SearchPending, TaskCreationOptions.LongRunning);
        }
        catch (Exception ex)
        {
            error = Plain(ex);
        }

        _searchTimer.Stop();
        _searching = false;
        SearchButton.IsEnabled = true;
        SearchProgress.Visibility = Visibility.Collapsed;
        Pending.ItemsSource = found;
        var when = DateTime.Now.ToString("t");
        SearchStatus.Text = error != null ? $"The search failed at {when}."
            : found.Count == 0 ? $"Searched at {when}."
            : $"{found.Count} update{(found.Count == 1 ? "" : "s")} waiting, as of {when}. Install them from Windows Update settings.";
        PendingEmpty.Text = error != null ? "Windows Update couldn't finish the search. " + error : "No updates are waiting. This PC is up to date.";
        PendingEmpty.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadTimes();
    }

    void ShowSearchProgress() =>
        SearchStatus.Text = $"Searching… {Fmt.Span(DateTime.Now - _searchStarted)}. This can take a minute or more, and you can use the rest of Tman meanwhile.";

    void Pending_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var u = Pending.SelectedItem as PendingUpdate;
        PendingDetails.Visibility = u is { Description.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        PendingDesc.Text = u?.Description ?? "";
    }

    void SearchPending_Click(object sender, RoutedEventArgs e)
    {
        if (Pending.SelectedItem is PendingUpdate u) Ui.SearchOnline(u.Kb.Length > 0 ? u.Kb : u.Title);
    }

    static string Plain(Exception ex) => ex.HResult < 0 && ex is System.Runtime.InteropServices.COMException ? WindowsUpdateInfo.Explain(ex.HResult) + "." : ex.Message;

    // ---- Update mode ----

    async void LoadMode()
    {
        try
        {
            ShowMode(await Task.Run(WindowsUpdateMode.Read));
        }
        catch (Exception ex)
        {
            _mode = null;
            ModeShort.Text = "Unknown";
            ModeTitle.Text = "Unknown";
            ModeSummary.Text = "Couldn't read the update settings: " + ex.Message;
            ModeNotes.Visibility = Visibility.Collapsed;
            UpdateSwitch();
        }
    }

    void ShowMode(UpdateModeState mode)
    {
        _mode = mode;
        ModeShort.Text = mode.SetByOthers ? mode.Title + " (someone else's policy)" : mode.Title;
        ModeShort.ToolTip = ModeShort.Text;
        ModeTitle.Text = mode.Title;
        ModeSummary.Text = mode.Summary;
        ModeNotes.Text = string.Join("\n", mode.Notes);
        ModeNotes.Visibility = mode.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PickDefault.IsChecked = mode.Mode == UpdateMode.Default;
        PickSecurity.IsChecked = mode.Mode == UpdateMode.Security;
        PickPause.IsChecked = mode.Mode == UpdateMode.Paused;
        UpdateSwitch();
    }

    void Pick_Click(object sender, RoutedEventArgs e) => UpdateSwitch();

    void UpdateSwitch()
    {
        PauseWeeks.IsEnabled = PickPause.IsChecked == true;
        // Reapplying the current mode is allowed: Default then also clears a stale, expired pause.
        SwitchButton.IsEnabled = MainWindow.IsAdmin && _mode != null && (PickDefault.IsChecked == true || PickSecurity.IsChecked == true || PickPause.IsChecked == true);
    }

    async void Switch_Click(object sender, RoutedEventArgs e)
    {
        Action change;
        string summary;
        if (PickSecurity.IsChecked == true)
        {
            change = WindowsUpdateMode.SetSecurityFocused;
            summary = "Switch Windows Update to Security-focused?\n\nThis sets Windows update policies so that:\n"
                + "• feature updates wait 365 days\n• quality updates, including security fixes, wait 4 days\n• drivers are left out of updates\n\nAny pause is cleared.\n\n"
                + "What could go wrong: security fixes arrive 4 days later, driver fixes no longer come through Windows Update, and Settings will say your organization manages some settings.";
        }
        else if (PickPause.IsChecked == true)
        {
            int weeks = PauseWeeks.SelectedIndex + 1;
            var until = await Task.Run(() => WindowsUpdateMode.PauseEnd(weeks));
            change = () => WindowsUpdateMode.Pause(weeks);
            summary = $"Pause Windows Update until {until:f}?\n\nThis uses Windows' own pause setting, and any deferral policies Tman or anyone else set here are removed. Updates start again by themselves on that date.\n\n"
                + "What could go wrong: no updates install until then, not even security fixes, so the PC stays open to anything they would have fixed.";
        }
        else
        {
            change = WindowsUpdateMode.SetDefault;
            summary = "Switch Windows Update to Default?\n\nThis removes the deferral and driver policies and any pause, so Windows offers updates as soon as they're ready.\n\n"
                + "What could go wrong: a feature update that was being held back can install soon after this.";
        }
        if (_mode?.SetByOthers == true)
            summary += "\n\nThe current values were set by someone else's policy. This replaces them, and Tman can't bring them back. If Group Policy or your organization set them, they can return.";
        if (!Ui.Confirm(summary)) return;

        SwitchButton.IsEnabled = false;
        var error = await Task.Run(() =>
        {
            try
            {
                change();
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return ex.Message;
            }
        });
        if (error != null)
            Ui.Error($"Couldn't switch the update mode, and some of the change may have been made:\n\n{error}\n\nThe page now shows what's actually set.");
        LoadMode();
    }
}
