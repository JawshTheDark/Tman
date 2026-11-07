using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;

namespace Tman.Pages;

/// <summary>A VM row. Rows are kept and updated in place, so a refresh doesn't lose the selection or the sort.</summary>
public sealed class VmRow : ObservableObject
{
    public VmRow(HyperVm data) => Data = data;

    public HyperVm Data { get; private set; }
    public string Name => Data.Name;
    public string State => Data.State;
    public string Cpu => Data.Cpu is { } cpu ? $"{cpu}%" : "";
    public string Memory => Data.MemoryBytes is { } bytes ? Fmt.Bytes(bytes) : "";
    public string Uptime => Data.Uptime > TimeSpan.Zero ? Fmt.Span(Data.Uptime) : "";
    public uint CpuValue => Data.Cpu ?? 0;
    public ulong MemoryValue => Data.MemoryBytes ?? 0;
    public TimeSpan UptimeValue => Data.Uptime;

    public Brush StateBrush => (Brush)Application.Current.FindResource(Data.Power switch
    {
        VmPower.Running => "GoodBrush",
        VmPower.Off or VmPower.Unknown => "SubtleBrush",
        _ => "WarnBrush",
    });

    public void Update(HyperVm data)
    {
        if (data == Data) return;
        Data = data;
        Raise(string.Empty);
    }
}

public partial class HyperVPage : UserControl, IPage
{
    /// <summary>How long a read gets before the page says Hyper-V isn't answering. It keeps going in the background, but the page stops waiting.</summary>
    static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(20);

    readonly ObservableCollection<VmRow> _vms = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    List<VmCheckpoint> _checkpoints = new();
    List<string> _gaps = new();
    HyperVStatus? _status;
    bool _shown, _refreshing, _running, _sample;

    public HyperVPage()
    {
        InitializeComponent();
        Ui.SetupGrid(Vms);
        Ui.SetupGrid(Checkpoints);
        Vms.ItemsSource = _vms;
        Ui.SortBy(Vms, nameof(VmRow.Name), ListSortDirection.Ascending);
        Note.Visibility = HyperV.HasRights ? Visibility.Collapsed : Visibility.Visible;
        _timer.Tick += async (_, _) => await Refresh();
    }

    public async void OnShown()
    {
        if (ShowSample()) return;
        _shown = true;
        await Refresh(withGaps: true);
    }

    public void OnHidden()
    {
        _shown = false;
        _timer.Stop();
    }

    VmRow? SelectedVm => Vms.SelectedItem as VmRow;
    VmCheckpoint? SelectedCheckpoint => Checkpoints.SelectedItem as VmCheckpoint;

    // ---- Reading ----

    /// <param name="withGaps">Also checks for missing tools, which only changes with a Windows features change, so it isn't worth doing on every tick.</param>
    async Task Refresh(bool withGaps = false)
    {
        if (_refreshing || _sample) return;
        _refreshing = true;
        try
        {
            var read = Task.Run(HyperV.Read);
            var reading = await Task.WhenAny(read, Task.Delay(ReadLimit)) == read
                ? await read
                : new HyperVReading(HyperVStatus.Unknown, Array.Empty<HyperVm>(), "Hyper-V didn't answer in time.");
            if (withGaps && reading.Status == HyperVStatus.Ready) _gaps = await Task.Run(HyperV.Gaps);
            Show(reading);
            await LoadCheckpoints();
            // Nothing changes on its own while Hyper-V is off: it takes a restart, which brings the page back through OnShown.
            _timer.IsEnabled = _shown && reading.Status == HyperVStatus.Ready;
        }
        finally
        {
            _refreshing = false;
        }
    }

