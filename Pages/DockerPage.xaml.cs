using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

/// <summary>An app's picture: a lettered tile straight away, replaced by the real icon if one arrives.</summary>
public sealed class AppIcon : ObservableObject
{
    static readonly Brush[] Tints = new[] { "#3B6EA8", "#7A5AA6", "#A65A7B", "#B0683C", "#7E8A3A", "#3C8F7A", "#4A7F4A", "#5A6478" }.Select(Solid).ToArray();
    static readonly Brush LightTile = Solid("#E9ECF1"), DarkTile = Solid("#272C35");

    static Brush Solid(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    readonly IReadOnlyList<string> _logos;
    bool _asked;

    public AppIcon(string title, IReadOnlyList<string> logos)
    {
        _logos = logos;
        var letter = title.FirstOrDefault(char.IsLetterOrDigit);
        Initial = letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
        Tint = Tints[title.Sum(c => c) % Tints.Length];
    }

    public string Initial { get; }
    public Brush Tint { get; }
    public ImageSource? Image { get; private set; }
    public Brush Backing { get; private set; } = DarkTile;
    public bool HasImage => Image != null;

    /// <summary>Fetches the icon the first time this tile comes into view, so a thousand apps don't mean a thousand downloads.</summary>
    public async void Load()
    {
        if (_asked) return;
        _asked = true;
        DockerIcon? icon;
        try
        {
            icon = await DockerIcons.GetAsync(_logos);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return;
        }
        if (icon == null) return;
        Image = icon.Image;
        Backing = icon.NeedsLightTile ? LightTile : DarkTile;
        Raise(string.Empty);
    }
}

public sealed class AppCard : ObservableObject
{
    public AppCard(DockerApp app)
    {
        App = app;
        Icon = new AppIcon(app.Title, app.Logos);
        var kinds = string.Join(" · ", app.Versions.Select(v => v.KindText).Distinct());
        Tag = app.Versions.Any(v => v.IsMine) ? $"{DockerMyTemplates.SourceName} · {kinds}"
            : app.Versions.Count > 1 ? $"{kinds} · {app.Versions.Count} versions" : kinds;
    }

    public DockerApp App { get; }
    public AppIcon Icon { get; }
    public string Title => App.Title;
    public string Description => App.Description;
    public string Tag { get; }

    bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>One line of the gallery. Lines are what the list virtualizes, so only the cards on screen exist.</summary>
public sealed record GalleryRow(AppCard[] Cards, int Columns);

/// <summary>A container row. Rows are kept and updated in place, so refreshing doesn't lose the selection or the sort.</summary>
public sealed class ContainerRow : ObservableObject
{
    public ContainerRow(DockerContainer data)
    {
        Data = data;
        _icon = new AppIcon(data.Name, Array.Empty<string>());
    }

    public DockerContainer Data { get; private set; }

    AppIcon _icon;
    public AppIcon Icon { get => _icon; set => Set(ref _icon, value); }
    public string Id => Data.Id;
    public string Name => Data.Name;
    public string Image => Data.Image;
    public string State => Data.State;
    public string Status => Data.Status;
    public string Ports => Data.Ports;
    public bool Running => Data.State == "running";

    string _cpu = "", _memory = "", _updateText = "";
    public string Cpu { get => _cpu; set => Set(ref _cpu, value); }
    public string Memory { get => _memory; set => Set(ref _memory, value); }
    /// <summary>Whether a newer image is available. Stays empty until you check.</summary>
    public string UpdateText { get => _updateText; set => Set(ref _updateText, value); }

    public void Update(DockerContainer data)
    {
        if (data == Data) return;
        Data = data;
        Raise(string.Empty);
    }
}

public partial class DockerPage : UserControl, IPage
{
    const int ContainersTab = 0, ImagesTab = 1, VolumesTab = 2, NetworksTab = 3, StacksTab = 4, DeployTab = 5;

    readonly ObservableCollection<ContainerRow> _containers = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly ICollectionView _containerView;
    readonly Dictionary<string, AppIcon> _iconsByImage = new();
    /// <summary>What the last check found, by container. A replaced container is a new ID, so it needs checking again.</summary>
    readonly Dictionary<string, ImageUpdate> _updates = new();
    readonly NetworkDraft _draft = new();
    DockerMap _map = DockerMap.Empty;
    bool _showMap = true;
    List<DockerApp> _listApps = new();
    TemplateLists _lists = new(new(), 0, new());
    List<AppCard> _cards = new();
    List<string> _listProblems = new();
    AppCard? _selected;
    DockerStatus? _status;
    CancellationTokenSource? _job;
    string _templateFilter = "";
    int _columns, _templateCount, _listCount;
    bool _refreshing, _busy, _loadingTemplates, _listsChecked;

    public DockerPage()
    {
        InitializeComponent();
        foreach (var grid in new[] { Containers, Images, Volumes, Networks, Stacks }) Ui.SetupGrid(grid);
        _containerView = new ListCollectionView(_containers);
        Containers.ItemsSource = _containerView;
        PullBox.TextChanged += (_, _) => UpdateButtons();
        TemplateSearch.TextChanged += t =>
        {
            _templateFilter = t.Trim();
            ShowCards();
        };
        _timer.Tick += async (_, _) => await Refresh();
        Map.DraftChanged += ShowDraft;
        Map.Hint += text =>
        {
            MapHint.Text = text.Length > 0 ? text : MapHelp;
            UpdateButtons();
        };
        Map.ContainerOpened += OpenFromMap;
        MapHint.Text = MapHelp;
    }

