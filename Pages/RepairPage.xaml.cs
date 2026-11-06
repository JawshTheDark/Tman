using System.Windows;
using System.Windows.Controls;
using Tman.Core;

namespace Tman.Pages;

public sealed class RepairRow : ObservableObject
{
    public required RepairJob Job { get; init; }

    List<string> _drives = new();
    public List<string> Drives { get => _drives; set => Set(ref _drives, value); }

    string? _drive;
    public string? Drive { get => _drive; set { if (Set(ref _drive, value)) Raise(nameof(CanRun)); } }

    bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(CanRun)); } }

    string _label = "Run";
    public string Label { get => _label; set => Set(ref _label, value); }

    public bool CanRun => MainWindow.IsAdmin && !_busy && (!Job.NeedsDrive || _drive != null);
}

public partial class RepairPage : UserControl, IPage
{
    readonly List<RepairRow> _rows = RepairJobs.All.Select(j => new RepairRow { Job = j }).ToList();
    CancellationTokenSource? _stoppable;
    bool _running;

    public RepairPage()
    {
        InitializeComponent();
        Jobs.ItemsSource = _rows;
        Panel.ShowIdle("Run a job and its output appears here. One job runs at a time.");
        // A job that's safe to stop goes down with Tman. The others are left to finish, and nothing here waits for them.
        Application.Current.Exit += (_, _) => _stoppable?.Cancel();
        App.Terminating += () => _stoppable?.Cancel();
        LoadDrives();
    }

    async void LoadDrives()
    {
        var drives = await Task.Run(() =>
        {
            var found = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.DriveFormat == "NTFS") found.Add(d.Name.TrimEnd('\\'));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return found;
        });
        foreach (var row in _rows.Where(r => r.Job.NeedsDrive))
        {
            row.Drives = drives;
            row.Drive = drives.FirstOrDefault(d => Environment.SystemDirectory.StartsWith(d, StringComparison.OrdinalIgnoreCase)) ?? drives.FirstOrDefault();
        }
    }

    void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running || ((Button)sender).Tag is not RepairRow row) return;
        var job = row.Job;
        var title = job.NeedsDrive ? $"{job.Title} ({row.Drive})" : job.Title;
        var after = job.CanStop ? "You can stop it at any time." : "It can't be stopped safely once it starts, so keep Tman open until it finishes.";
        if (Ui.Confirm($"{title}?\n\nHow long it takes: {job.Takes}.\n\nWhat it changes: {job.Changes}\n\n{after}"))
            Run(row, title);
    }

    async void Run(RepairRow row, string title)
    {
        _running = true;
        foreach (var r in _rows) r.Busy = true;
        row.Label = "Running…";
        var stop = row.Job.CanStop ? new CancellationTokenSource() : null;
        _stoppable = stop;
        Panel.Start(title, stop == null ? null : stop.Cancel);
        JobResult result;
        try
        {
            var drive = row.Drive ?? "";
            result = await Task.Run(() => row.Job.Run(drive, Panel.Line, stop?.Token ?? CancellationToken.None));
        }
        catch (Exception ex)
        {
            result = new JobResult(JobLevel.Bad, $"The job failed: {ex.Message}");
        }
        _stoppable = null;
        stop?.Dispose();
        Panel.Finish(result);
        row.Label = "Run";
        foreach (var r in _rows) r.Busy = false;
        _running = false;
    }
}