    void Show(HyperVReading reading)
    {
        _status = reading.Status;
        bool ready = reading.Status == HyperVStatus.Ready;
        var (title, text) = Describe(reading);
        StatusTitle.Text = title;
        StatusText.Text = text;
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusCard.Visibility = title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusCard.BorderBrush = (Brush)FindResource(ready ? "WarnBrush" : "CardStrokeBrush");
        FeaturesButton.Visibility = reading.Status == HyperVStatus.TurnedOff || (ready && _gaps.Count > 0 && !(HyperV.HasModule && HyperV.HasConsole))
            ? Visibility.Visible : Visibility.Collapsed;
        ServicesButton.Visibility = reading.Status == HyperVStatus.ServiceStopped ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = NewButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        ShowVms(reading.Vms);
        UpdateButtons();
    }

    (string Title, string Text) Describe(HyperVReading reading) => reading.Status switch
    {
        HyperVStatus.Ready => _gaps.Count > 0 ? ("Hyper-V is running, but something's missing", string.Join("\n", _gaps)) : ("", ""),
        HyperVStatus.TurnedOff => ("Hyper-V isn't turned on",
            "Virtual machines need it. Turn it on in Windows features, then restart Windows. If you've just turned it on, restart to finish."),
        HyperVStatus.RestartNeeded => ("Restart Windows to finish turning on Hyper-V", "It's turned on, but it can't run until Windows has restarted."),
        HyperVStatus.NotInEdition => ("This edition of Windows doesn't include Hyper-V", "It comes with Windows 11 Pro, Enterprise and Education, but not Home."),
        HyperVStatus.ServiceStopped => ("Hyper-V isn't running", "The Hyper-V Virtual Machine Management service is stopped. Start it on the Services page."),
        HyperVStatus.NoAccess => ("Tman can't read your virtual machines",
            "Windows only shows them to administrators and members of the Hyper-V Administrators group."),
        _ => ("Tman couldn't check Hyper-V", reading.Detail),
    };

    void ShowVms(IReadOnlyList<HyperVm> vms)
    {
        foreach (var gone in _vms.Where(r => vms.All(v => v.Id != r.Data.Id)).ToList()) _vms.Remove(gone);
        foreach (var vm in vms)
        {
            var row = _vms.FirstOrDefault(r => r.Data.Id == vm.Id);
            if (row != null) row.Update(vm);
            else _vms.Add(new VmRow(vm));
        }
        Vms.SelectedItem ??= _vms.FirstOrDefault();
        VmsEmpty.Visibility = vms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int on = vms.Count(v => v.Power is not (VmPower.Off or VmPower.Saved));
        CountText.Text = _status != HyperVStatus.Ready ? "" : vms.Count == 1 ? $"1 virtual machine, {on} on" : $"{vms.Count} virtual machines, {on} on";
    }

    async Task LoadCheckpoints()
    {
        if (_sample) return;
        if (SelectedVm?.Data is not { } vm)
        {
            ShowCheckpoints(null, new List<VmCheckpoint>());
            return;
        }
        var list = await Task.Run(() => HyperV.ReadCheckpoints(vm.Id));
        if (SelectedVm?.Data.Id == vm.Id) ShowCheckpoints(vm, list);
    }