    const string MapHelp = "Drag a container's dot to a network to connect them. Click a line and press Delete to disconnect. Nothing changes until you apply.";

    public async void OnShown()
    {
        _timer.Start();
        // Saved lists are enough to put icons on containers, without going online just because the page opened.
        await LoadTemplates(force: false, cacheOnly: true);
        await Refresh();
    }

    public void OnHidden() => _timer.Stop();

    public void FocusSearch()
    {
        Tabs.SelectedIndex = DeployTab;
        TemplateSearch.FocusBox();
    }

    /// <summary>Dev aid for <c>--snap</c>: opens a tab by position.</summary>
    internal void ShowTab(int index) => Tabs.SelectedIndex = index;

    bool DockerUp => _status?.State == DockerState.Running;
    ContainerRow? SelectedContainer => Containers.SelectedItem as ContainerRow;
    DockerImage? SelectedImage => Images.SelectedItem as DockerImage;
    DockerVolume? SelectedVolume => Volumes.SelectedItem as DockerVolume;
    DockerNetwork? SelectedNetwork => Networks.SelectedItem as DockerNetwork;
    DockerStack? SelectedStack => Stacks.SelectedItem as DockerStack;
    DockerTemplate? SelectedTemplate => VersionPick.SelectedItem as DockerTemplate;

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

    async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if (Tabs.SelectedIndex == DeployTab) await LoadTemplates(force: false);
        await Refresh();
    }

