using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public sealed class IsoPick : ObservableObject
{
    public required IsoRemovable Item { get; init; }
    public required Action Changed { get; init; }

    bool _ticked;
    public bool Ticked
    {
        get => _ticked;
        set { if (Set(ref _ticked, value)) Changed(); }
    }
}

public sealed record IsoWorkDrive(char Letter, long Free)
{
    public override string ToString() => $"{Letter}: ({Fmt.Bytes(Free)} free)";
}

public partial class IsoBuilderPage : UserControl, IPage
{
    readonly List<IsoPick> _apps, _extras;
    IsoMedia? _media;
    CancellationTokenSource? _stop;
    bool _running, _reading;

    public IsoBuilderPage()
    {
        InitializeComponent();
        _apps = IsoBuilder.Apps.Select(MakePick).ToList();
        _extras = IsoBuilder.Extras.Select(MakePick).ToList();
        AppList.ItemsSource = _apps;
        ExtraList.ItemsSource = _extras;
        SelectSuggested();
        ToolText.Text = IsoBuilder.FindOscdimg() is { } tool
            ? $"The ISO is made with oscdimg from {Path.GetDirectoryName(tool)}."
            : "The ISO is made by Windows' built-in image writer, which works without the Windows ADK.";
        // Stopping here is all Tman can do on the way out. The build's watchdog sees it go, and finishes the cleanup without holding up the exit.
        Application.Current.Exit += (_, _) => _stop?.Cancel();
        App.Terminating += () => _stop?.Cancel();
        LoadWorkDrives();
        LoadUsbDisks();
        CheckLeftover();
        Refresh();
    }

    IsoPick MakePick(IsoRemovable item) => new() { Item = item, Changed = Refresh };

    public void OnShown()
    {
        if (!_running) CheckLeftover();
    }

    IEnumerable<IsoPick> AllPicks => _apps.Concat(_extras);

    void SelectSuggested()
    {
        foreach (var pick in AllPicks) pick.Ticked = pick.Item.Suggested;
    }

    async void LoadWorkDrives()
    {
        var drives = await Task.Run(() =>
        {
            var found = new List<IsoWorkDrive>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (IsoBuilder.CanWorkOn(d)) found.Add(new IsoWorkDrive(d.Name[0], d.AvailableFreeSpace));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return found.OrderByDescending(d => d.Free).ToList();
        });
        WorkDrives.ItemsSource = drives;
        WorkDrives.SelectedItem = drives.FirstOrDefault();
    }

    async void LoadUsbDisks()
    {
        RefreshUsbButton.IsEnabled = false;
        UsbStatus.Text = "Looking for USB drives…";
        var previous = (UsbDisks.SelectedItem as UsbDisk)?.Id;
        var disks = await Task.Run(() => IsoBuilder.ListDisks().Where(d => d.IsUsb).ToList());
        UsbDisks.ItemsSource = disks;
        UsbDisks.SelectedItem = disks.FirstOrDefault(d => d.Id == previous) ?? disks.FirstOrDefault();
        UsbStatus.Text = disks.Count == 0
            ? "No USB drives found. Plug one in, then choose Refresh."
            : "Only USB drives are listed. Writing erases the whole drive.";
        RefreshUsbButton.IsEnabled = !_running;
        Refresh();
    }

    void CheckLeftover()
    {
        var work = IsoBuilder.Leftover();
        LeftoverBar.Visibility = work != null ? Visibility.Visible : Visibility.Collapsed;
        LeftoverButton.IsEnabled = MainWindow.IsAdmin && !_running;
        if (work != null) LeftoverText.Text = MainWindow.IsAdmin
            ? $"An earlier build was interrupted and may have left an image mounted in {work}."
            : $"An earlier build was interrupted and may have left an image mounted in {work}. Restart as administrator to clean it up.";
    }

    async void CleanUp_Click(object sender, RoutedEventArgs e)
    {
        StartJob("Cleaning up an earlier build", null);
        bool clean = await Task.Run(() => IsoBuilder.RecoverAsync(Panel.Line));
        FinishJob(clean
            ? new JobResult(JobLevel.Good, "Cleaned up. Nothing from the earlier build is left.")
            : new JobResult(JobLevel.Bad, "The image is still mounted. Restart Windows, then try again."));
        CheckLeftover();
    }