    /// <param name="list">Null means they couldn't be read.</param>
    void ShowCheckpoints(HyperVm? vm, List<VmCheckpoint>? list)
    {
        var items = list ?? new List<VmCheckpoint>();
        if (!items.SequenceEqual(_checkpoints))
        {
            var selected = SelectedCheckpoint;
            _checkpoints = items;
            Checkpoints.ItemsSource = items;
            Checkpoints.SelectedItem = selected != null && items.Contains(selected) ? selected : items.LastOrDefault();
        }
        CheckpointTitle.Text = vm == null ? "CHECKPOINTS" : $"CHECKPOINTS OF {vm.Name.ToUpperInvariant()}";
        CheckpointsEmpty.Text = vm == null ? "Select a virtual machine to see its checkpoints."
            : list == null ? "Tman couldn't read its checkpoints."
            : "No checkpoints yet. Take one before you change something you might want to undo.";
        CheckpointsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var power = SelectedVm?.Data.Power;
        bool idle = !_running && power != null;
        ConsoleButton.IsEnabled = idle && (HyperV.HasConsole || VmConsoleWindow.CanConnect);
        StartButton.IsEnabled = idle && power is VmPower.Off or VmPower.Saved;
        ShutDownButton.IsEnabled = idle && power == VmPower.Running;
        PauseButton.IsEnabled = idle && power is VmPower.Running or VmPower.Paused;
        TurnOffButton.IsEnabled = SaveButton.IsEnabled = idle && power is VmPower.Running or VmPower.Paused;
        PauseButton.Content = power == VmPower.Paused ? "Resume" : "Pause";
        DeleteButton.IsEnabled = idle && power is VmPower.Off or VmPower.Saved;
        TakeButton.IsEnabled = idle && HyperV.HasModule && power is not VmPower.Changing;
        ApplyButton.IsEnabled = idle && SelectedCheckpoint != null && power is VmPower.Off or VmPower.Saved;
        DeleteCheckpointButton.IsEnabled = idle && SelectedCheckpoint != null && power is not VmPower.Changing;
        NewButton.IsEnabled = !_running && HyperV.HasModule;
        RefreshButton.IsEnabled = !_running;
        CheckpointHint.Text = SelectedCheckpoint != null && power is VmPower.Running or VmPower.Paused ? "Shut it down to apply one." : "";
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = Refresh(withGaps: true);

    async void Vms_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        await LoadCheckpoints();
    }

