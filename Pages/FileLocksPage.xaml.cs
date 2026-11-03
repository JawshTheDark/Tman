using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public sealed record LockRow(int Pid, string Process, ImageSource? Icon, string Kind, string Path);

/// <summary>Search every process's open files (and optionally loaded DLLs) for a path.</summary>
public partial class FileLocksPage : UserControl, IPage
{
    CancellationTokenSource? _cts;

    public FileLocksPage()
    {
        InitializeComponent();
        Ui.SetupGrid(Results);
        AdminNote.Visibility = MainWindow.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        Query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _ = SearchAsync(Query.Text);
            e.Handled = true;
        };
        StatusText.Text = "Results show the processes holding a match open right now.";
    }

    public void FocusSearch() => Query.FocusBox();

    void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(Query.Text);

    void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Which file is in use?", CheckFileExists = false };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Query.Text = dlg.FileName;
        _ = SearchAsync(dlg.FileName);
    }

    void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Which folder is in use?" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Query.Text = dlg.FolderName;
        _ = SearchAsync(dlg.FolderName);
    }

    internal async Task SearchAsync(string text)
    {
        var q = text.Trim().Trim('"');
        if (q.Length < 2)
        {
            StatusText.Text = "Type at least two characters of a path or file name.";
            return;
        }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        SearchButton.IsEnabled = false;
        CancelButton.Visibility = Busy.Visibility = Visibility.Visible;
        StatusText.Text = $"Searching for \"{q}\"…";
        var procs = (SystemMonitor.Instance.Latest?.Processes ?? new List<ProcSample>())
            .GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        bool dlls = IncludeDlls.IsChecked == true;
        try
        {
            var scan = await Task.Run(() => Handles.Scan(p => p.Contains(q, StringComparison.OrdinalIgnoreCase), dlls, null, cts.Token));
            if (cts != _cts) return;
            var rows = scan.Results
                .Select(r =>
                {
                    procs.TryGetValue(r.Pid, out var p);
                    var kind = r.Kind switch { OpenFileKind.Folder => "Folder", OpenFileKind.Module => "DLL", _ => "File" };
                    return new LockRow(r.Pid, p?.Name ?? $"PID {r.Pid}", p?.Details.Icon, kind, r.Path);
                })
                .OrderBy(r => r.Process, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Results.ItemsSource = rows;
            int processes = rows.Select(r => r.Pid).Distinct().Count();
            StatusText.Text = (rows.Count == 0 ? $"Nothing has \"{q}\" open." : $"{rows.Count} open {(rows.Count == 1 ? "item" : "items")} in {processes} {(processes == 1 ? "process" : "processes")}.")
                + $"   Checked {Fmt.Num(scan.Handles)} file handles across {scan.Processes} processes in {scan.Elapsed.TotalSeconds:0.0} s"
                + (scan.Denied > 0 ? $"; {scan.Denied} processes couldn't be inspected." : ".");
        }
        catch (OperationCanceledException)
        {
            if (cts == _cts) StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (cts == _cts)
            {
                SearchButton.IsEnabled = true;
                CancelButton.Visibility = Busy.Visibility = Visibility.Collapsed;
            }
        }
    }

    void Admin_Click(object sender, RoutedEventArgs e) => MainWindow.RestartAsAdmin();

    LockRow? Selected => Results.SelectedItem as LockRow;

    void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var warning = ProcessActions.IsCritical(row.Process)
            ? $"{row.Process} is a critical Windows process. Ending it can crash Windows. End it anyway?"
            : $"End {row.Process} (PID {row.Pid})? Unsaved work in it will be lost.";
        if (!Ui.Confirm(warning, "End process")) return;
        if (!ProcessActions.Terminate(row.Pid, out var error))
            Ui.Error($"Couldn't end {row.Process}: {error}");
        else
            Results.ItemsSource = ((IEnumerable<LockRow>)Results.ItemsSource).Where(r => r.Pid != row.Pid).ToList();
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(Selected?.Path);

    void CopyPath_Click(object sender, RoutedEventArgs e) =>
        Ui.Copy(string.Join(Environment.NewLine, Results.SelectedItems.Cast<LockRow>().Select(r => r.Path)));
}
