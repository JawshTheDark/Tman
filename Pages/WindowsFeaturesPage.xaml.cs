using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Pages;

public enum FeatureState { Off, On, Absent, NotAvailable, Unknown }

/// <summary>A row with an on/off action, either an optional feature or a capability, which DISM adds and removes differently.</summary>
public sealed class FeatureRow : ObservableObject
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string Warning { get; init; } = "";
    public bool HasWarning => Warning.Length > 0;
    public required string OnArgs { get; init; }
    public required string OffArgs { get; init; }
    public string OnVerb { get; init; } = "Turn on";
    public string OffVerb { get; init; } = "Turn off";
    public string OnState { get; init; } = "On";
    public string OffState { get; init; } = "Off";
    public string OnEffect { get; init; } = "";
    public string OffEffect { get; init; } = "";
    public bool RestartPending { get; init; }

    public required FeatureState State { get; init; }

    bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(CanAct)); } }

    public bool IsOn => State == FeatureState.On;
    public bool HasAction => !RestartPending && State is FeatureState.On or FeatureState.Off or FeatureState.Absent;
    public bool CanAct => MainWindow.IsAdmin && !_busy;
    public string ActionText => IsOn ? OffVerb : OnVerb;

    public string StateText => RestartPending ? "Restart to finish" : State switch
    {
        FeatureState.On => OnState,
        FeatureState.Off => OffState,
        FeatureState.Absent => "Files not on this PC",
        FeatureState.NotAvailable => "Not in this edition",
        _ => "Unknown",
    };

    public Brush StateBrush => (Brush)Application.Current.FindResource(RestartPending ? "WarnBrush" : IsOn ? "GoodBrush" : "SubtleBrush");

    public bool Matches(string filter) => filter.Length == 0 || Ui.Contains(Title, filter) || Ui.Contains(Description, filter);
}

public partial class WindowsFeaturesPage : UserControl, IPage
{
    sealed record Known(string Title, string Description, string StateName, string? OnName = null, string? OffName = null, string Warning = "");

    /// <summary>Where a parent feature does nothing by itself, the row turns on the child it's really for; turning off goes through the parent.</summary>
    static readonly Known[] Common =
    {
        new("Hyper-V", "Runs full virtual machines on this PC, and adds Hyper-V Manager.", "Microsoft-Hyper-V-All", OnName: "Microsoft-Hyper-V",
            Warning: "VirtualBox and VMware can run slowly or fail to start machines while this is on."),
        new("Windows Subsystem for Linux", "Runs Linux tools and distributions inside Windows.", "Microsoft-Windows-Subsystem-Linux"),
        new("Virtual Machine Platform", "Virtualization support that WSL 2 and some other apps need.", "VirtualMachinePlatform"),
        new("Windows Sandbox", "A throwaway desktop for trying programs safely. Everything in it is wiped when it closes.", "Containers-DisposableClientVM"),
        new(".NET Framework 3.5", "Needed by some older apps and games. Windows downloads it when you turn it on.", "NetFx3"),
        new("DirectPlay", "An old networking library that some games from the 2000s need.", "DirectPlay"),
        new("Media Features", "Windows Media Player Legacy and the media parts some apps rely on.", "MediaPlayback"),
        new("NFS client", "Connects to NFS file shares, which are common on Linux servers and NAS boxes.", "ClientForNFS-Infrastructure", OffName: "ServicesForNFS-ClientOnly"),
        new("Telnet client", "An old remote terminal, still handy for testing whether a port is open. It sends everything unencrypted.", "TelnetClient"),
        new("SMB 1.0 file sharing", "Reaches very old NAS boxes, printers and PCs that can't use anything newer.", "SMB1Protocol", OnName: "SMB1Protocol-Client",
            Warning: "Insecure. SMB1 has known holes that ransomware such as WannaCry spread through. Leave it off unless a device needs it."),
    };

    static readonly string[] CommonNames = Common.Select(k => k.StateName).ToArray();