    void Checkpoints_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    void Vms_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking a column header or empty space isn't asking for the selected VM.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Vms, source) is DataGridRow && ConsoleButton.IsEnabled) OpenConsole();
    }

    void Features_Click(object sender, RoutedEventArgs e) => OpenPage("Windows features");

    void Services_Click(object sender, RoutedEventArgs e) => OpenPage("Services");

    /// <summary>The nav list's items carry their title as a tooltip.</summary>
    static void OpenPage(string title)
    {
        if (Application.Current.MainWindow is MainWindow main)
            main.Nav.SelectedItem = main.Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.ToolTip as string == title) ?? main.Nav.SelectedItem;
    }

    // ---- State changes ----

    void Console_Click(object sender, RoutedEventArgs e) => OpenConsole();

    void OpenConsole()
    {
        if (SelectedVm?.Data is not { } vm) return;
        // Without those rights, Hyper-V's own window can still ask for them.
        if (VmConsoleWindow.CanConnect) VmConsoleWindow.Show(vm, Window.GetWindow(this));
        else if (HyperV.OpenConsole(vm) is { } failed) Ui.Error($"The console didn't open.\n\n{failed}");
    }

    async void Start_Click(object sender, RoutedEventArgs e) => await Change(VmAction.Start, "Starting");

    async void ShutDown_Click(object sender, RoutedEventArgs e) => await Change(VmAction.ShutDown, "Asking to shut down:");

    async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVm?.Data.Power == VmPower.Paused) await Change(VmAction.Resume, "Resuming");
        else await Change(VmAction.Pause, "Pausing");
    }

    async void Save_Click(object sender, RoutedEventArgs e) => await Change(VmAction.Save, "Saving");

    async void TurnOff_Click(object sender, RoutedEventArgs e)
    {
        if (_running || SelectedVm?.Data is not { } vm) return;
        if (Ui.Confirm($"Turn off {vm.Name}?\n\nThis is the same as pulling the plug. Anything unsaved inside it is lost, and its system may have to check its disk at the next start.\n\nShut down is the gentler choice if it's still responding.", "Tman - turn off a virtual machine"))
            await Change(VmAction.TurnOff, "Turning off");
    }

    /// <summary>These are quick and show in the list as they happen, so only a failure needs saying.</summary>
    async Task Change(VmAction action, string doing)
    {
        if (_running || SelectedVm?.Data is not { } vm) return;
        _running = true;
        ActionText.Text = $"{doing} {vm.Name}…";
        UpdateButtons();
        var failed = await Task.Run(() => HyperV.ChangeAsync(vm, action));
        _running = false;
        ActionText.Text = failed == null && action == VmAction.ShutDown ? $"{vm.Name} was asked to shut down. Programs open inside it can hold that up." : "";
        await Refresh();
        if (failed == HyperV.LockedSystem)
        {
            if (Ui.Confirm($"{vm.Name} is locked, so it won't shut down when asked.\n\nShut it down anyway? It goes down at once, and anything unsaved inside it is lost.", "Tman - shut down a locked virtual machine"))
                await Change(VmAction.ForceShutDown, doing);
        }
        else if (failed != null) Ui.Error($"{doing} {vm.Name} didn't work.\n\n{failed}");
    }

    // ---- Longer jobs ----

    /// <summary>Nothing here is safe to stop partway, so there's no Stop button, and Tman doesn't wait for a job on its way out.</summary>
    async Task RunJob(string title, Func<Action<string>, Task<JobResult>> job)
    {
        _running = true;
        ActionText.Text = "";
        UpdateButtons();
        ShowPanel();
        HideOutputButton.Visibility = Visibility.Collapsed;
        Panel.Start(title, null);
        JobResult result;
        try
        {
            result = await Task.Run(() => job(Panel.Line));
        }
        catch (Exception ex)
        {
            result = new JobResult(JobLevel.Bad, $"It failed: {ex.Message}");
        }
        Panel.Finish(result);
        HideOutputButton.Visibility = Visibility.Visible;
        _running = false;
        await Refresh();
    }

    void ShowPanel()
    {
        Panel.Visibility = Visibility.Visible;
        PanelRow.Height = new GridLength(1, GridUnitType.Star);
    }

    void HideOutput_Click(object sender, RoutedEventArgs e)
    {
        Panel.Visibility = HideOutputButton.Visibility = Visibility.Collapsed;
        PanelRow.Height = GridLength.Auto;
    }

    async void New_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        UpdateButtons();
        var options = await Task.Run(HyperV.ReadNewVmOptions);
        _running = false;
        UpdateButtons();
        var dialog = new NewVmDialog(options, _vms.Select(v => v.Name).ToList()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Plan is not { } plan) return;
        await RunJob($"Creating {plan.Name}", line => HyperV.CreateAsync(plan, line));
        Vms.SelectedItem = _vms.FirstOrDefault(v => v.Name == plan.Name) ?? Vms.SelectedItem;
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_running || SelectedVm?.Data is not { } vm) return;
        _running = true;
        ActionText.Text = $"Checking {vm.Name}'s disks…";
        UpdateButtons();
        var disks = await Task.Run(() => HyperV.ReadDisksAsync(vm));
        int checkpoints = (await Task.Run(() => HyperV.ReadCheckpoints(vm.Id)))?.Count ?? 0;
        _running = false;
        ActionText.Text = "";
        UpdateButtons();

        var lost = new List<string> { "its settings" };
        if (checkpoints > 0) lost.Add(checkpoints == 1 ? "its checkpoint" : $"its {checkpoints} checkpoints");
        if (vm.Power == VmPower.Saved) lost.Add("its saved state");
        var unlisted = disks == null ? "\n\nIts virtual hard disks stay on this PC, because Tman couldn't list them." : "";
        if (!Ui.Confirm($"Delete the virtual machine {vm.Name}?\n\nThis deletes {string.Join(", ", lost)} for good. It can't be undone.{unlisted}", "Tman - delete a virtual machine")) return;

        var doomed = new List<VmDiskFile>();
        if (disks is { Count: > 0 })
        {
            var files = string.Join("\n", disks.Select(d => $"•  {d.Path} ({Fmt.Bytes(d.Bytes)})"));
            if (Ui.Confirm($"Also delete {vm.Name}'s virtual hard disks?\n\n{files}\n\nChoose Yes and everything stored inside the virtual machine is gone for good: its system, apps and files.\n\nChoose No to keep the disk files, so you can attach them to another VM.", "Tman - delete virtual hard disks"))
                doomed = disks;
        }
        await RunJob($"Deleting {vm.Name}", line => HyperV.DeleteVmAsync(vm, doomed, line));
    }

    async void Take_Click(object sender, RoutedEventArgs e)
    {
        if (_running || SelectedVm?.Data is not { } vm) return;
        await RunJob($"Checkpoint of {vm.Name}", line => HyperV.TakeCheckpointAsync(vm, line));
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_running || SelectedVm?.Data is not { } vm || SelectedCheckpoint is not { } checkpoint) return;
        var saved = vm.Power == VmPower.Saved ? " Its saved state is lost too." : "";
        if (!Ui.Confirm($"Apply the checkpoint \"{checkpoint.Name}\" to {vm.Name}?\n\n{vm.Name} goes back to how it was when this checkpoint was taken. Everything that has changed inside it since then is lost, including files and installed apps.{saved}\n\nTo keep the way it is now, cancel and take a new checkpoint first.", "Tman - apply a checkpoint")) return;
        await RunJob($"Applying a checkpoint to {vm.Name}", line => HyperV.ApplyCheckpointAsync(vm, checkpoint, line));
    }

    async void DeleteCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_running || SelectedVm?.Data is not { } vm || SelectedCheckpoint is not { } checkpoint) return;
        if (!Ui.Confirm($"Delete the checkpoint \"{checkpoint.Name}\" of {vm.Name}?\n\nYou won't be able to go back to it. {vm.Name} itself stays as it is now.", "Tman - delete a checkpoint")) return;
        await RunJob($"Deleting a checkpoint of {vm.Name}", line => HyperV.DeleteCheckpointAsync(vm, checkpoint, line));
    }

    // ---- Dev aid ----

    /// <summary>
    /// <c>--snap &lt;dir&gt; hyperv sample</c> fills the page with made-up machines, so that its layout can be checked on a PC
    /// without Hyper-V. Adding <c>output</c> shows the job panel too.
    /// </summary>
    bool ShowSample()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("--snap") || !args.Contains("sample")) return false;
        var now = DateTime.Now;
        var vms = new[]
        {
            new HyperVm(Guid.NewGuid().ToString(), "Windows 11 test", VmPower.Running, "Running", 12, 4294967296, TimeSpan.FromMinutes(194)),
            new HyperVm(Guid.NewGuid().ToString(), "Ubuntu server", VmPower.Saved, "Saved", null, null, TimeSpan.Zero),
            new HyperVm(Guid.NewGuid().ToString(), "Trimmed ISO check", VmPower.Off, "Off", null, null, TimeSpan.Zero),
        };
        _sample = true;
        Note.Visibility = Visibility.Collapsed;
        Show(new HyperVReading(HyperVStatus.Ready, vms));
        Vms.SelectedItem = _vms[0];
        ShowCheckpoints(vms[0], new List<VmCheckpoint>
        {
            new("a", "Windows 11 test - (Clean install)", now.AddDays(-3)),
            new("b", "Before driver update", now.AddHours(-5)),
        });
        if (args.Contains("output"))
        {
            ShowPanel();
            Panel.Start("Creating Windows 11 test", null);
            foreach (var line in new[] { "Creating the virtual machine and its disk", "Setting processors and memory", "Attaching the ISO", "Adding the virtual TPM", "Done" }) Panel.Line(line);
            Panel.Finish(new JobResult(JobLevel.Good, "Windows 11 test is ready. Start it, open its console, and press a key when it offers to boot from the ISO."));
            HideOutputButton.Visibility = Visibility.Visible;
        }
        return true;
    }
}
