using System.Windows;
using System.Windows.Controls;
using Tman.Core;

namespace Tman.Pages;

public partial class SharesPage : UserControl, IPage
{
    sealed record Snapshot(List<SmbShare> Shares, bool ServerRunning, (bool On, string Network)? Sharing,
        List<SmbSession> Sessions, string? SessionError, List<SmbOpenFile> Files, string? FileError);

    bool _busy;

    public SharesPage()
    {
        InitializeComponent();
        Ui.SetupGrid(Shares);
        Ui.SetupGrid(Sessions);
        Ui.SetupGrid(Files);
        Load();
    }

    async void Load()
    {
        ShareCount.Text = "Loading…";
        var selectedShare = (Shares.SelectedItem as SmbShare)?.Name;
        var s = await Task.Run(() =>
        {
            var sessions = SmbShares.ReadSessions();
            var files = SmbShares.ReadOpenFiles();
            return new Snapshot(SmbShares.ReadShares(), SmbShares.ServerRunning(), SmbShares.ReadNetworkSharing(), sessions.Items, sessions.Error, files.Items, files.Error);
        });

        ShowSharingState(s);

        Shares.ItemsSource = s.Shares;
        Shares.SelectedItem = s.Shares.FirstOrDefault(x => x.Name == selectedShare);
        int real = s.Shares.Count(x => !x.IsAdmin), admin = s.Shares.Count - real;
        ShareCount.Text = s.Shares.Count == 0 ? "" : $"{real} shared · {admin} admin";
        NoShares.Visibility = real == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoSharesText.Text = !s.ServerRunning ? "The Server service isn't running, so this PC isn't sharing anything."
            : s.Shares.Count == 0 ? "Tman couldn't read the share list from Windows, so it can't tell you more."
            : "Nothing here was shared by you or an app. The entries below are Windows' own admin shares, which exist on every PC and only administrators can use.";
        SharesEmpty.Text = "No shares to list.";
        SharesEmpty.Visibility = s.Shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Sessions.ItemsSource = s.Sessions;
        SessionCount.Text = s.SessionError != null ? "" : $"{s.Sessions.Count} connected";
        SessionsEmpty.Text = s.SessionError != null ? $"Can't list connected computers. {s.SessionError}" : "No other computer is connected to this PC's shares.";
        SessionsEmpty.Visibility = s.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Files.ItemsSource = s.Files;
        FileCount.Text = s.FileError != null ? "" : $"{s.Files.Count} open";
        FilesEmpty.Text = s.FileError != null ? $"Can't list open files. {s.FileError}" : "No files are open from other computers.";
        FilesEmpty.Visibility = s.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        Shares_SelectionChanged(this, null!);
        Sessions_SelectionChanged(this, null!);
    }

    void ShowSharingState(Snapshot s)
    {
        SharingText.Visibility = s.Sharing == null ? Visibility.Collapsed : Visibility.Visible;
        if (s.Sharing is not { } state) return;
        bool on = state.On && s.ServerRunning;
        SharingText.Text = $"File and printer sharing is {(on ? "on" : "off")} for the {state.Network} network you're using.";
        SharingText.Foreground = (System.Windows.Media.Brush)FindResource(on && state.Network.Contains("public") ? "WarnBrush" : "SubtleBrush");
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    SmbShare? SelectedShare => Shares.SelectedItem as SmbShare;
    SmbSession? SelectedSession => Sessions.SelectedItem as SmbSession;

    void Shares_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopButton.IsEnabled = !_busy && MainWindow.IsAdmin && SelectedShare != null;
        OpenButton.IsEnabled = SelectedShare is { Path.Length: > 0 };
    }

    void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CloseButton.IsEnabled = !_busy && MainWindow.IsAdmin && SelectedSession != null;

    void Open_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(SelectedShare?.Path);

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedShare is not { } s) return;
        var warning = s.IsAdmin
            ? "This is one of Windows' own admin shares. Remote management tools use it, and Windows puts it back the next time the Server service starts."
            : "Anyone using it over the network loses access, and any files they have open from it are closed. The folder and its files stay on this PC.";
        if (Ui.Confirm($"Stop sharing \"{s.Name}\"?\n\n{warning}"))
            Act($"stop sharing {s.Name}", () => SmbShares.StopSharing(s.Name));
    }

    void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { } s) return;
        var opens = s.OpenFiles > 0 ? $"It has {s.OpenFiles} open, and unsaved changes in them may be lost." : "It has no files open.";
        if (Ui.Confirm($"Close the session from {s.Client} ({s.User})?\n\n{opens} The computer can connect again unless the share is stopped."))
            Act($"close the session from {s.Client}", () => SmbShares.CloseSession(s.Client, s.User));
    }

    async void Act(string description, Func<string?> action)
    {
        if (_busy) return;
        _busy = true;
        Shares_SelectionChanged(this, null!);
        Sessions_SelectionChanged(this, null!);
        var error = await Task.Run(() =>
        {
            try { return action(); }
            catch (Exception ex) { return ex.Message; }
        });
        _busy = false;
        if (error != null) Ui.Error($"Couldn't {description}.\n\n{error}");
        Load();
    }
}