    void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    async Task Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _status = await DockerCli.StatusAsync();
            ShowStatus(_status);
            if (DockerUp)
            {
                var list = await DockerCli.ContainersAsync();
                ShowContainers(list);
                switch (Tabs.SelectedIndex)
                {
                    case ContainersTab when _containers.Any(c => c.Running):
                        ShowStats(await DockerCli.StatsAsync());
                        break;
                    case ImagesTab:
                        Fill(Images, await DockerCli.ImagesAsync(), ImageCount, ImagesEmpty, "image", "No images yet. Pull one, or deploy something.", i => i.Id);
                        break;
                    case VolumesTab:
                        Fill(Volumes, await DockerCli.VolumesAsync(), VolumeCount, VolumesEmpty, "volume", "No volumes yet.", v => v.Name);
                        break;
                    case NetworksTab:
                        Fill(Networks, await DockerCli.NetworksAsync(), NetworkCount, NetworksEmpty, "network", "No networks.", n => n.Id);
                        if (_showMap) await ShowMap();
                        break;
                    case StacksTab:
                        Fill(Stacks, WithUpdates(await DockerCli.StacksAsync()), StackCount, StacksEmpty, "stack", "No Compose stacks yet. Deploy one from Quick deploy.", s => s.Name);
                        break;
                }
            }
            else
            {
                ClearLists();
            }
            UpdateButtons();
        }
        finally
        {
            _refreshing = false;
        }
    }

    void ShowStatus(DockerStatus s)
    {
        bool hasDesktop = DockerCli.DesktopApp() != null;
        InstallButton.Visibility = s.State == DockerState.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = s.State == DockerState.Stopped && hasDesktop ? Visibility.Visible : Visibility.Collapsed;
        switch (s.State)
        {
            case DockerState.NotInstalled:
                StatusTitle.Text = "Docker isn't installed";
                StatusText.Text = "Install Docker Desktop to run containers on this PC. You can browse Quick deploy in the meantime.";
                break;
            case DockerState.Stopped:
                StatusTitle.Text = "Docker isn't running";
                StatusText.Text = hasDesktop ? $"{s.Problem} Start Docker Desktop, which takes a minute or so." : s.Problem;
                break;
            default:
                StatusTitle.Text = $"Docker {s.EngineVersion} is running";
                int running = _containers.Count(c => c.Running);
                StatusText.Text = $"{running} of {_containers.Count} containers running · {s.EngineOs} containers";
                break;
        }
    }

    void ShowContainers(DockerList<DockerContainer> list)
    {
        var fresh = list.Items.ToDictionary(c => c.Id);
        foreach (var gone in _containers.Where(c => !fresh.ContainsKey(c.Id)).ToList()) _containers.Remove(gone);
        foreach (var gone in _updates.Keys.Where(id => !fresh.ContainsKey(id)).ToList()) _updates.Remove(gone);
        foreach (var data in list.Items)
        {
            if (_containers.FirstOrDefault(c => c.Id == data.Id) is { } row) row.Update(data);
            else _containers.Add(WithIcon(new ContainerRow(data)));
            if (!data.State.Equals("running", StringComparison.OrdinalIgnoreCase) && _containers.FirstOrDefault(c => c.Id == data.Id) is { } stopped)
                stopped.Cpu = stopped.Memory = "";
        }
        ContainerCount.Text = list.Error != null ? "" : $"{_containers.Count} containers";
        ContainersEmpty.Text = list.Error != null ? $"Can't list containers. {list.Error}" : "No containers yet. Quick deploy is the fastest way to start one.";
        ContainersEmpty.Visibility = _containers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_status != null) ShowStatus(_status);
    }

    /// <summary>Marks the stacks holding a container with a newer image, once a check has found one.</summary>
    DockerList<DockerStack> WithUpdates(DockerList<DockerStack> list)
    {
        var newer = _containers.Where(c => _updates.GetValueOrDefault(c.Id) == ImageUpdate.Newer).Select(c => c.Data.Stack).ToHashSet();
        return list with { Items = list.Items.Select(s => s with { Update = newer.Contains(s.Name) ? DockerUpdates.Describe(ImageUpdate.Newer) : "" }).ToList() };
    }

    ContainerRow WithIcon(ContainerRow row)
    {
        if (_iconsByImage.TryGetValue(DockerTemplates.ImageKey(row.Image), out var icon)) row.Icon = icon;
        row.Icon.Load();
        return row;
    }

    void ShowStats(Dictionary<string, DockerStats> stats)
    {
        foreach (var row in _containers)
        {
            if (!stats.TryGetValue(row.Id, out var s)) continue;
            row.Cpu = s.Cpu;
            row.Memory = s.Memory;
        }
    }

    /// <summary>Shows a list and puts the selection back, leaving the grid alone when nothing changed.</summary>
    static void Fill<T>(DataGrid grid, DockerList<T> list, TextBlock count, TextBlock empty, string noun, string emptyText, Func<T, string> key) where T : class
    {
        var old = grid.ItemsSource as List<T>;
        if (old == null || !old.SequenceEqual(list.Items))
        {
            var selected = grid.SelectedItem is T item ? key(item) : null;
            grid.ItemsSource = list.Items;
            grid.SelectedItem = list.Items.FirstOrDefault(i => key(i) == selected);
        }
        count.Text = list.Error != null ? "" : $"{list.Items.Count} {noun}{(list.Items.Count == 1 ? "" : "s")}";
        empty.Text = list.Error != null ? $"Can't list these. {list.Error}" : emptyText;
        empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClearLists()
    {
        _containers.Clear();
        var message = _status?.State == DockerState.NotInstalled ? "Docker isn't installed." : "Docker isn't running.";
        foreach (var grid in new[] { Images, Volumes, Networks, Stacks }) grid.ItemsSource = null;
        _map = DockerMap.Empty;
        _draft.Clear();
        Map.Show(_map, _draft);
        ShowDraft();
        foreach (var count in new[] { ContainerCount, ImageCount, VolumeCount, NetworkCount, StackCount }) count.Text = "";
        foreach (var empty in new[] { ContainersEmpty, ImagesEmpty, VolumesEmpty, NetworksEmpty, StacksEmpty })
        {
            empty.Text = message;
            empty.Visibility = Visibility.Visible;
        }
    }

    void UpdateButtons()
    {
        bool ready = DockerUp && !_busy;
        var c = SelectedContainer;
        ContainerStart.IsEnabled = ready && c is { Running: false };
        ContainerStop.IsEnabled = ContainerRestart.IsEnabled = ready && c is { Running: true };
        ContainerRemove.IsEnabled = ready && c != null;
        ContainerLogs.IsEnabled = ContainerInspect.IsEnabled = DockerUp && c != null;
        ContainerShell.IsEnabled = DockerUp && c is { Running: true };
        ContainerOpen.IsEnabled = c is { Running: true, Data.Url: not null };
        PullButton.IsEnabled = ready && PullBox.Text.Trim().Length > 0;
        ImageRemove.IsEnabled = ready && SelectedImage != null;
        ImagePrune.IsEnabled = VolumePrune.IsEnabled = ready;
        VolumeRemove.IsEnabled = ready && SelectedVolume != null;
        NetworkRemove.IsEnabled = ready && (_showMap ? Map.SelectedNetwork is not (null or "bridge" or "host" or "none") : SelectedNetwork is { BuiltIn: false });
        NetworkNew.IsEnabled = ready;
        MapApply.IsEnabled = ready;
        ContainerEdit.IsEnabled = ContainerUpdate.IsEnabled = ready && c != null;
        CheckUpdates.IsEnabled = ready && _containers.Count > 0;
        StackStart.IsEnabled = StackStop.IsEnabled = StackRestart.IsEnabled = StackRemove.IsEnabled = ready && SelectedStack != null;
        StackEdit.IsEnabled = StackUpdate.IsEnabled = ready && SelectedStack != null;
        StackLogs.IsEnabled = DockerUp && SelectedStack != null;
        StackFolder.IsEnabled = SelectedStack is { ConfigFiles.Length: > 0 };
        DeployButton.IsEnabled = ready && SelectedTemplate is { } t && t.CantDeploy(_status?.EngineOs) == null;
    }

    // ---- Running a job ----

    CancellationToken Begin(string title, bool canStop = true)
    {
        _busy = true;
        _job = new CancellationTokenSource();
        var job = _job;
        Output.Visibility = Visibility.Visible;
        HideOutputButton.Visibility = Visibility.Collapsed;
        Output.Start(title, canStop ? () => job.Cancel() : null);
        UpdateButtons();
        return job.Token;
    }

    void ShowLine(string line)
    {
        if (WingetCli.LogLine(line) is { } clean) Output.Line(clean);
    }

    async Task End(JobResult result)
    {
        Output.Finish(result);
        HideOutputButton.Visibility = Visibility.Visible;
        _busy = false;
        _job?.Dispose();
        _job = null;
        UpdateButtons();
        await Refresh();
    }

    void HideOutput_Click(object sender, RoutedEventArgs e) => HideOutput();

    void HideOutput() => Output.Visibility = HideOutputButton.Visibility = Visibility.Collapsed;

    static JobResult ResultOf(int code, string done) =>
        code == 0 ? new JobResult(JobLevel.Good, done)
        : code == Cmd.Cancelled ? new JobResult(JobLevel.Warn, "Stopped before it finished.")
        : new JobResult(JobLevel.Bad, "Docker reported a problem. The log above has the details.");

    async Task Run(string title, string done, params string[] args)
    {
        if (_busy) return;
        var cancel = Begin(title);
        int code = await DockerCli.StreamAsync(args, ShowLine, cancel);
        await End(ResultOf(code, done));
    }

    // ---- Docker itself ----

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!Ui.Confirm("Install Docker Desktop with winget?\n\n"
            + "Linux containers run in a small virtual machine, so Docker Desktop needs WSL. It's off on many PCs, including this one if you haven't used it, and turning it on needs a restart. "
            + "Windows may ask for approval during the install.\n\n"
            + "Docker Desktop is a large download. It's free for personal use and small businesses, and larger companies need a paid plan. Installing accepts Docker's subscription agreement:\n"
            + "https://www.docker.com/legal/docker-subscription-service-agreement", "Docker")) return;
        Begin("Install Docker Desktop", canStop: false);
        int code = await WingetCli.InstallAsync(DockerCli.DesktopPackage, ShowLine);
        bool ok = WingetCli.Succeeded(code);
        await End(new JobResult(ok ? JobLevel.Good : JobLevel.Bad, ok ? "Docker Desktop is installed. Restart if Windows asks, then start it once to accept its terms." : WingetCli.Describe(code), ok ? true : null));
    }

    void StartDocker_Click(object sender, RoutedEventArgs e)
    {
        if (DockerCli.DesktopApp() is not { } app) return;
        try
        {
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
            StatusText.Text = "Starting Docker Desktop. This page updates by itself when the engine is up.";
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't start Docker Desktop.\n\n{ex.Message}");
        }
    }

    // ---- Containers ----

    async void ContainerStart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is { } c) await Run($"Start {c.Name}", $"{c.Name} started.", "start", c.Id);
    }

    async void ContainerStop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is { } c) await Run($"Stop {c.Name}", $"{c.Name} stopped.", "stop", c.Id);
    }

    async void ContainerRestart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is { } c) await Run($"Restart {c.Name}", $"{c.Name} restarted.", "restart", c.Id);
    }

    async void ContainerRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is not { } c) return;
        var running = c.Running ? " It's running, so it will be stopped first." : "";
        if (!Ui.Confirm($"Remove the container {c.Name}?{running}\n\nAnything stored inside the container itself is lost. Its volumes and image stay.", "Docker")) return;
        await Run($"Remove {c.Name}", $"{c.Name} removed.", "rm", "--force", c.Id);
    }

    void ContainerLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is { } c) ShowOutput($"{c.Name} logs", "logs", "--tail", "500", "--follow", c.Id);
    }

    void ContainerInspect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is { } c) ShowOutput($"{c.Name} details", "inspect", c.Id);
    }

    void ShowOutput(string title, params string[] args) => new DockerOutputWindow(title, args) { Owner = Window.GetWindow(this) }.Show();

    async void ContainerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is { } c) await EditContainer(c);
    }

    async Task EditContainer(ContainerRow c)
    {
        if (_busy) return;
        if (c.Data.Stack.Length > 0)
        {
            await EditStack(c.Data.Stack);
            return;
        }
        if (await ReadChange(c, update: false) is not { } change) return;
        var form = new DockerDeployDialog(change, await NetworkNames()) { Owner = Window.GetWindow(this) };
        form.TemplateSaved += ShowMine;
        if (form.ShowDialog() != true) return;
        var cancel = Begin($"Change {c.Name}");
        await End(await change.RunAsync(ShowLine, cancel));
    }

    async void ContainerUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is not { } c || _busy) return;
        if (c.Data.Stack.Length > 0)
        {
            await UpdateStack(c.Data.Stack);
            return;
        }
        if (await ReadChange(c, update: true) is not { } change) return;
        if (DockerUpdates.Parse(change.After.Image) == null)
        {
            Ui.Error($"{c.Name} is fixed to one exact build of its image, so there's nothing newer to get. Use Edit to choose another version.", "Docker");
            return;
        }
        if (new DockerUpdateDialog(change) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        var cancel = Begin($"Update {c.Name}");
        await End(await change.RunAsync(ShowLine, cancel));
    }

    /// <summary>Reads everything needed to replace a container, including what would be lost. Null if it can't be replaced.</summary>
    async Task<RecreatePlan?> ReadChange(ContainerRow c, bool update)
    {
        _busy = true;
        UpdateButtons();
        try
        {
            var others = _containers.Where(o => o.Id != c.Id).ToList();
            var details = (await DockerCli.InspectAsync(new[] { c.Id }, withSize: true)).FirstOrDefault();
            if (details == null)
            {
                Ui.Error($"Docker can't find {c.Name} any more.", "Docker");
                return null;
            }
            if (details.InSwarm)
            {
                Ui.Error($"Docker Swarm runs {c.Name}, and would undo any change made here.", "Docker");
                return null;
            }
            var image = (await DockerCli.InspectImagesAsync(new[] { details.ImageId })).FirstOrDefault();
            var logDriver = await DockerCli.DefaultLogDriverAsync();
            var changed = await DockerCli.ChangedFilesAsync(c.Id);
            var relying = await DockerCli.InspectAsync(others.Select(o => o.Id));
            var notCarried = new List<string>();
            return new RecreatePlan
            {
                Container = details,
                Before = details.ToPlan(image, logDriver, notCarried),
                After = details.ToPlan(image, logDriver, new List<string>()),
                NotCarried = notCarried,
                ChangedFiles = changed,
                Dependents = relying.Where(o => o.DependsOn(details)).Select(o => o.Name).ToList(),
                TakenNames = others.Select(o => o.Name).ToList(),
                BusyTcp = DockerTemplates.BusyPorts("tcp"),
                BusyUdp = DockerTemplates.BusyPorts("udp"),
                IsUpdate = update,
                StartAfter = details.Running,
            };
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var cancel = Begin("Check for newer images");
        JobResult result;
        try
        {
            result = await CheckForUpdates(cancel);
        }
        catch (OperationCanceledException)
        {
            result = new JobResult(JobLevel.Warn, "Stopped before it finished.");
        }
        await End(result);
    }

    async Task<JobResult> CheckForUpdates(CancellationToken cancel)
    {
        var details = await DockerCli.InspectAsync(_containers.Select(c => c.Id).ToList());
        var images = await DockerCli.InspectImagesAsync(details.Select(d => d.ImageId));
        var digests = images.ToDictionary(i => i.Id, i => i.RepoDigests);

        using var gate = new SemaphoreSlim(4);
        async Task<(List<string> Ids, ImageUpdate Result)> Check(IGrouping<(string Image, string ImageId), DockerDetails> group)
        {
            await gate.WaitAsync(cancel);
            try
            {
                var found = await DockerUpdates.CheckAsync(group.Key.Image, digests.GetValueOrDefault(group.Key.ImageId, new()), cancel);
                ShowLine($"{group.Key.Image}: {DockerUpdates.Describe(found)}");
                return (group.Select(d => d.Id).ToList(), found);
            }
            finally
            {
                gate.Release();
            }
        }

        var results = await Task.WhenAll(details.GroupBy(d => (d.Image, d.ImageId)).Select(Check));
        cancel.ThrowIfCancellationRequested();
        foreach (var (ids, found) in results)
        {
            foreach (var id in ids) _updates[id] = found;
        }
        foreach (var row in _containers) row.UpdateText = _updates.TryGetValue(row.Id, out var u) ? DockerUpdates.Describe(u) : "";
        // The columns stay out of the way until there's something to put in them.
        ContainerUpdateColumn.Visibility = StackUpdateColumn.Visibility = Visibility.Visible;

        int newer = results.Where(r => r.Result == ImageUpdate.Newer).Sum(r => r.Ids.Count);
        int unknown = results.Where(r => r.Result == ImageUpdate.Unknown).Sum(r => r.Ids.Count);
        var unchecked_ = unknown > 0 ? $" {unknown} couldn't be checked, which usually means a private image." : "";
        return new JobResult(newer > 0 ? JobLevel.Warn : JobLevel.Good, newer > 0
            ? $"{newer} {(newer == 1 ? "container has" : "containers have")} a newer image. Select one and choose Update.{unchecked_}"
            : $"Nothing has a newer image.{unchecked_}");
    }

    void ContainerShell_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedContainer is not { } c || DockerCli.FindExe() is not { } exe) return;
        if (!Regex.IsMatch(c.Id, "^[0-9a-f]{12,64}$")) return;
        try
        {
            // The window stays open after the shell ends, so an image without sh leaves its error on screen.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\" exec --interactive --tty {c.Id} sh\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't open a shell.\n\n{ex.Message}");
        }
    }

    void ContainerOpen_Click(object sender, RoutedEventArgs e) => OpenUrl(SelectedContainer?.Data.Url);

    static void OpenUrl(string? url)
    {
        if (url == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't open {url}.\n\n{ex.Message}");
        }
    }

    void Containers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedContainer is { Running: true, Data.Url: { } url }) OpenUrl(url);
    }

    // ---- Images, volumes and networks ----

    async void Pull_Click(object sender, RoutedEventArgs e) => await Pull();

    async void PullBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PullButton.IsEnabled) await Pull();
    }

    async Task Pull()
    {
        var image = PullBox.Text.Trim();
        if (!DockerCli.IsValidImage(image))
        {
            Ui.Error("That isn't a valid image name. An example is nginx:latest.", "Docker");
            return;
        }
        await Run($"Pull {image}", $"{image} is ready.", "pull", image);
    }

    async void ImageRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedImage is not { } i) return;
        if (!Ui.Confirm($"Remove the image {i.Reference}?\n\nDocker refuses if a container uses it. You can pull it again later.", "Docker")) return;
        await Run($"Remove {i.Reference}", $"{i.Reference} removed.", "rmi", i.Reference);
    }

    async void ImagePrune_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Remove every image that no container uses?\n\nStopped containers count as using theirs. You can pull any of them again later.", "Docker")) return;
        await Run("Remove unused images", "Unused images removed.", "image", "prune", "--all", "--force");
    }

    async void VolumeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVolume is not { } v) return;
        if (!Ui.Confirm($"Remove the volume {v.Name}?\n\nEverything stored in it is permanently deleted. Docker refuses if a container uses it.", "Docker")) return;
        await Run($"Remove {v.Name}", $"{v.Name} removed.", "volume", "rm", v.Name);
    }

    async void VolumePrune_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Remove every volume that no container uses?\n\nThis permanently deletes the data in them, including data from containers you removed but may want to bring back.", "Docker")) return;
        await Run("Remove unused volumes", "Unused volumes removed.", "volume", "prune", "--all", "--force");
    }

    async void NetworkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_showMap)
        {
            Map.DeleteSelected();
            return;
        }
        if (SelectedNetwork is not { } n) return;
        if (!Ui.Confirm($"Remove the network {n.Name}?\n\nDocker refuses if a container is connected to it.", "Docker")) return;
        await Run($"Remove {n.Name}", $"{n.Name} removed.", "network", "rm", n.Id);
    }

    // ---- The network map ----

    async Task ShowMap()
    {
        _map = await DockerMapReader.ReadAsync();
        _draft.Prune(_map);
        Map.Show(_map, _draft);
        ShowDraft();
    }

    /// <summary>Shows the commands the drawing adds up to. Nothing runs until Apply.</summary>
    void ShowDraft()
    {
        bool any = !_draft.IsEmpty;
        MapChanges.Visibility = any && _showMap ? Visibility.Visible : Visibility.Collapsed;
        // A finished job's output takes room the drawing needs, above all in a small window.
        if (any && !_busy) HideOutput();
        if (any) MapPreview.Text = _draft.Preview();
        var warnings = _draft.Warnings(_map);
        MapWarnings.Text = string.Join("\n", warnings.Select(w => "•  " + w));
        MapWarnings.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void NetworkView_Click(object sender, RoutedEventArgs e)
    {
        _showMap = !_showMap;
        NetworkView.Content = _showMap ? "Show as list" : "Show as map";
        MapFrame.Visibility = _showMap ? Visibility.Visible : Visibility.Collapsed;
        Networks.Visibility = _showMap ? Visibility.Collapsed : Visibility.Visible;
        NetworkTidy.Visibility = _showMap ? Visibility.Visible : Visibility.Collapsed;
        ShowDraft();
        UpdateButtons();
    }

    void NetworkTidy_Click(object sender, RoutedEventArgs e) => Map.Tidy();

    void NetworkNew_Click(object sender, RoutedEventArgs e) => NewNetwork();

    void NewNetwork()
    {
        var taken = _map.Networks.Select(n => n.Name).Concat(_draft.Created.Select(n => n.Name)).ToList();
        var form = new DockerNetworkDialog(taken) { Owner = Window.GetWindow(this) };
        if (form.ShowDialog() != true) return;
        _draft.Create(form.Result);
        if (!_showMap) NetworkView_Click(this, new RoutedEventArgs());
        Map.Show(_map, _draft);
        ShowDraft();
    }

    async void MapApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _draft.IsEmpty) return;
        if (_draft.HasLosses && !Ui.Confirm($"Apply these network changes?\n\n{_draft.LossText()}", "Docker")) return;
        var commands = _draft.Commands();
        var cancel = Begin("Change networks");
        int code = 0;
        foreach (var command in commands)
        {
            ShowLine("> docker " + string.Join(" ", command.Select(DockerTemplates.Quote)));
            code = await DockerCli.StreamAsync(command, ShowLine, cancel);
            if (code != 0) break;
        }
        _draft.Clear();
        await End(code == 0 ? new JobResult(JobLevel.Good, "The network changes are made.")
            : code == Cmd.Cancelled ? new JobResult(JobLevel.Warn, "Stopped before it finished. The map shows where things stand.")
            : new JobResult(JobLevel.Bad, "Docker refused one of the changes, so the rest weren't made. The map shows where things stand."));
    }

    void MapDiscard_Click(object sender, RoutedEventArgs e)
    {
        _draft.Clear();
        Map.Show(_map, _draft);
        ShowDraft();
        MapHint.Text = MapHelp;
    }

    async void OpenFromMap(string name)
    {
        if (_containers.FirstOrDefault(c => c.Name == name) is { } row) await EditContainer(row);
    }

    // ---- Stacks ----

    async void StackStart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStack is { } s) await Run($"Start {s.Name}", $"{s.Name} started.", DockerCli.ComposeArgs(s.Name, "start"));
    }

    async void StackStop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStack is { } s) await Run($"Stop {s.Name}", $"{s.Name} stopped.", DockerCli.ComposeArgs(s.Name, "stop"));
    }

    async void StackRestart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStack is { } s) await Run($"Restart {s.Name}", $"{s.Name} restarted.", DockerCli.ComposeArgs(s.Name, "restart"));
    }

    async void StackRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStack is not { } s) return;
        if (!Ui.Confirm($"Remove the stack {s.Name}?\n\nIts containers and networks go. Its volumes and Compose file stay.", "Docker")) return;
        await Run($"Remove {s.Name}", $"{s.Name} removed.", DockerCli.ComposeArgs(s.Name, "down"));
    }

    async void StackEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStack is { } s) await EditStack(s.Name);
    }

    async void StackUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStack is { } s) await UpdateStack(s.Name);
    }

    /// <summary>Finds a stack's files, or says why they can't be used. Compose needs them for anything beyond start and stop.</summary>
    async Task<DockerStackFiles?> FindStackFiles(string name)
    {
        var stack = (await DockerCli.StacksAsync()).Items.FirstOrDefault(s => s.Name == name);
        var files = stack == null ? null : await DockerCli.StackFilesAsync(stack);
        if (files == null) Ui.Error($"Tman can't reach {name}'s Compose file from Windows. It may have been started somewhere else, such as inside WSL, or the file may have moved.", "Docker");
        return files;
    }

    async Task EditStack(string name)
    {
        if (_busy || await FindStackFiles(name) is not { } files) return;
        StackPlan plan;
        try
        {
            plan = StackPlan.From(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error($"Couldn't read {name}'s Compose file.\n\n{ex.Message}", "Docker");
            return;
        }
        var form = new DockerStackDialog(plan) { Owner = Window.GetWindow(this) };
        form.TemplateSaved += ShowMine;
        if (form.ShowDialog() != true) return;
        var cancel = Begin($"Change {name}");
        await End(await DockerStackChange.ApplyAsync(plan, ShowLine, cancel));
    }

    async Task UpdateStack(string name)
    {
        if (_busy || await FindStackFiles(name) is not { } files) return;
        if (new DockerUpdateDialog(files) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        var cancel = Begin($"Update {name}");
        await End(await DockerStackChange.UpdateAsync(files, ShowLine, cancel));
    }

    void StackLogs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStack is { } s) ShowOutput($"{s.Name} logs", DockerCli.ComposeArgs(s.Name, "logs", "--tail", "300", "--follow"));
    }

    void StackFolder_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(SelectedStack?.ConfigFiles.Split(',')[0].Trim());

    // ---- Quick deploy ----

    async void UpdateList_Click(object sender, RoutedEventArgs e) => await LoadTemplates(force: true);

    async void Lists_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DockerListsDialog(Settings.Current.DockerTemplateUrls, _listProblems) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        Settings.Current.DockerTemplateUrls = dialog.Urls;
        Settings.Current.Save();
        _listsChecked = false;
        await LoadTemplates(force: false);
    }

    /// <param name="cacheOnly">Use what's saved and stay offline.</param>
    async Task LoadTemplates(bool force, bool cacheOnly = false)
    {
        // A quick offline load can still be running when the tab opens, and the download has to wait for it, not be skipped.
        while (_loadingTemplates) await Task.Delay(50);
        if (!force && (cacheOnly ? _cards.Count > 0 : _listsChecked)) return;
        _loadingTemplates = true;
        UpdateListButton.IsEnabled = ListsButton.IsEnabled = false;
        if (!cacheOnly) TemplateCount.Text = "Getting the template lists…";
        try
        {
            var urls = Settings.Current.DockerTemplateUrls.ToList();
            var lists = await DockerTemplates.LoadAllAsync(urls, force, cacheOnly);
            var apps = await Task.Run(() => DockerTemplates.Merge(lists.Templates));
            if (!cacheOnly)
            {
                _listsChecked = true;
                _listProblems = lists.Problems;
            }
            ShowApps(apps, lists);
        }
        finally
        {
            _loadingTemplates = false;
            UpdateListButton.IsEnabled = ListsButton.IsEnabled = true;
        }
    }

    void ShowMine() => ShowApps(_listApps, _lists);

    void ShowApps(List<DockerApp> apps, TemplateLists lists)
    {
        _listApps = apps;
        _lists = lists;
        _templateCount = lists.Templates.Count;
        _listCount = lists.Loaded;
        // Your own come first, and stay separate, or they'd be folded into the app they were made from.
        _cards = DockerMyTemplates.Load().Select(DockerMyTemplates.ToApp).Concat(apps).Select(a => new AppCard(a)).ToList();

        _iconsByImage.Clear();
        foreach (var card in _cards)
        {
            foreach (var version in card.App.Versions.Where(v => v.Image.Length > 0))
                _iconsByImage.TryAdd(DockerTemplates.ImageKey(version.Image), card.Icon);
        }
        foreach (var row in _containers) WithIcon(row);

        // The lists use hundreds of categories between them, and most only ever hold an app or two.
        var current = CategoryFilter.SelectedItem as string;
        while (CategoryFilter.Items.Count > 1) CategoryFilter.Items.RemoveAt(1);
        var categories = apps.SelectMany(a => a.Categories).GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 5).Select(g => g.Key).OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories) CategoryFilter.Items.Add(category);
        CategoryFilter.SelectedItem = current;
        if (CategoryFilter.SelectedIndex < 0) CategoryFilter.SelectedIndex = 0;

        Select(null);
        ShowCards();
    }

    bool Matches(DockerApp a)
    {
        if (CategoryFilter.SelectedItem is string category && !a.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)) return false;
        if (KindFilter.SelectedIndex == 1 && !a.Versions.Any(v => v.Kind == DockerTemplateKind.Container)) return false;
        if (KindFilter.SelectedIndex == 2 && !a.Versions.Any(v => v.Kind is DockerTemplateKind.Compose or DockerTemplateKind.Edge)) return false;
        if (KindFilter.SelectedIndex == 3 && !a.Versions.Any(v => v.IsMine)) return false;
        return _templateFilter.Length == 0 || Ui.Contains(a.Title, _templateFilter) || Ui.Contains(a.Description, _templateFilter)
            || a.Categories.Any(c => Ui.Contains(c, _templateFilter)) || a.Versions.Any(v => Ui.Contains(v.Image, _templateFilter));
    }

    void TemplateFilter_Changed(object sender, SelectionChangedEventArgs e) => ShowCards();

    void Gallery_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Columns() != _columns) ShowCards();
    }

    int Columns() => Math.Max(1, (int)((Gallery.ActualWidth - 20) / 250));

    void ShowCards()
    {
        if (!IsInitialized) return;
        if (_cards.Count == 0)
        {
            TemplateCount.Text = "";
            GalleryEmpty.Text = _listProblems.Count > 0 ? "Couldn't get any template lists:\n\n" + string.Join("\n", _listProblems) : "";
            GalleryEmpty.Visibility = _listProblems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var shown = _cards.Where(c => Matches(c.App)).ToList();
        _columns = Columns();
        Gallery.ItemsSource = shown.Chunk(_columns).Select(cards => new GalleryRow(cards, _columns)).ToList();
        GalleryEmpty.Text = "No apps match that.";
        GalleryEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var failed = _listProblems.Count > 0 ? $" · {_listProblems.Count} couldn't update" : "";
        TemplateCount.Text = $"{shown.Count:N0} of {_cards.Count:N0} apps · {_templateCount:N0} templates from {_listCount} lists{failed}";
        TemplateCount.ToolTip = _listProblems.Count > 0 ? string.Join("\n", _listProblems) : null;
    }

    void Card_Loaded(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as AppCard)?.Icon.Load();

    void Card_Click(object sender, RoutedEventArgs e) => Select((sender as FrameworkElement)?.DataContext as AppCard);

    async void Card_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeployButton.IsEnabled) await Deploy();
    }

    void Select(AppCard? card)
    {
        if (_selected != null) _selected.IsSelected = false;
        _selected = card;
        if (card != null) card.IsSelected = true;
        DetailIcon.Content = card?.Icon;
        DetailIcon.Visibility = card != null ? Visibility.Visible : Visibility.Collapsed;
        VersionPick.ItemsSource = card?.App.Versions;
        VersionPick.SelectedIndex = card != null ? 0 : -1;
        VersionPick.Visibility = card?.App.Versions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowTemplateDetails();
        UpdateButtons();
    }

    void VersionPick_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowTemplateDetails();
        UpdateButtons();
    }

    void ShowTemplateDetails()
    {
        TemplateRemove.Visibility = SelectedTemplate is { IsMine: true } ? Visibility.Visible : Visibility.Collapsed;
        if (_selected == null || SelectedTemplate is not { } t)
        {
            DetailTitle.Text = "Choose an app";
            DetailText.Text = "";
            return;
        }
        DetailTitle.Text = t.Kind == DockerTemplateKind.Container ? $"{_selected.Title} · {t.Image}" : $"{_selected.Title} · {t.KindText} stack";
        var description = t.Description.Length > 0 ? t.Description : _selected.Description;
        DetailText.Text = t.CantDeploy(_status?.EngineOs) ?? (DockerUp ? description : $"Deploying needs Docker running. {description}");
    }

    void TemplateRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { IsMine: true } t) return;
        if (!Ui.Confirm($"Remove your template {t.Title}?\n\nOnly the template goes. Anything you deployed from it stays.", "Docker")) return;
        try
        {
            DockerMyTemplates.Remove(t.Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error($"Couldn't remove the template.\n\n{ex.Message}", "Docker");
        }
        ShowMine();
    }

    async void Deploy_Click(object sender, RoutedEventArgs e) => await Deploy();

    async Task<List<string>> NetworkNames() => (await DockerCli.NetworksAsync()).Items.Select(n => n.Name).ToList();

    async Task Deploy()
    {
        if (SelectedTemplate is not { } t || _busy) return;
        if (t.Kind == DockerTemplateKind.Container) await DeployContainer(t);
        else await DeployStack(t);
    }

    async Task DeployContainer(DockerTemplate t)
    {
        var plan = ContainerPlan.From(t, _containers.Select(c => c.Name).ToList(), DockerTemplates.BusyPorts("tcp"), DockerTemplates.BusyPorts("udp"));
        var form = new DockerDeployDialog(plan, t, await NetworkNames()) { Owner = Window.GetWindow(this) };
        form.TemplateSaved += ShowMine;
        if (form.ShowDialog() != true) return;

        var cancel = Begin($"Deploy {t.Title}");
        Output.Line(plan.Preview());
        int code = await DockerCli.StreamAsync(new[] { "pull", plan.Image.Trim() }, ShowLine, cancel);
        // A failed pull is fine if the image is already here, and Docker says so plainly if it isn't.
        if (code != Cmd.Cancelled) code = await DockerCli.StreamAsync(plan.RunArgs(), ShowLine, cancel);
        var firstPort = plan.Ports.FirstOrDefault(p => p.Protocol == "tcp" && int.TryParse(p.Host.Trim(), out _));
        var where = firstPort != null && !plan.HostNetwork ? $" Try http://localhost:{firstPort.Host.Trim()}" : "";
        await End(ResultOf(code, $"{plan.Name} is running.{where}"));
        if (code == 0) Tabs.SelectedIndex = ContainersTab;
    }

    async Task DeployStack(DockerTemplate t)
    {
        DetailText.Text = $"Downloading {t.Title}'s Compose file…";
        string yaml;
        List<string> projects;
        try
        {
            yaml = await DockerTemplates.FetchStackAsync(t);
            projects = (await DockerCli.StacksAsync()).Items.Select(s => s.Name).ToList();
        }
        catch (Exception ex)
        {
            ShowTemplateDetails();
            var other = _selected?.App.Versions.Count > 1 ? "\n\nThis app has other versions. Choose one beside the Deploy button." : "";
            Ui.Error($"Couldn't get {t.Title}'s Compose file.\n\n{ex.Message}{other}", "Docker");
            return;
        }
        ShowTemplateDetails();

        var plan = StackPlan.From(t, yaml, projects);
        var form = new DockerStackDialog(plan, t) { Owner = Window.GetWindow(this) };
        form.TemplateSaved += ShowMine;
        if (form.ShowDialog() != true) return;

        var cancel = Begin($"Deploy {t.Title}");
        int code;
        try
        {
            plan.Save();
            Output.Line(plan.Preview());
            code = await DockerCli.StreamAsync(plan.UpArgs(), ShowLine, cancel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Line($"Couldn't save the Compose file: {ex.Message}");
            code = Cmd.FailedToStart;
        }
        await End(ResultOf(code, $"{plan.Project} is running."));
        if (code == 0) Tabs.SelectedIndex = StacksTab;
    }
}
