using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tman.Core;

namespace Tman.Pages;

public partial class HostsPage : UserControl, IPage
{
    const string FlagNote = "Microsoft Defender may also flag hosts edits for popular domains, and it can remove them.";

    readonly string _path, _backups;
    HostsFile? _file;
    bool _loaded, _busy;

    public HostsPage() : this(HostsFile.SystemPath, HostsFile.BackupFolder) { }

    public HostsPage(string path, string backups)
    {
        _path = path;
        _backups = backups;
        InitializeComponent();
        Ui.SetupGrid(List);
        RawBox.IsReadOnly = !CanWrite;
        Tabs.SelectionChanged += Tabs_SelectionChanged;
    }

    bool CanWrite => MainWindow.IsAdmin || _path != HostsFile.SystemPath;

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    async void Load(string? status = null)
    {
        StatusText.Text = "Reading the hosts file…";
        try
        {
            _file = await Task.Run(() => HostsFile.Load(_path));
            ShowFile(status);
        }
        catch (Exception ex)
        {
            _file = null;
            ShowFile(null);
            StatusText.Text = $"Can't read the hosts file: {ex.Message}";
        }
    }

    void ShowFile(string? status)
    {
        var entries = _file?.Entries.ToList() ?? new List<HostsLine>();
        List.ItemsSource = entries;
        if (Tabs.SelectedItem == RawTab) RawBox.Text = _file?.ToText() ?? "";
        EmptyText.Text = "There are no entries in this file, so Windows uses DNS for every name.";
        EmptyText.Visibility = _file != null && entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowWarnings(entries);
        ShowStatus(status);
        UpdateActions();
    }

    void ShowWarnings(List<HostsLine> entries)
    {
        var flagged = entries.Where(e => e.Enabled && e.Warning.Length > 0).ToList();
        int redirects = flagged.Count(e => e.Warning.StartsWith("Sends"));
        var parts = new List<string>();
        if (redirects > 0)
            parts.Add($"{redirects} {(redirects == 1 ? "entry sends a well-known site" : "entries send well-known sites")} to another address. Malware does this to reach fake sites, so check that you added {(redirects == 1 ? "it" : "them")} yourself.");
        if (flagged.Count > redirects)
            parts.Add($"{flagged.Count - redirects} {(flagged.Count - redirects == 1 ? "entry blocks a well-known site" : "entries block well-known sites")}.");
        WarnText.Text = string.Join(" ", parts) + (parts.Count > 0 ? " " + FlagNote : "");
        WarnBox.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowStatus(string? message)
    {
        if (_file == null) return;
        int entries = _file.Entries.Count(), on = _file.Entries.Count(e => e.Enabled), invalid = _file.Problems().Count;
        var parts = new List<string> { $"{entries} {(entries == 1 ? "entry" : "entries")}, {on} enabled" };
        if (invalid > 0) parts.Add($"{invalid} {(invalid == 1 ? "line isn't" : "lines aren't")} valid and Windows ignores {(invalid == 1 ? "it" : "them")}: see Raw text");
        parts.Add($"{_file.EncodingName}, {_file.EolName}");
        if (_file.Dirty) parts.Add("Unsaved changes");
        StatusText.Text = (message != null ? message + " " : "") + string.Join(" · ", parts);
    }

    List<HostsLine> Selected => List.SelectedItems.Cast<HostsLine>().ToList();

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    void UpdateActions()
    {
        var rows = Selected;
        bool entries = Tabs.SelectedItem != RawTab, ready = CanWrite && !_busy && _file != null;
        AddButton.IsEnabled = ready && entries;
        EditButton.IsEnabled = ready && entries && rows.Count == 1;
        RemoveButton.IsEnabled = ready && entries && rows.Count > 0;
        EnableButton.IsEnabled = ready && entries && rows.Any(r => !r.Enabled);
        DisableButton.IsEnabled = ready && entries && rows.Any(r => r.Enabled);
        SaveButton.IsEnabled = ready;
        ReloadButton.IsEnabled = !_busy;
        RestoreButton.IsEnabled = ready;
    }

    // The raw tab and the rows are two views of one file: leaving the raw tab applies what was typed there.
    void ApplyRaw()
    {
        if (_file != null && Tabs.SelectedItem == RawTab && CanWrite && RawBox.Text != _file.ToText()) _file.SetText(RawBox.Text);
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || _file == null) return;
        if (e.RemovedItems.Contains(RawTab) && CanWrite && RawBox.Text != _file.ToText()) _file.SetText(RawBox.Text);
        ShowFile(null);
    }