    // ---- Choosing things ----

    void BrowseIso_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a Windows 11 ISO", Filter = "Disc images (*.iso)|*.iso" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = LoadIsoAsync(dialog.FileName);
    }

    /// <summary>Reads the ISO's editions. Also the dev route in, through <c>--snap &lt;dir&gt; isobuilder &lt;iso&gt;</c>.</summary>
    public async Task LoadIsoAsync(string path)
    {
        _media = null;
        _reading = true;
        IsoBox.Text = path;
        IsoStatus.Text = "Reading the ISO…";
        Editions.ItemsSource = null;
        EditionRow.Visibility = Visibility.Collapsed;
        Refresh();
        try
        {
            var media = await Task.Run(() => IsoBuilder.ReadMedia(path));
            _media = media;
            var first = media.Editions[0];
            IsoStatus.Text = $"Windows 11 build {first.Build}, {first.Arch}, {first.Language}. {Fmt.Bytes(media.IsoBytes)}, {Count(media.Editions.Count, "edition")}.";
            Editions.ItemsSource = media.Editions;
            Editions.SelectedItem = media.Editions.FirstOrDefault(x => x.EditionId == "Professional") ?? first;
            EditionRow.Visibility = Visibility.Visible;
            if (OutputBox.Text.Length == 0)
                OutputBox.Text = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "-trimmed.iso");
        }
        catch (Exception ex) when (ex is IsoProblem or IOException or UnauthorizedAccessException)
        {
            IsoStatus.Text = ex.Message;
        }
        _reading = false;
        Refresh();
    }

    static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the new ISO as",
            Filter = "Disc images (*.iso)|*.iso",
            FileName = OutputBox.Text.Length > 0 ? Path.GetFileName(OutputBox.Text) : "Windows11-trimmed.iso",
        };
        if (OutputBox.Text.Length > 0) dialog.InitialDirectory = Path.GetDirectoryName(OutputBox.Text);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        OutputBox.Text = dialog.FileName;
        Refresh();
    }

    void BrowseUsbIso_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the ISO to write", Filter = "Disc images (*.iso)|*.iso" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        UsbIsoBox.Text = dialog.FileName;
        Refresh();
    }

    void Option_Changed(object sender, RoutedEventArgs e) => Refresh();

    void Account_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    void Suggested_Click(object sender, RoutedEventArgs e) => SelectSuggested();

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pick in AllPicks) pick.Ticked = false;
    }

    void RefreshUsb_Click(object sender, RoutedEventArgs e) => LoadUsbDisks();

    string? Account => AnswerBox.IsChecked == true ? AccountBox.Text.Trim() : null;

    IsoPlan? Plan() =>
        _media != null && Editions.SelectedItem is IsoEdition edition && WorkDrives.SelectedItem is IsoWorkDrive drive && OutputBox.Text.Length > 0
            ? new IsoPlan(_media.Path, OutputBox.Text, drive.Letter, edition, AllPicks.Where(p => p.Ticked).Select(p => p.Item).ToList(), CleanBox.IsChecked == true, Account)
            : null;

    /// <summary>Everything on the page that depends on what's chosen. Called from the pickers' events, so it can run before the page has finished loading.</summary>
    void Refresh()
    {
        if (!IsInitialized || RemoveCount == null) return;
        var ticked = AllPicks.Count(p => p.Ticked);
        RemoveCount.Text = $"{ticked} of {AllPicks.Count()} ticked";
        AccountRow.IsEnabled = AnswerBox.IsChecked == true && !_running;
        var plan = Plan();
        string? problem = null;
        if (plan != null && _media != null)
        {
            try { problem = IsoBuilder.CheckPlan(plan, _media); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { problem = ex.Message; }
            SpaceText.Text = $"Needs about {Fmt.Bytes(IsoBuilder.WorkBytes(_media, plan.Edition))} while it builds.";
        }
        else SpaceText.Text = "";
        BuildStatus.Foreground = (System.Windows.Media.Brush)FindResource(problem != null ? "WarnBrush" : "SubtleBrush");
        BuildStatus.Text = _running ? ""
            : !MainWindow.IsAdmin ? "Needs administrator rights."
            : _reading ? "Reading the ISO…"
            : _media == null ? "Choose an ISO to start."
            : problem ?? "Takes 20 minutes to an hour or more. You can stop at any time.";
        BuildButton.IsEnabled = MainWindow.IsAdmin && !_running && !_reading && plan != null && problem == null;
        WriteButton.IsEnabled = MainWindow.IsAdmin && !_running && UsbDisks.SelectedItem is UsbDisk && File.Exists(UsbIsoBox.Text);
        BrowseIsoButton.IsEnabled = BrowseOutputButton.IsEnabled = BrowseUsbIsoButton.IsEnabled = RefreshUsbButton.IsEnabled = !_running;
        AppList.IsEnabled = ExtraList.IsEnabled = ClearButton.IsEnabled = SuggestedButton.IsEnabled = !_running;
        AnswerBox.IsEnabled = CleanBox.IsEnabled = Editions.IsEnabled = WorkDrives.IsEnabled = UsbDisks.IsEnabled = !_running;
    }

    // ---- Running ----

    void StartJob(string title, CancellationTokenSource? stop)
    {
        _running = true;
        _stop = stop;
        Panel.Visibility = Visibility.Visible;
        PanelRow.Height = new GridLength(1, GridUnitType.Star);
        Panel.Start(title, stop == null ? null : stop.Cancel);
        LeftoverButton.IsEnabled = false;
        Refresh();
    }

    void FinishJob(JobResult result)
    {
        _stop?.Dispose();
        _stop = null;
        _running = false;
        Panel.Finish(result);
        Refresh();
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_running || Plan() is not { } plan || _media is not { } media) return;
        var account = plan.LocalAccount == null ? "" : $"\n\nSetup will skip the Microsoft account step and create a local administrator called {plan.LocalAccount} with no password.";
        var tool = IsoBuilder.FindOscdimg() == null ? "Windows' built-in image writer" : "oscdimg";
        if (!Ui.Confirm($"Build a trimmed ISO?\n\nEdition: {plan.Edition.Name}\nRemoving: {Count(plan.Remove.Count, "item")}\nSaving to: {plan.OutputIso}{account}\n\nIt uses up to {Fmt.Bytes(IsoBuilder.WorkBytes(media, plan.Edition))} on {plan.WorkDrive}: while it runs, makes the ISO with {tool}, and takes 20 minutes to an hour or more. You can stop at any time, and everything temporary is removed.")) return;
        var stop = new CancellationTokenSource();
        StartJob($"Building {plan.Edition.Name}", stop);
        JobResult result;
        try
        {
            result = await Task.Run(() => IsoBuilder.BuildAsync(plan, media, Panel.Line, stop.Token));
        }
        catch (Exception ex)
        {
            result = new JobResult(JobLevel.Bad, $"The build failed: {ex.Message}");
        }
        if (result.Level != JobLevel.Bad && File.Exists(plan.OutputIso)) UsbIsoBox.Text = plan.OutputIso;
        FinishJob(result);
        LoadWorkDrives();
        CheckLeftover();
    }

    async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (_running || UsbDisks.SelectedItem is not UsbDisk disk) return;
        var iso = UsbIsoBox.Text;
        if (IsoBuilder.UsbRefusal(disk, Environment.SystemDirectory[0], new[] { iso[0] }) is { } refused)
        {
            Ui.Error(refused);
            return;
        }
        WriteButton.IsEnabled = false;
        var contents = await Task.Run(() => IsoBuilder.DescribeContents(disk));
        Refresh();
        if (!Ui.Confirm($"Erase {disk.Title}?\n\nOn it now: {contents}\n\nEverything on this drive will be erased, including anything Windows doesn't show, and it can't be undone. It will then hold {Path.GetFileName(iso)}.\n\nCheck that this is the right drive before you continue.", "Tman - erase USB drive")) return;
        var stop = new CancellationTokenSource();
        StartJob($"Writing to {disk.Title}", stop);
        JobResult result;
        try
        {
            result = await Task.Run(() => IsoBuilder.WriteUsbAsync(disk, iso, Panel.Line, stop.Token));
        }
        catch (Exception ex)
        {
            result = new JobResult(JobLevel.Bad, $"Writing the drive failed: {ex.Message}");
        }
        FinishJob(result);
        LoadUsbDisks();
    }
}
