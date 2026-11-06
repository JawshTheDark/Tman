using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Pages;

public sealed class CleanupRow : ObservableObject
{
    public required CleanupCategory Category { get; init; }

    bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    string _sizeText = "Scanning…";
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    bool _canClean;
    public bool CanClean { get => _canClean; set => Set(ref _canClean, value); }

    public long Bytes { get; set; }
}

/// <summary>The Cleanup section of Disk space. It scans the first time it's shown, and deletes only from the engine's fixed locations.</summary>
public partial class CleanupPanel : UserControl
{
    readonly CleanupEngine _engine = CleanupEngine.ForThisPc();
    List<CleanupRow> _rows = new();
    bool _scanned, _busy;

    /// <summary>Raised after files were removed, so the page can update its drive figures.</summary>
    public event Action? Cleaned;

    public CleanupPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && !_scanned) _ = Scan();
        };
    }

    async Task Scan()
    {
        _scanned = true;
        SetBusy(true);
        if (_rows.Count == 0) Rows.ItemsSource = _rows = _engine.Categories.Select(c => new CleanupRow { Category = c }).ToList();
        foreach (var row in _rows) row.SizeText = "Scanning…";
        TotalText.Text = "Scanning…";
        foreach (var row in _rows)
        {
            var size = await Task.Run(() =>
            {
                try { return _engine.Measure(row.Category, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new CleanupSize(0, 0, true); }
            });
            bool locked = row.Category.NeedsAdmin && !MainWindow.IsAdmin;
            row.Bytes = size.Bytes;
            row.SizeText = locked && size.Bytes == 0 ? "Needs admin" : (size.Denied ? "At least " : "") + Fmt.Bytes(size.Bytes);
            row.CanClean = !locked && size.Bytes > 0;
            if (!row.CanClean) row.IsChecked = false;
        }
        SetBusy(false);
        ShowTotal();
    }

    void Check_Click(object sender, RoutedEventArgs e) => ShowTotal();

    IEnumerable<CleanupRow> Selected => _rows.Where(r => r.IsChecked && r.CanClean);

    void ShowTotal()
    {
        var total = Selected.Sum(r => r.Bytes);
        TotalText.Text = Selected.Any() ? $"Selected: {Fmt.Bytes(total)}" : $"Found {Fmt.Bytes(_rows.Sum(r => r.Bytes))}. Tick what to clean.";
        CleanButton.IsEnabled = !_busy && Selected.Any();
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy && Selected.Any();
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ResultText.Visibility = Visibility.Collapsed;
        await Scan();
    }

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected.ToList();
        var list = string.Join("\n", rows.Select(r => $"•  {r.Category.Title}: {r.SizeText}"));
        var service = rows.Any(r => r.Category.Kind == CleanupKind.UpdateCache) ? " The Windows Update service stops while its cache is cleaned." : "";
        if (Ui.Confirm($"Permanently delete about {Fmt.Bytes(rows.Sum(r => r.Bytes))}?\n\n{list}\n\nThese files don't go to the Recycle Bin and can't be recovered. Files in use are skipped.{service}"))
            await Clean(rows);
    }

    async Task Clean(List<CleanupRow> rows)
    {
        SetBusy(true);
        ResultText.Visibility = Visibility.Collapsed;
        long freed = 0, skipped = 0, skippedFiles = 0;
        var notes = new StringBuilder();
        foreach (var row in rows)
        {
            TotalText.Text = $"Cleaning {row.Category.Title}…";
            var outcome = await Task.Run(() =>
            {
                try { return _engine.Clean(row.Category, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new CleanupOutcome(0, row.Bytes, 0, $"{row.Category.Title} couldn't be cleaned: {ex.Message}");
                }
            });
            freed += outcome.Freed;
            skipped += outcome.Skipped;
            skippedFiles += outcome.SkippedFiles;
            if (outcome.Note != null) notes.Append(' ').Append(outcome.Note);
        }
        var result = $"Freed {Fmt.Bytes(freed)}.";
        if (skippedFiles > 0) result += $" Skipped {Fmt.Bytes(skipped)} in {(skippedFiles == 1 ? "1 file that was" : $"{Fmt.Num(skippedFiles)} files that were")} in use or protected.";
        else if (skipped > 0) result += $" Skipped {Fmt.Bytes(skipped)}.";
        ResultText.Text = result + notes;
        ResultText.Foreground = (Brush)FindResource(notes.Length > 0 ? "WarnBrush" : "GoodBrush");
        ResultText.Visibility = Visibility.Visible;
        foreach (var row in rows) row.IsChecked = false;
        await Scan();
        Cleaned?.Invoke();
    }
}