    Window? Owner => Window.GetWindow(this);

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_file == null) return;
        var dialog = new HostsEntryDialog(_file, null) { Owner = Owner };
        if (dialog.ShowDialog() != true) return;
        _file.Add(dialog.Ip, dialog.Hosts, dialog.Comment);
        ShowFile(null);
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_file == null || !CanWrite || Selected.FirstOrDefault() is not { } line) return;
        var dialog = new HostsEntryDialog(_file, line) { Owner = Owner };
        if (dialog.ShowDialog() != true) return;
        _file.Update(line, dialog.Ip, dialog.Hosts, dialog.Comment);
        ShowFile(null);
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_file == null) return;
        foreach (var line in Selected) _file.Remove(line);
        ShowFile(null);
    }

    void Enable_Click(object sender, RoutedEventArgs e) => SetEnabled(true);
    void Disable_Click(object sender, RoutedEventArgs e) => SetEnabled(false);

    void SetEnabled(bool enabled)
    {
        if (_file == null) return;
        var lines = Selected;
        foreach (var line in lines) _file.SetEnabled(line, enabled);
        ShowFile(null);
        foreach (var line in lines) List.SelectedItems.Add(line);
    }

    static string Lines(List<string> lines) =>
        string.Join("\n", lines.Take(10).Select(l => "  " + l.Trim())) + (lines.Count > 10 ? $"\n  and {lines.Count - 10} more" : "");

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_file is not { } file) return;
        ApplyRaw();
        if (!file.Dirty)
        {
            ShowFile("Nothing to save.");
            return;
        }
        var (removed, added) = file.Changes();
        var message = "Save these changes to the hosts file?\n";
        if (removed.Count > 0) message += $"\nRemoved or changed:\n{Lines(removed)}\n";
        if (added.Count > 0) message += $"\nNew or changed:\n{Lines(added)}\n";
        var problems = file.Problems();
        if (problems.Count > 0) message += $"\nWindows will ignore these lines because they aren't valid:\n{Lines(problems)}\n";
        var warnings = file.Entries.Where(l => l.Enabled && l.Warning.Length > 0 && added.Contains(l.Text)).Select(l => l.Warning).ToList();
        if (warnings.Count > 0) message += $"\nCheck these before saving, because malware uses the same trick:\n{Lines(warnings)}\n{FlagNote}\n";
        message += $"\nA dated copy goes to {_backups} first, and the DNS cache is flushed afterwards. A wrong entry can send a site to the wrong place or stop it loading.";
        if (!Ui.Confirm(message, "Save hosts file")) return;

        _busy = true;
        UpdateActions();
        StatusText.Text = "Saving…";
        string? error = null, backup = null, flush = null;
        await Task.Run(() =>
        {
            try
            {
                backup = file.Save(_path, _backups);
                if (_path == HostsFile.SystemPath) flush = NetworkConfig.FlushDns();
            }
            catch (Exception ex)
            {
                error = WriteError(ex);
            }
        });
        _busy = false;
        if (error != null)
        {
            ShowFile(null);
            Ui.Error($"Couldn't save the hosts file:\n\n{error}");
            return;
        }
        var saved = "Saved." + (backup != null ? $" The old file is at {backup}." : "");
        ShowFile(flush == null ? saved : saved + " The DNS cache couldn't be flushed, so old answers may last a few minutes.");
    }

    string WriteError(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            bool readOnly = File.Exists(_path) && File.GetAttributes(_path).HasFlag(FileAttributes.ReadOnly);
            if (readOnly) return "The file is marked read-only. Clear that in its Properties, then save again.";
            return "Windows refused the write. Saving needs administrator rights, and security software can also protect this file.";
        }
        return ex.Message + (ex.Message.Contains("virus", StringComparison.OrdinalIgnoreCase) ? $"\n\n{FlagNote}" : "");
    }

    void Reload_Click(object sender, RoutedEventArgs e)
    {
        ApplyRaw();
        if (_file is { Dirty: true } && !Ui.Confirm("Throw away your unsaved changes and read the file again?")) return;
        Load();
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        List<HostsFile.Backup> backups;
        try
        {
            backups = await Task.Run(() => HostsFile.ListBackups(_backups));
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't read the backups in {_backups}:\n\n{ex.Message}");
            return;
        }
        var dialog = new HostsRestoreDialog(backups, _backups) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Chosen is not { } chosen) return;
        ApplyRaw();
        var unsaved = _file is { Dirty: true } ? " Your unsaved changes will be lost." : "";
        if (!Ui.Confirm($"Replace the hosts file with the copy from {chosen.TimeText}?\n\nIt has {chosen.Entries} entries. The current file is copied to {_backups} first, and the DNS cache is flushed afterwards.{unsaved}", "Restore backup")) return;

        _busy = true;
        UpdateActions();
        string? error = null, saved = null;
        await Task.Run(() =>
        {
            try
            {
                saved = HostsFile.Restore(chosen.Path, _path, _backups);
                if (_path == HostsFile.SystemPath) NetworkConfig.FlushDns();
            }
            catch (Exception ex)
            {
                error = WriteError(ex);
            }
        });
        _busy = false;
        if (error != null) Ui.Error($"Couldn't restore the backup:\n\n{error}");
        Load(error == null ? $"Restored the copy from {chosen.TimeText}." + (saved != null ? $" The file it replaced is at {saved}." : "") : null);
    }
}

