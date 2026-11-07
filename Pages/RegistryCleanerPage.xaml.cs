using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Tman.Core;

namespace Tman.Pages;

public sealed class RegRow : ObservableObject
{
    readonly Action _changed;

    public RegRow(RegFinding finding, Action changed)
    {
        Finding = finding;
        _changed = changed;
    }

    public RegFinding Finding { get; }
    public string Name => Finding.Name;
    public string Reason => Finding.Reason;
    public string Location => Finding.Location;
    public string Group => Finding.Category.ToUpperInvariant();
    public bool CanRemove => !Finding.NeedsAdmin || MainWindow.IsAdmin;
    public string? LockReason => Finding.NeedsAdmin && !MainWindow.IsAdmin ? "Needs administrator rights. Use File > Restart as administrator." : null;

    public IEnumerable<string> Tags
    {
        get
        {
            yield return Finding.HiveLabel;
            if (Finding.NeedsAdmin) yield return "Admin";
        }
    }

    bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) _changed(); }
    }
}

/// <summary>The Registry cleaner page. It scans on demand, removes only what the user ticks, and always writes a .reg backup first.</summary>
public partial class RegistryCleanerPage : UserControl, IPage
{
    static readonly string BackupFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "reg-backups");

    List<RegRow> _rows = new();
    string? _lastBackup;
    bool _scanned, _busy;

    public RegistryCleanerPage() => InitializeComponent();

    public void OnShown()
    {
        if (!_scanned) _ = Scan();
    }

    IEnumerable<RegRow> Selected => _rows.Where(r => r.IsChecked && r.CanRemove);

    async Task Scan()
    {
        _scanned = true;
        SetBusy(true);
        CountText.Text = "Scanning…";
        EmptyText.Visibility = Visibility.Collapsed;
        var found = await Task.Run(RegistryClean.Scan);
        _rows = found.Select(f => new RegRow(f, UpdateCount)).ToList();
        var view = new CollectionViewSource { Source = _rows }.View;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RegRow.Group)));
        List.ItemsSource = view;
        EmptyText.Text = "No broken entries were found in the places Tman checks.";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetBusy(false);
        UpdateCount();
    }

    void UpdateCount()
    {
        int selected = Selected.Count();
        CountText.Text = _rows.Count == 0 ? "" : $"{_rows.Count} found · {selected} selected";
        RemoveButton.IsEnabled = selected > 0 && !_busy;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        List.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy && Selected.Any();
        RestoreButton.IsEnabled = !busy && _lastBackup != null && File.Exists(_lastBackup);
    }

    void Check_Click(object sender, RoutedEventArgs e) => UpdateCount();

    async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var chosen = Selected.ToList();
        if (chosen.Count == 0) return;

        bool admin = chosen.Any(r => r.Finding.NeedsAdmin);
        var footer = "Tman saves a .reg backup first. Use Restore last backup to put everything back."
            + (admin ? " Entries under HKLM need administrator rights." : "");
        var dialog = new ListConfirmDialog("Registry cleaner",
            $"Remove {(chosen.Count == 1 ? "this entry" : $"these {chosen.Count} entries")}?",
            chosen.Select(r => $"{r.Name}  ({r.Location})"), footer) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        SetBusy(true);
        StatusText.Visibility = Visibility.Collapsed;
        string? backup = null, backupError = null;
        List<string> errors = new();
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(BackupFolder);
                backup = Path.Combine(BackupFolder, $"reg-clean-{DateTime.Now:yyyy-MM-dd-HHmmss}.reg");
                RegistryClean.Backup(chosen.Select(r => r.Finding), backup);
            }
            catch (Exception ex)
            {
                backupError = ex.Message;
                backup = null;
            }
            if (backup != null) errors = RegistryClean.Delete(chosen.Select(r => r.Finding));
        });

        if (backup == null)
        {
            SetBusy(false);
            Ui.Error($"Tman didn't remove anything because it couldn't write the backup first:\n\n{backupError}");
            return;
        }
        _lastBackup = backup;
        await Scan();
        int removed = chosen.Count - errors.Count;
        StatusText.Text = $"Removed {removed} of {chosen.Count}. Backup saved to {backup}."
            + (errors.Count > 0 ? "\nSome couldn't be removed:\n" + string.Join("\n", errors) : "");
        StatusText.Visibility = Visibility.Visible;
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _lastBackup == null) return;
        if (!Ui.Confirm($"Put back everything from the last clean?\n\nThis imports {_lastBackup}.", "Registry cleaner")) return;
        SetBusy(true);
        StatusText.Visibility = Visibility.Collapsed;
        var error = await Task.Run(() => RegistryClean.Restore(_lastBackup));
        await Scan();
        StatusText.Text = error == null ? "Put back the entries from the last backup." : $"Couldn't restore the backup:\n\n{error}";
        StatusText.Visibility = Visibility.Visible;
    }
}