    const string FirewallRule = "OpenSSH-Server-In-TCP";
    const string ServerId = "OpenSSH.Server~~~~0.0.1.0", ClientId = "OpenSSH.Client~~~~0.0.1.0";

    readonly HashSet<string> _restartPending = new(StringComparer.OrdinalIgnoreCase);
    List<FeatureRow> _common = new(), _other = new(), _ssh = new();
    int _total, _on;
    string _filter = "";
    bool _running;

    public WindowsFeaturesPage()
    {
        InitializeComponent();
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            ApplyFilter();
        };
        Load();
    }

    public void FocusSearch() => Search.FocusBox();

    async void Load()
    {
        CountText.Text = "Loading…";
        RefreshButton.IsEnabled = false;
        var features = await Task.Run(() => Wmi.Query("SELECT Name, Caption, InstallState FROM Win32_OptionalFeature")
            .Select(o => (Name: o.Str("Name"), Caption: o.Str("Caption"), State: o.U64("InstallState")))
            .Where(f => f.Name.Length > 0)
            .ToList());
        BuildFeatures(features);
        ShowSsh(await Task.Run(() => ReadSsh(useDism: false)));
        if (MainWindow.IsAdmin) ShowSsh(await Task.Run(() => ReadSsh(useDism: true)));
        RefreshButton.IsEnabled = !_running;
    }

    void BuildFeatures(List<(string Name, string Caption, ulong State)> features)
    {
        var byName = features.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        _common = Common.Select(k =>
        {
            byName.TryGetValue(k.StateName, out var f);
            return new FeatureRow
            {
                Title = k.Title,
                Description = k.Description,
                Warning = k.Warning,
                OnArgs = EnableArgs(k.OnName ?? k.StateName),
                OffArgs = DisableArgs(k.OffName ?? k.StateName),
                State = f.Name == null ? FeatureState.NotAvailable : StateOf(f.State),
                RestartPending = _restartPending.Contains(k.StateName),
                Busy = _running,
            };
        }).ToList();
        _other = features.Where(f => !CommonNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Caption, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FeatureRow
            {
                Title = f.Caption.Length > 0 ? f.Caption : f.Name,
                Description = f.Name,
                OnArgs = EnableArgs(f.Name),
                OffArgs = DisableArgs(f.Name),
                State = StateOf(f.State),
                RestartPending = _restartPending.Contains(f.Name),
                Busy = _running,
            }).ToList();
        _total = features.Count;
        _on = features.Count(f => f.State == 1);
        CommonList.ItemsSource = _common;
        ApplyFilter();
    }

    static FeatureState StateOf(ulong installState) => installState switch
    {
        1 => FeatureState.On,
        2 => FeatureState.Off,
        3 => FeatureState.Absent,
        _ => FeatureState.Unknown,
    };

    static string EnableArgs(string name) => $"/Online /English /Enable-Feature /FeatureName:{name} /All /NoRestart";
    static string DisableArgs(string name) => $"/Online /English /Disable-Feature /FeatureName:{name} /NoRestart";

    void ApplyFilter()
    {
        var common = _common.Where(r => r.Matches(_filter)).ToList();
        var other = _other.Where(r => r.Matches(_filter)).ToList();
        bool ssh = _filter.Length == 0 || Ui.Contains("OpenSSH sshd server client remote sign-in", _filter);
        CommonList.ItemsSource = common;
        OtherList.ItemsSource = other;
        CommonCard.Visibility = common.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherCard.Visibility = other.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SshCard.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _total == 0 ? "" : $"{_total} features · {_on} on";
        EmptyText.Visibility = _total == 0 || (common.Count + other.Count == 0 && !ssh) ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _total == 0
            ? "Windows didn't return a list of features. Try Refresh. If it stays empty, the Windows Management Instrumentation service may be stopped."
            : "No features match your search.";
    }

    sealed record SshState(FeatureState Server, FeatureState Client, string ServerName, string ClientName, string Service, string Firewall);

    /// <summary>DISM only answers administrators, so a standard user gets the state of the program files instead.</summary>
    static SshState ReadSsh(bool useDism)
    {
        string dir = Path.Combine(Environment.SystemDirectory, "OpenSSH");
        var server = File.Exists(Path.Combine(dir, "sshd.exe")) ? FeatureState.On : FeatureState.Off;
        var client = File.Exists(Path.Combine(dir, "ssh.exe")) ? FeatureState.On : FeatureState.Off;
        string serverName = ServerId, clientName = ClientId;
        if (useDism)
        {
            var list = Cmd.Run(Cmd.System("dism.exe"), "/Online /English /Get-Capabilities", 120000);
            if (list.Ok)
            {
                foreach (var (name, state) in ParseCapabilities(list.Output))
                {
                    if (name.StartsWith("OpenSSH.Server", StringComparison.OrdinalIgnoreCase)) (serverName, server) = (name, state);
                    else if (name.StartsWith("OpenSSH.Client", StringComparison.OrdinalIgnoreCase)) (clientName, client) = (name, state);
                }
            }
        }
        return new SshState(server, client, serverName, clientName, ReadService(), ReadFirewall());
    }

    /// <summary>Reads pairs of "Capability Identity : X" and "State : Installed" lines.</summary>
    static IEnumerable<(string Name, FeatureState State)> ParseCapabilities(string output)
    {
        string? name = null;
        foreach (var line in output.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var value = line[(colon + 1)..].Trim();
            if (value.Contains("~~~")) name = value;
            else if (name != null)
            {
                yield return (name, value.Equals("Installed", StringComparison.OrdinalIgnoreCase) ? FeatureState.On : FeatureState.Off);
                name = null;
            }
        }
    }

    static string ReadService()
    {
        try
        {
            using var service = new ServiceController("sshd");
            var running = service.Status == ServiceControllerStatus.Running ? "Running" : "Not running";
            var start = service.StartType switch
            {
                ServiceStartMode.Automatic => "starts automatically",
                ServiceStartMode.Disabled => "disabled",
                _ => "starts only when started by hand",
            };
            return $"{running}, {start}. Change this on the Services page.";
        }
        catch (InvalidOperationException)
        {
            return "Not there. It comes with OpenSSH Server.";
        }
    }

    static string ReadFirewall()
    {
        try
        {
            using var rules = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
            if (rules == null) return "Windows didn't say.";
            return rules.GetValue(FirewallRule) is string rule ? DescribeRule(rule) : $"No rule called {FirewallRule}, so other PCs can't connect.";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "Windows wouldn't let Tman read the firewall rules.";
        }
    }

    /// <summary>A stored rule looks like <c>v2.33|Action=Allow|Active=TRUE|Dir=In|Protocol=6|Profile=Private|LPort=22|</c>.</summary>
    static string DescribeRule(string rule)
    {
        var fields = rule.Split('|');
        string? Field(string key) => fields.FirstOrDefault(f => f.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..];
        var port = Field("LPort");
        var profiles = fields.Where(f => f.StartsWith("Profile=", StringComparison.OrdinalIgnoreCase)).Select(f => f[8..]).ToList();
        var networks = profiles.Count == 0 ? "all" : string.Join(", ", profiles);
        if (!string.Equals(Field("Active"), "TRUE", StringComparison.OrdinalIgnoreCase)) return "The rule exists but is off, so other PCs can't connect.";
        if (!string.Equals(Field("Action"), "Allow", StringComparison.OrdinalIgnoreCase)) return "The rule exists but blocks connections.";
        return port == null ? $"On for {networks} networks." : $"On. Allows port {port} on {networks} networks.";
    }

    void ShowSsh(SshState s)
    {
        _ssh = new List<FeatureRow>
        {
            new()
            {
                Title = "OpenSSH Server",
                Description = "Lets other computers sign in to this PC over SSH.",
                OnArgs = $"/Online /English /Add-Capability /CapabilityName:{s.ServerName} /NoRestart",
                OffArgs = $"/Online /English /Remove-Capability /CapabilityName:{s.ServerName} /NoRestart",
                OnVerb = "Install", OffVerb = "Remove", OnState = "Installed", OffState = "Not installed",
                OnEffect = "Windows downloads it from Windows Update. It adds the sshd service and a firewall rule for port 22. The service stays stopped until you start it on the Services page.",
                OffEffect = "Anyone signed in over SSH is cut off. The firewall rule stays behind, and a restart is wise if the service was running.",
                State = s.Server,
                Busy = _running,
            },
            new()
            {
                Title = "OpenSSH Client",
                Description = "The ssh, scp and sftp commands for connecting to other computers.",
                OnArgs = $"/Online /English /Add-Capability /CapabilityName:{s.ClientName} /NoRestart",
                OffArgs = $"/Online /English /Remove-Capability /CapabilityName:{s.ClientName} /NoRestart",
                OnVerb = "Install", OffVerb = "Remove", OnState = "Installed", OffState = "Not installed",
                OnEffect = "Windows downloads it from Windows Update.",
                OffEffect = "The ssh, scp and sftp commands go away, along with anything that relies on them.",
                State = s.Client,
                Busy = _running,
            },
        };
        SshList.ItemsSource = _ssh;
        ServiceText.Text = s.Service;
        FirewallText.Text = s.Firewall;
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_running || ((Button)sender).Tag is not FeatureRow row) return;
        bool turningOff = row.IsOn;
        var verb = turningOff ? row.OffVerb : row.OnVerb;
        var effect = turningOff ? row.OffEffect : row.OnEffect;
        if (effect.Length == 0) effect = turningOff
            ? "Apps that rely on it stop working. Windows keeps its files, so you can turn it back on."
            : "Windows adds it, and may download files from Windows Update.";
        var warning = row.HasWarning && !turningOff ? $"\n\n{row.Warning}" : "";
        if (Ui.Confirm($"{verb} {row.Title}?\n\n{effect}{warning}\n\nThis can take several minutes and may need a restart. It can't be stopped safely once it starts, so keep Tman open until it finishes."))
            Run(row, turningOff);
    }

    async void Run(FeatureRow row, bool turningOff)
    {
        SetBusy(true);
        var verb = turningOff ? row.OffVerb : row.OnVerb;
        ShowPanel();
        Panel.Start($"{verb} {row.Title}", null);
        var log = new StringBuilder();
        int code = await Task.Run(() => Cmd.StreamAsync(Cmd.System("dism.exe"), turningOff ? row.OffArgs : row.OnArgs, line =>
        {
            lock (log) log.AppendLine(line);
            Panel.Line(line);
        }));
        string text;
        lock (log) text = Regex.Replace(log.ToString(), @"\s+", " ");
        var done = turningOff
            ? $"{row.Title} was {(row.OffVerb == "Remove" ? "removed" : "turned off")}."
            : $"{row.Title} was {(row.OnVerb == "Install" ? "installed" : "turned on")}.";
        Panel.Finish(RepairJobs.ReadDismChange(done, $"{row.Title} wasn't changed.", code, text));
        if (code == 3010) MarkRestartPending(row);
        SetBusy(false);
        Load();
    }

    void ShowPanel()
    {
        Panel.Visibility = Visibility.Visible;
        PanelRow.Height = new GridLength(1, GridUnitType.Star);
    }

    void MarkRestartPending(FeatureRow row)
    {
        var known = Common.FirstOrDefault(k => k.Title == row.Title);
        _restartPending.Add(known?.StateName ?? row.Description);
    }

    void SetBusy(bool busy)
    {
        _running = busy;
        RefreshButton.IsEnabled = !busy;
        foreach (var r in _common.Concat(_other).Concat(_ssh)) r.Busy = busy;
    }
}