/// <summary>Adds or edits one entry.</summary>
public sealed class HostsEntryDialog : Window
{
    readonly HostsFile _file;
    readonly TextBox _ip = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBox _hosts = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBox _comment = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    public string Ip => _ip.Text.Trim();
    public string Hosts => string.Join(' ', _hosts.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public string Comment => _comment.Text.Trim();

    public HostsEntryDialog(HostsFile file, HostsLine? line)
    {
        _file = file;
        Title = line == null ? "Add entry" : "Edit entry";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);
        _error.Foreground = (System.Windows.Media.Brush)FindResource("BadBrush");

        if (line != null)
        {
            _ip.Text = line.Ip;
            _hosts.Text = line.Hosts;
            _comment.Text = line.Comment;
        }

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "IP address" });
        root.Children.Add(_ip);
        root.Children.Add(new TextBlock { Text = "Host name (separate several with spaces)" });
        root.Children.Add(_hosts);
        root.Children.Add(new TextBlock { Text = "Comment (optional)" });
        root.Children.Add(_comment);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => Keyboard.Focus(_ip);
    }

    void Accept()
    {
        var problem = _file.Validate(Ip, Hosts, Comment);
        if (problem != null)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
            return;
        }
        var warning = HostsFile.WarningFor(Ip, Hosts);
        if (warning != null)
        {
            var why = warning.StartsWith("Sends")
                ? $"{warning}. Malware does this to send people to fake sites, so only keep this if you meant to."
                : $"{warning}, so it won't load on this PC.";
            var message = $"{why}\n\nMicrosoft Defender may flag hosts edits for popular domains, and it can remove them.\n\nKeep this entry?";
            if (MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
    }
}

/// <summary>Picks one of the dated copies Tman made.</summary>
public sealed class HostsRestoreDialog : Window
{
    readonly DataGrid _list = new() { Height = 220, SelectionMode = DataGridSelectionMode.Single, IsReadOnly = true };

    public HostsFile.Backup? Chosen => _list.SelectedItem as HostsFile.Backup;

    public HostsRestoreDialog(List<HostsFile.Backup> backups, string folder)
    {
        Title = "Restore backup";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Ui.Dress(this);

        Ui.SetupGrid(_list);
        _list.Columns.Add(new DataGridTextColumn { Header = "Saved", Binding = new System.Windows.Data.Binding("TimeText"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _list.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new System.Windows.Data.Binding("SizeText"), Width = 80 });
        _list.Columns.Add(new DataGridTextColumn { Header = "Entries", Binding = new System.Windows.Data.Binding("Entries"), Width = 80 });
        _list.ItemsSource = backups;
        _list.SelectedIndex = backups.Count > 0 ? 0 : -1;

        var ok = new Button { Content = "Restore", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = backups.Count > 0 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            if (Chosen != null) DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = backups.Count > 0 ? "Choose a copy to put back. Tman makes one each time it saves the hosts file." : $"There are no backups yet. Tman makes one in {folder} each time it saves the hosts file.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(_list);
        root.Children.Add(buttons);
        Content = root;
    }
}
