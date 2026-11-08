using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using Tman.Monitoring;

namespace Tman.Core;

public enum Verdict { Allowed, Blocked, Asked }

/// <summary>One line of the history: a connection and what became of it.</summary>
public sealed class ConnectionRecord
{
    public DateTime Time { get; set; }
    public string App { get; set; } = "";
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string Protocol { get; set; } = "";
    public bool Inbound { get; set; }
    public Verdict Verdict { get; set; }
    /// <summary>What decided it: a rule, a blocklist, Windows Firewall, or nothing.</summary>
    public string Rule { get; set; } = "";

    [JsonIgnore] public string TimeText => Time.ToString("g");
    [JsonIgnore] public string VerdictText => Verdict.ToString();
    [JsonIgnore] public string DirectionText => Inbound ? "In" : "Out";
    [JsonIgnore] public string Destination => Host.Length > 0 ? Host : Ip;
    [JsonIgnore] public string PortText => Port > 0 ? Port.ToString() : "";
}

/// <summary>Where one app has been talking to, added up since Tman started watching.</summary>
public sealed class AppDestination
{
    public required string Key { get; init; }
    public required string Ip { get; init; }
    public int Port { get; init; }
    public required string Protocol { get; init; }
    public bool Inbound { get; init; }
    public string Host { get; set; } = "";
    public long Sent { get; set; }
    public long Received { get; set; }
    public int Count { get; set; }
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    public Verdict Verdict { get; set; }
    public string Rule { get; set; } = "";

    public AppDestination Clone() => (AppDestination)MemberwiseClone();
}

/// <summary>One program's network life since Tman started watching.</summary>
public sealed class AppActivity
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; set; }
    public int Connections { get; set; }
    public long Sent { get; set; }
    public long Received { get; set; }
    public int Blocked { get; set; }
    public int Asked { get; set; }
    public DateTime First { get; set; }
    public DateTime Last { get; set; }
    public Dictionary<string, AppDestination> Destinations { get; } = new();

    public AppActivity Clone()
    {
        var copy = new AppActivity { Path = Path, Name = Name, Icon = Icon, Connections = Connections, Sent = Sent, Received = Received, Blocked = Blocked, Asked = Asked, First = First, Last = Last };
        foreach (var (k, d) in Destinations) copy.Destinations[k] = d.Clone();
        return copy;
    }
}

public enum PromptChoice { Allow, Block, AllowOnce }

/// <summary>
/// The App firewall: watches every connection on the PC through two ETW providers (the kernel's network events for
/// who talked to where, and the DNS client's for which name they asked for), enforces the user's rules through the
/// Windows Filtering Platform, listens for the connections the engine refused, keeps a history, and asks about new
/// apps when prompt mode is on. One instance for the app, so it keeps working from the tray. Watching and enforcing
/// need administrator rights; as a standard user the page polls the connection tables instead.
/// </summary>
public sealed class AppFirewall
{
    public static AppFirewall Instance { get; } = new();

    static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    static readonly Guid DnsClient = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    static readonly Guid KernelProcess = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    const ulong KeywordProcess = 0x10;
    const ushort ProcessStart = 1, ProcessStop = 2;
    const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;
    const ushort DnsQueryCompleted = 3008;
    const int HistoryCap = 50_000, DnsCap = 20_000;
    const int PromptSeconds = 30;

    public static string HistoryPath => Path.Combine(AppFirewallStore.DataFolder, "app-firewall-history.json");

    public AppFirewallStore Store { get; } = AppFirewallStore.Load();

    readonly object _gate = new();
    readonly Dictionary<string, AppActivity> _apps = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ConnectionRecord> _history = new();
    readonly Dictionary<string, (string Name, DateTime Seen)> _dns = new();
    readonly Dictionary<int, Dictionary<string, string>> _dnsByPid = new();
    /// <summary>Process id to image. Entries from the process trace are trusted until the process ends; the rest are looked up again after 30 seconds.</summary>
    readonly Dictionary<int, (string Path, string Name, DateTime Seen, bool Traced)> _pids = new();
    readonly Dictionary<ulong, string> _filterLabels = new();
    readonly HashSet<string> _decided = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _prompting = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string?> _reverse = new();
    readonly SemaphoreSlim _reverseSlots = new(2);
    HashSet<IPAddress> _local = new();
    DateTime _localRead;
    bool _historyDirty;
    DateTime _historySaved = DateTime.Now;
    int _allowed, _blocked, _asked;

    EtwTrace? _trace;
    WfpEngine? _rules, _live;
    System.Diagnostics.Eventing.Reader.EventLogWatcher? _audit;
    volatile bool _monitoring;

    public BlocklistSet Blocklist { get; private set; } = BlocklistSet.Empty;
    /// <summary>Block, for this session only, the addresses that names on a blocklist resolve to. Catches subdomains the hosts file can't, at the risk of shared addresses.</summary>
    public bool LiveBlocklist { get; set; }

    /// <summary>True while the ETW trace is running.</summary>
    public bool Monitoring => _monitoring;
    /// <summary>True while rules are being enforced through the filter engine.</summary>
    public bool Enforcing => _rules != null;
    public string? MonitorError { get; private set; }
    public string? FilterError { get; private set; }
    public DateTime? Since { get; private set; }
    public int FiltersInEngine { get; private set; }
    /// <summary>Where Blocked verdicts come from: "the filter engine", "Windows security auditing", or null when nothing can report them.</summary>
    public string? DropSource { get; private set; }
    /// <summary>Why the filter engine's own record of refusals can't be read, when it can't.</summary>
    public string? DropError { get; private set; }
    /// <summary>True while the Security log is being watched for blocked connections.</summary>
    public bool Auditing => _audit != null;

    /// <summary>Raised on the UI thread when an app with no rule connects for the first time and prompt mode is on.</summary>
    public event Action<AppActivity, AppDestination>? Prompt;

    AppFirewall()
    {
        LoadHistory();
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            if (_rules != null) Task.Run(() => { try { ApplyRules(); } catch (Exception ex) { ErrorLog.Write(ex); } });
        };
        _ = Task.Run(ReloadBlocklist);
    }

    /// <summary>Called at startup: starts watching if the user asked for that and Tman is elevated.</summary>
    public void StartIfWanted()
    {
        if (MainWindow.IsAdmin && (Store.Settings.StartWithTman || Store.Settings.PromptMode)) Task.Run(() => Start());
        else if (MainWindow.IsAdmin && Store.Rules.Any(r => r.Enabled)) Task.Run(() => { try { ApplyRules(); } catch (Exception ex) { FilterError = ex.Message; } });
    }

    // ---- Starting and stopping ----

    /// <summary>Starts the trace and the filter engine. Returns null, or why it couldn't. Safe to call again.</summary>
    public string? Start()
    {
        lock (_gate)
        {
            if (_monitoring) return null;
            if (!MainWindow.IsAdmin) return MonitorError = "Watching connections needs administrator rights.";
            try
            {
                unsafe { _trace = EtwTrace.StartProvider("AppFirewall", KernelNetwork, KeywordIpv4 | KeywordIpv6, OnEvent); }
                _trace.Enable(DnsClient, ulong.MaxValue);
                // Short-lived processes are gone before their network events arrive, so their names come from the process trace.
                _trace.Enable(KernelProcess, KeywordProcess);
                SeedProcesses();
                _monitoring = true;
                MonitorError = null;
                Since = DateTime.Now;
            }
            catch (Exception ex)
            {
                _trace?.Stop();
                _trace = null;
                return MonitorError = ex is EtwFailure ? ex.Message : $"The trace didn't start: {ex.Message}";
            }
        }
        try
        {
            ApplyRules();
            OpenLive();
        }
        catch (Exception ex)
        {
            FilterError = ex.Message;
        }
        return null;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _monitoring = false;
            _trace?.Stop();
            _trace = null;
            _live?.Dispose();
            _live = null;
            _rules?.Dispose();
            _rules = null;
            _audit?.Dispose();
            _audit = null;
            DropSource = null;
            Since = null;
        }
        SaveHistory(force: true);
    }

    void OpenLive()
    {
        lock (_gate)
        {
            if (_live != null) return;
            var live = WfpEngine.Open(dynamic: true);
            try
            {
                live.EnsureDropEvents();
                live.SubscribeDrops(OnDrop);
                DropError = null;
                if (live.DropSource == "polling")
                {
                    // The subscription was refused; make sure the log can at least be read, or say so.
                    try { live.DropsSince(DateTime.UtcNow.ToFileTimeUtc()); }
                    catch (WfpFailure ex) { DropError = $"The filter engine's record of refused connections can't be read on this PC ({ex.Message}); netsh wfp show netevents fails the same way."; }
                }
                DropSource = DropError == null ? "the filter engine" : null;
            }
            catch (WfpFailure ex)
            {
                DropError = ex.Message;
                DropSource = null;
            }
            _live = live;
        }
        if (DropError != null) StartAuditIfOn();
    }

    /// <summary>Watches the Security log when Windows is already auditing blocked connections.</summary>
    void StartAuditIfOn()
    {
        lock (_gate)
        {
            if (_audit != null) return;
            try
            {
                if (BlockedAudit.IsOn() != true) return;
                _audit = BlockedAudit.Watch(OnDrop);
                DropSource = "Windows security auditing";
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
        }
    }

    /// <summary>Turns Windows' audit of blocked connections on or off and starts or stops reading it. Returns why it failed, or null.</summary>
    public string? SetAuditing(bool on)
    {
        var error = BlockedAudit.SetOn(on);
        if (error != null) return error;
        lock (_gate)
        {
            if (on)
            {
                try
                {
                    _audit ??= BlockedAudit.Watch(OnDrop);
                    DropSource = "Windows security auditing";
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }
            else
            {
                _audit?.Dispose();
                _audit = null;
                if (DropError != null) DropSource = null;
            }
        }
        return null;
    }

    // ---- Rules in the filter engine ----

    static readonly Guid[] OutLayers = { WfpEngine.LayerConnectV4, WfpEngine.LayerConnectV6 };
    static readonly Guid[] InLayers = { WfpEngine.LayerAcceptV4, WfpEngine.LayerAcceptV6 };
    static readonly Guid[] AllLayers = { WfpEngine.LayerConnectV4, WfpEngine.LayerConnectV6, WfpEngine.LayerAcceptV4, WfpEngine.LayerAcceptV6 };

    static bool IsV6(Guid layer) => layer == WfpEngine.LayerConnectV6 || layer == WfpEngine.LayerAcceptV6;
    static bool IsIn(Guid layer) => layer == WfpEngine.LayerAcceptV4 || layer == WfpEngine.LayerAcceptV6;

    static Guid KeyFor(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("Tman app firewall " + text));
        return new Guid(hash);
    }

    static byte WeightOf(AppRule rule)
    {
        if (rule.Force) return rule.Action == RuleAction.Block ? (byte)15 : (byte)13;
        int w = rule.Action == RuleAction.Block ? 2 : 8;
        if (rule.App != null) w += 2;
        if (!rule.CoversEverything) w += 2;
        return (byte)w;
    }

    /// <summary>The persistent filters a rule needs. Domain rules make none here; theirs come from DNS answers.</summary>
    public static List<WfpFilterSpec> FiltersFor(AppRule rule)
    {
        var list = new List<WfpFilterSpec>();
        if (!rule.Enabled || rule.IsDomainRule) return list;
        byte[]? appId = rule.App == null ? null : WfpEngine.AppId(rule.App);
        var layers = rule.Direction switch { RuleDirection.Outgoing => OutLayers, RuleDirection.Incoming => InLayers, _ => AllLayers };
        var addresses = AppRule.ParseAddresses(rule.Addresses);
        var ports = AppRule.ParsePorts(rule.Ports);
        byte? protocol = rule.Protocol switch { RuleProtocol.Tcp => 6, RuleProtocol.Udp => 17, _ => null };
        int index = 0;
        foreach (var layer in layers)
        {
            var forLayer = addresses.Count == 0
                ? new List<(IPAddress Address, int Prefix)?> { null }
                : addresses.Where(a => (a.Address.AddressFamily == AddressFamily.InterNetworkV6) == IsV6(layer)).Select(a => ((IPAddress Address, int Prefix)?)a).ToList();
            foreach (var address in forLayer)
                foreach (var port in ports.Count == 0 ? new List<(ushort, ushort)?> { null } : ports.Select(p => ((ushort, ushort)?)p))
                {
                    list.Add(new WfpFilterSpec(KeyFor($"{rule.Id}/{index++}"), "Tman: " + rule.Summary, rule.Id.ToString("D"), layer, rule.Action == RuleAction.Block, WeightOf(rule), Persistent: true)
                    {
                        AppId = appId,
                        RemoteAddress = address?.Address,
                        PrefixLength = address?.Prefix ?? -1,
                        RemotePort = port,
                        Protocol = protocol,
                    });
                }
        }
        return list;
    }

    /// <summary>
    /// Permits that keep the PC reachable whatever the rules say: loopback, DHCP and the DNS servers in use. A rule
    /// marked Force outweighs them. Windows Update has no fixed addresses, so it's protected by keeping its names out of
    /// the hosts file instead.
    /// </summary>
    static List<WfpFilterSpec> SafetyFilters()
    {
        var list = new List<WfpFilterSpec>();
        const byte weight = 14;
        void Add(string label, Guid layer, IPAddress? remote = null, int prefix = -1, (ushort, ushort)? remotePort = null, (ushort, ushort)? localPort = null, byte? protocol = null) =>
            list.Add(new WfpFilterSpec(KeyFor("safety/" + label + "/" + layer), "Tman: keep " + label.Split('/')[0], "safety", layer, Block: false, weight, Persistent: true)
            {
                RemoteAddress = remote, PrefixLength = prefix, RemotePort = remotePort, LocalPort = localPort, Protocol = protocol,
            });
        foreach (var layer in AllLayers)
            Add("loopback", layer, IsV6(layer) ? IPAddress.IPv6Loopback : IPAddress.Parse("127.0.0.0"), IsV6(layer) ? 128 : 8);
        Add("DHCP/out", WfpEngine.LayerConnectV4, remotePort: (67, 67), protocol: 17);
        Add("DHCP/in", WfpEngine.LayerAcceptV4, localPort: (68, 68), protocol: 17);
        Add("DHCP/out6", WfpEngine.LayerConnectV6, remotePort: (547, 547), protocol: 17);
        Add("DHCP/in6", WfpEngine.LayerAcceptV6, localPort: (546, 546), protocol: 17);
        foreach (var server in DnsServers())
        {
            var layer = server.AddressFamily == AddressFamily.InterNetworkV6 ? WfpEngine.LayerConnectV6 : WfpEngine.LayerConnectV4;
            Add($"DNS/{server}/udp", layer, server, -1, (53, 53), protocol: 17);
            Add($"DNS/{server}/tcp", layer, server, -1, (53, 53), protocol: 6);
        }
        return list;
    }

    static List<IPAddress> DnsServers()
    {
        var servers = new List<IPAddress>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in n.GetIPProperties().DnsAddresses)
                    if (!IPAddress.IsLoopback(a) && !servers.Contains(a)) servers.Add(a);
            }
        }
        catch { }
        return servers;
    }

    /// <summary>
    /// Makes the engine match the rules: adds filters that are missing, removes Tman's persistent filters that no rule
    /// wants any more. Filters are persistent, so the rules hold while Tman is closed. Returns a note about rules that
    /// couldn't be applied, or null.
    /// </summary>
    public string? ApplyRules()
    {
        if (!MainWindow.IsAdmin) return FilterError = "Rules can only be applied by an administrator.";
        lock (_gate)
        {
            try
            {
                _rules ??= WfpEngine.Open(dynamic: false);
                var problems = new List<string>();
                var wanted = new Dictionary<Guid, WfpFilterSpec>();
                var labels = new Dictionary<Guid, string>();
                foreach (var rule in Store.Rules.Where(r => r.Enabled))
                {
                    try
                    {
                        foreach (var spec in FiltersFor(rule))
                        {
                            wanted[spec.Key] = spec;
                            labels[spec.Key] = rule.Summary;
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{rule.Summary}: {ex.Message}");
                    }
                }
                // The permits that keep the PC reachable are only needed once there's a rule that could cut something off.
                if (wanted.Count > 0) foreach (var spec in SafetyFilters()) wanted[spec.Key] = spec;
                var existing = _rules.ListTmanFilters();
                foreach (var f in existing.Where(f => f.Persistent && !wanted.ContainsKey(f.Key))) _rules.DeleteFilter(f.Id);
                if (wanted.Count == 0)
                {
                    // No rules: leave nothing of Tman's in the engine, not even the empty provider and sublayer.
                    if (existing.All(f => f.Persistent)) _rules.DeleteProviderAndSubLayer();
                    FiltersInEngine = 0;
                    FilterError = problems.Count > 0 ? "Some rules couldn't be applied:\n" + string.Join("\n", problems) : null;
                    return FilterError;
                }
                _rules.EnsureProviderAndSubLayer();
                var present = existing.Where(f => f.Persistent).ToDictionary(f => f.Key, f => f.Id);
                foreach (var (key, spec) in wanted)
                {
                    if (!present.TryGetValue(key, out var id)) id = _rules.AddFilter(spec);
                    if (labels.TryGetValue(key, out var label)) _filterLabels[id] = label;
                }
                FiltersInEngine = _rules.ListTmanFilters().Count;
                FilterError = problems.Count > 0 ? "Some rules couldn't be applied:\n" + string.Join("\n", problems) : null;
                return FilterError;
            }
            catch (Exception ex)
            {
                FilterError = ex.Message;
                if (ex is WfpFailure { Status: 0x80070005 or 5 })
                {
                    _rules?.Dispose();
                    _rules = null;
                }
                throw;
            }
        }
    }

    /// <summary>Deletes every filter Tman ever added, with the sublayer and provider, and turns every rule off. Returns how many filters went.</summary>
    public int RemoveAllFilters()
    {
        lock (_gate)
        {
            _live?.Dispose();
            _live = null;
            _rules?.Dispose();
            _rules = null;
            using var engine = WfpEngine.Open(dynamic: false);
            int removed = engine.RemoveEverything();
            foreach (var rule in Store.Rules) rule.Enabled = false;
            Store.Save();
            _filterLabels.Clear();
            _dynamic.Clear();
            FiltersInEngine = 0;
            FilterError = null;
            if (_monitoring)
            {
                try { OpenLive(); } catch (Exception ex) { ErrorLog.Write(ex); }
            }
            return removed;
        }
    }

    /// <summary>Everything the engine holds for Tman right now. Needs admin.</summary>
    public List<WfpFilterInfo> FiltersNow()
    {
        using var engine = WfpEngine.Open(dynamic: false);
        return engine.ListTmanFilters();
    }

    public void AddRule(AppRule rule)
    {
        lock (_gate) Store.Rules.Add(rule);
        Store.Save();
        AfterRuleChange(rule);
    }

    public void UpdateRule(AppRule rule)
    {
        Store.Save();
        AfterRuleChange(rule);
    }

    public void RemoveRules(IEnumerable<AppRule> rules)
    {
        lock (_gate) foreach (var r in rules) Store.Rules.Remove(r);
        Store.Save();
        AfterRuleChange(null);
    }

    public void SetEnabled(IEnumerable<AppRule> rules, bool enabled)
    {
        foreach (var r in rules) r.Enabled = enabled;
        Store.Save();
        AfterRuleChange(null);
    }

    void AfterRuleChange(AppRule? rule)
    {
        if (!MainWindow.IsAdmin) return;
        try { ApplyRules(); }
        catch (Exception ex) { FilterError = ex.Message; }
        if (rule is { Enabled: true, IsDomainRule: true, Action: RuleAction.Block } && !rule.Domain.Contains('*'))
        {
            // Block what the name points at right now; later answers add more as they're seen.
            try
            {
                foreach (var ip in Dns.GetHostAddresses(rule.Domain)) BlockAddress(rule.App, ip, rule.Summary, WeightOf(rule));
            }
            catch { }
        }
    }

    // Filters made from DNS answers live in the dynamic session: one per app and address.
    readonly HashSet<string> _dynamic = new(StringComparer.OrdinalIgnoreCase);

    void BlockAddress(string? app, IPAddress ip, string label, byte weight)
    {
        if (_live == null || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return;
        var tag = $"{app}|{ip}";
        lock (_gate)
        {
            if (!_dynamic.Add(tag)) return;
            try
            {
                _live.EnsureProviderAndSubLayer();
                byte[]? appId = app == null ? null : WfpEngine.AppId(app);
                var layer = ip.AddressFamily == AddressFamily.InterNetworkV6 ? WfpEngine.LayerConnectV6 : WfpEngine.LayerConnectV4;
                ulong id = _live.AddFilter(new WfpFilterSpec(Guid.NewGuid(), "Tman: " + label, "dns", layer, Block: true, weight, Persistent: false) { AppId = appId, RemoteAddress = ip });
                _filterLabels[id] = label;
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
        }
    }

    // ---- The trace ----

    unsafe void OnEvent(EtwEvent* e)
    {
        if (e->ProviderId == DnsClient)
        {
            if (e->Id == DnsQueryCompleted) OnDns(e);
            return;
        }
        if (e->ProviderId == KernelProcess)
        {
            if (e->Id is ProcessStart or ProcessStop) OnProcess(e);
            return;
        }
        if (e->ProviderId != KernelNetwork || e->UserDataLength < 20) return;
        bool v6 = e->Id is 26 or 27 or 28 or 31 or 58 or 59;
        bool udp = e->Id is 42 or 43 or 58 or 59;
        bool connect = e->Id is 12 or 28, accept = e->Id is 15 or 31, send = e->Id is 10 or 26 or 42 or 58, recv = e->Id is 11 or 27 or 43 or 59;
        if (!(connect || accept || send || recv)) return;
        int addrLen = v6 ? 16 : 4;
        if (e->UserDataLength < 8 + 2 * addrLen + 4) return;
        byte* d = e->UserData;
        int pid = Unsafe.ReadUnaligned<int>(d);
        uint size = Unsafe.ReadUnaligned<uint>(d + 4);
        var daddr = new IPAddress(new ReadOnlySpan<byte>(d + 8, addrLen));
        var saddr = new IPAddress(new ReadOnlySpan<byte>(d + 8 + addrLen, addrLen));
        int dport = (d[8 + 2 * addrLen] << 8) | d[9 + 2 * addrLen];
        int sport = (d[10 + 2 * addrLen] << 8) | d[11 + 2 * addrLen];
        // Sent events name the remote end as the destination; for the rest, the remote end is whichever address isn't ours.
        var locals = LocalAddresses();
        bool dIsLocal = locals.Contains(daddr), sIsLocal = locals.Contains(saddr);
        IPAddress remote = daddr;
        int remotePort = dport;
        if (dIsLocal && !sIsLocal)
        {
            remote = saddr;
            remotePort = sport;
        }
        if (pid <= 0 || remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any)) return;
        Record(pid, remote, remotePort, udp ? "UDP" : "TCP", accept, connect || accept || udp, send ? size : 0, recv ? size : 0);
    }

    /// <summary>Process start and stop, in whichever layout this Windows writes (versions 0 to 4 differ before the image name).</summary>
    unsafe void OnProcess(EtwEvent* e)
    {
        var data = new ReadOnlySpan<byte>(e->UserData, e->UserDataLength);
        if (data.Length < 4) return;
        int pid = Unsafe.ReadUnaligned<int>(e->UserData);
        if (e->Id == ProcessStop)
        {
            lock (_gate)
            {
                // Kept a little longer: the last network events of a process arrive after it has gone.
                if (_pids.TryGetValue(pid, out var known) && known.Traced) _pids[pid] = (known.Path, known.Name, DateTime.Now.AddSeconds(60), false);
            }
            return;
        }
        int offset = e->Version switch
        {
            0 => 20,
            1 or 2 => 24,
            _ => 48,
        };
        if (e->Version >= 3)
        {
            // A SID sits before the image name: revision, count, authority, then 4 bytes per sub-authority.
            if (data.Length < offset + 8) return;
            offset += 8 + 4 * data[offset + 1];
        }
        if (offset >= data.Length) return;
        var image = ReadString(data[offset..], out _);
        if (string.IsNullOrEmpty(image)) return;
        var path = WfpEngine.DosPath(image);
        lock (_gate)
        {
            if (_pids.Count > 4000) foreach (var old in _pids.Where(kv => !kv.Value.Traced && DateTime.Now - kv.Value.Seen > TimeSpan.FromMinutes(5)).Select(kv => kv.Key).ToList()) _pids.Remove(old);
            _pids[pid] = (path, Path.GetFileName(path), DateTime.Now, true);
        }
    }

    /// <summary>Names the processes already running when the trace starts.</summary>
    void SeedProcesses()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id <= 4) continue;
                    var path = QueryPath(p.Id);
                    if (path == null) continue;
                    lock (_gate) _pids[p.Id] = (path, Path.GetFileName(path), DateTime.Now, true);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    static unsafe string? QueryPath(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            char* buf = stackalloc char[1024];
            int size = 1024;
            return Native.QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally { Native.CloseHandle(h); }
    }

    HashSet<IPAddress> LocalAddresses()
    {
        if (DateTime.Now - _localRead < TimeSpan.FromSeconds(30)) return _local;
        var set = new HashSet<IPAddress>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in n.GetIPProperties().UnicastAddresses) set.Add(a.Address);
        }
        catch { }
        set.Add(IPAddress.Loopback);
        set.Add(IPAddress.IPv6Loopback);
        _local = set;
        _localRead = DateTime.Now;
        return set;
    }

    unsafe void OnDns(EtwEvent* e)
    {
        var data = new ReadOnlySpan<byte>(e->UserData, e->UserDataLength);
        var name = ReadString(data, out int used);
        if (name == null) return;
        int pos = used + 4 + 8;
        if (pos + 4 > data.Length) return;
        uint status = Unsafe.ReadUnaligned<uint>(e->UserData + pos);
        pos += 4;
        var results = ReadString(data[pos..], out _) ?? "";
        name = name.TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || name.EndsWith(".in-addr.arpa") || name.EndsWith(".ip6.arpa") || status != 0 && results.Length == 0) return;
        int pid = (int)e->ProcessId;
        var addresses = new List<IPAddress>();
        bool sinkholed = false;
        foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("type:", StringComparison.OrdinalIgnoreCase) || !IPAddress.TryParse(part, out var ip)) continue;
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(ip) && !name.Equals("localhost")) sinkholed = true;
            else addresses.Add(ip);
        }
        lock (_gate)
        {
            if (_dns.Count > DnsCap) foreach (var old in _dns.Where(kv => DateTime.Now - kv.Value.Seen > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList()) _dns.Remove(old);
            if (!_dnsByPid.TryGetValue(pid, out var byPid)) _dnsByPid[pid] = byPid = new Dictionary<string, string>();
            if (byPid.Count > 2000) byPid.Clear();
            foreach (var ip in addresses)
            {
                var key = ip.ToString();
                _dns[key] = (name, DateTime.Now);
                byPid[key] = name;
            }
        }
        var (path, appName) = ResolvePid(pid);
        if (sinkholed && addresses.Count == 0)
        {
            var source = Blocklist.Blocks(name) is { } list ? $"Blocklist: {list}" : "Hosts file";
            AddHistory(new ConnectionRecord { Time = DateTime.Now, App = appName, Path = path, Host = name, Ip = "0.0.0.0", Port = 0, Protocol = "DNS", Verdict = Verdict.Blocked, Rule = source });
            lock (_gate) if (_apps.TryGetValue(path, out var app)) app.Blocked++;
            return;
        }
        // Domain rules: block what the name resolved to, for this app or for everyone.
        AppRule? rule;
        lock (_gate) rule = Store.Rules.FirstOrDefault(r => r.Enabled && r.Action == RuleAction.Block && r.IsDomainRule && r.AppliesTo(path) && r.MatchesDomain(name));
        if (rule != null) foreach (var ip in addresses) BlockAddress(rule.App, ip, rule.Summary, WeightOf(rule));
        else if (LiveBlocklist && Blocklist.Blocks(name) is { } list) foreach (var ip in addresses) BlockAddress(null, ip, $"Blocklist: {list} ({name})", 6);
    }

    static string? ReadString(ReadOnlySpan<byte> data, out int used)
    {
        used = 0;
        var chars = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(data);
        int end = chars.IndexOf('\0');
        if (end < 0) return null;
        used = (end + 1) * 2;
        return new string(chars[..end]);
    }

    // ---- Refused connections ----

    void OnDrop(WfpDrop drop)
    {
        string? label;
        lock (_gate) _filterLabels.TryGetValue(drop.FilterId, out label);
        // Windows Firewall refuses unsolicited inbound packets all day long; only its outbound refusals are worth a line.
        if (label == null && drop.Inbound) return;
        if (drop.AppPath == null || drop.Remote == null) return;
        var path = drop.AppPath;
        var name = Path.GetFileName(path);
        var protocol = drop.Protocol switch { 6 => "TCP", 17 => "UDP", 1 or 58 => "ICMP", _ => $"IP {drop.Protocol}" };
        var ip = drop.Remote.ToString();
        var host = HostFor(0, ip);
        var rule = label ?? "Windows Firewall";
        lock (_gate)
        {
            var app = GetApp(path, name);
            var key = $"{ip}|{drop.RemotePort}|{protocol}|{(drop.Inbound ? "in" : "out")}";
            if (!app.Destinations.TryGetValue(key, out var dest))
                app.Destinations[key] = dest = new AppDestination { Key = key, Ip = ip, Port = drop.RemotePort, Protocol = protocol, Inbound = drop.Inbound, First = drop.Time };
            dest.Count++;
            dest.Last = drop.Time;
            dest.Host = host ?? dest.Host;
            dest.Verdict = Verdict.Blocked;
            dest.Rule = rule;
            app.Blocked++;
            app.Last = drop.Time;
            _blocked++;
            if (label != null)
                foreach (var r in Store.Rules.Where(r => r.Summary == label)) r.Hits++;
        }
        AddHistory(new ConnectionRecord { Time = drop.Time, App = name, Path = path, Host = host ?? "", Ip = ip, Port = drop.RemotePort, Protocol = protocol, Inbound = drop.Inbound, Verdict = Verdict.Blocked, Rule = rule });
    }

    // ---- The model ----

    AppActivity GetApp(string path, string name)
    {
        if (!_apps.TryGetValue(path, out var app))
        {
            _apps[path] = app = new AppActivity { Path = path, Name = name, First = DateTime.Now };
            app.Icon = Icons.ForFile(File.Exists(path) ? path : null);
        }
        return app;
    }

    void Record(int pid, IPAddress remote, int port, string protocol, bool inbound, bool newConnection, uint sent, uint received)
    {
        var (path, name) = ResolvePid(pid);
        var ip = remote.ToString();
        var host = HostFor(pid, ip);
        bool firstForApp, firstForDest;
        AppActivity app;
        AppDestination dest;
        lock (_gate)
        {
            firstForApp = !_apps.ContainsKey(path);
            app = GetApp(path, name);
            var key = $"{ip}|{port}|{protocol}|{(inbound ? "in" : "out")}";
            firstForDest = !app.Destinations.TryGetValue(key, out var known);
            if (known == null)
                app.Destinations[key] = dest = new AppDestination { Key = key, Ip = ip, Port = port, Protocol = protocol, Inbound = inbound, First = DateTime.Now, Host = host ?? "" };
            else
            {
                dest = known;
                if (dest.Host.Length == 0 && host != null) dest.Host = host;
            }
            if (newConnection && (protocol == "TCP" || firstForDest))
            {
                dest.Count++;
                app.Connections++;
            }
            dest.Sent += sent;
            dest.Received += received;
            app.Sent += sent;
            app.Received += received;
            dest.Last = app.Last = DateTime.Now;
            if (!newConnection || protocol == "UDP" && !firstForDest) return;
            if (firstForApp && Store.Settings.PromptMode && _monitoring && !_decided.Contains(path) && !Store.Rules.Any(r => r.App != null && r.AppliesTo(path)))
            {
                dest.Verdict = Verdict.Asked;
                app.Asked++;
                _asked++;
                _prompting.Add(path);
            }
            else _allowed++;
        }
        if (host == null) QueueReverse(ip);
        AddHistory(new ConnectionRecord { Time = DateTime.Now, App = name, Path = path, Host = host ?? "", Ip = ip, Port = port, Protocol = protocol, Inbound = inbound, Verdict = dest.Verdict, Rule = "" });
        if (dest.Verdict == Verdict.Asked) Application.Current?.Dispatcher.BeginInvoke(() => Prompt?.Invoke(app.Clone(), dest.Clone()));
    }

    /// <summary>The user's answer to a prompt. Allow and Block make a rule for the app; Allow once just stops asking until Tman restarts.</summary>
    public void Decide(string path, PromptChoice choice)
    {
        lock (_gate)
        {
            _decided.Add(path);
            _prompting.Remove(path);
        }
        if (choice == PromptChoice.AllowOnce) return;
        AddRule(new AppRule { App = path, Action = choice == PromptChoice.Allow ? RuleAction.Allow : RuleAction.Block, Direction = RuleDirection.Outgoing, Note = "From a prompt" });
    }

    /// <summary>A prompt that was left alone: the app stays allowed and isn't asked about again this session.</summary>
    public void PromptTimedOut(string path) => Decide(path, PromptChoice.AllowOnce);

    string? HostFor(int pid, string ip)
    {
        lock (_gate)
        {
            if (pid > 0 && _dnsByPid.TryGetValue(pid, out var byPid) && byPid.TryGetValue(ip, out var name)) return name;
            if (_dns.TryGetValue(ip, out var entry)) return entry.Name;
        }
        return _reverse.TryGetValue(ip, out var reverse) ? reverse : null;
    }

    void QueueReverse(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || !_reverse.TryAdd(ip, null)) return;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || IsPrivate(address)) return;
        _ = Task.Run(async () =>
        {
            await _reverseSlots.WaitAsync();
            try
            {
                var lookup = Dns.GetHostEntryAsync(address);
                if (await Task.WhenAny(lookup, Task.Delay(4000)) != lookup) return;
                var host = lookup.Result.HostName;
                if (host == ip || host.Length == 0) return;
                _reverse[ip] = host;
                lock (_gate)
                    foreach (var app in _apps.Values)
                        foreach (var d in app.Destinations.Values)
                            if (d.Ip == ip && d.Host.Length == 0) d.Host = host;
            }
            catch { }
            finally { _reverseSlots.Release(); }
        });
    }

    static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] < 32 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] >= 224;
    }

    (string Path, string Name) ResolvePid(int pid)
    {
        lock (_gate)
        {
            if (_pids.TryGetValue(pid, out var known) && (known.Traced || DateTime.Now < known.Seen.AddSeconds(30))) return (known.Path, known.Name);
        }
        string? path = null, name = null;
        if (pid == 4)
        {
            path = Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe");
            name = "System";
        }
        else
        {
            var sample = SystemMonitor.Instance.Latest?.Processes.FirstOrDefault(p => p.Pid == pid);
            if (sample?.Details.Path is { Length: > 0 } p)
            {
                path = p;
                name = sample.Name;
            }
            else
            {
                path = QueryPath(pid);
                if (path != null) name = Path.GetFileName(path);
            }
        }
        bool found = path != null;
        path ??= $"PID {pid}";
        name ??= sampleName(pid) ?? path;
        lock (_gate)
        {
            if (_pids.Count > 4000) _pids.Clear();
            // A name that couldn't be found is kept only briefly, in case the process trace names it a moment later.
            _pids[pid] = (path, name, found ? DateTime.Now : DateTime.Now.AddSeconds(-25), false);
        }
        return (path, name);

        static string? sampleName(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
            catch { return null; }
        }
    }

    // ---- Reading it back ----

    public List<AppActivity> Apps()
    {
        lock (_gate) return _apps.Values.Select(a => a.Clone()).ToList();
    }

    public (int Allowed, int Blocked, int Asked, int Apps) Counters()
    {
        lock (_gate) return (_allowed, _blocked, _asked, _apps.Count);
    }

    /// <summary>Forgets what has been seen so far. Rules and history stay.</summary>
    public void ClearLive()
    {
        lock (_gate)
        {
            _apps.Clear();
            _allowed = _blocked = _asked = 0;
        }
    }

    /// <summary>The verdict a rule set gives an app as a whole: blocked outright, blocked in part, allowed, or being asked about.</summary>
    public string VerdictFor(AppActivity app)
    {
        lock (_gate)
        {
            if (_prompting.Contains(app.Path)) return "Asked";
            var rules = Store.Rules.Where(r => r.Enabled && r.App != null && r.AppliesTo(app.Path)).ToList();
            if (rules.Any(r => r.Action == RuleAction.Block && r.CoversEverything && r.Direction != RuleDirection.Incoming)) return "Blocked";
            if (rules.Any(r => r.Action == RuleAction.Block) || app.Blocked > 0) return "Partly blocked";
            if (rules.Any(r => r.Action == RuleAction.Allow)) return "Allowed (rule)";
            return "Allowed";
        }
    }

    /// <summary>
    /// For a standard user, who can't run the trace: reads the live connection tables and shows them as apps and
    /// destinations. Counts are what's open now, not totals, and there are no bytes or verdicts.
    /// </summary>
    public void RefreshFromTables()
    {
        var rows = Pages.ConnectionsPage.ReadAll();
        lock (_gate) _apps.Clear();
        foreach (var r in rows)
        {
            if (r.RemoteAddress.Length == 0 || r.RemotePort == 0 || r.Pid <= 0) continue;
            var (path, name) = ResolvePid(r.Pid);
            var protocol = r.Protocol.StartsWith("UDP") ? "UDP" : "TCP";
            var host = HostFor(r.Pid, r.RemoteAddress);
            lock (_gate)
            {
                var app = GetApp(path, name);
                var key = $"{r.RemoteAddress}|{r.RemotePort}|{protocol}|out";
                if (!app.Destinations.TryGetValue(key, out var dest))
                    app.Destinations[key] = dest = new AppDestination { Key = key, Ip = r.RemoteAddress, Port = r.RemotePort, Protocol = protocol, First = DateTime.Now, Host = host ?? "" };
                dest.Count++;
                app.Connections++;
                dest.Last = app.Last = DateTime.Now;
            }
            if (host == null) QueueReverse(r.RemoteAddress);
        }
    }

    // ---- History ----

    void AddHistory(ConnectionRecord record)
    {
        lock (_gate)
        {
            _history.Add(record);
            if (_history.Count > HistoryCap + 1000) _history.RemoveRange(0, _history.Count - HistoryCap);
            _historyDirty = true;
        }
        if (DateTime.Now - _historySaved > TimeSpan.FromMinutes(2)) Task.Run(() => SaveHistory(force: false));
    }

    public List<ConnectionRecord> History()
    {
        lock (_gate) return _history.ToList();
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            _historyDirty = true;
        }
        SaveHistory(force: true);
    }

    void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var records = JsonSerializer.Deserialize<List<ConnectionRecord>>(File.ReadAllText(HistoryPath), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }) ?? new();
            var cutoff = DateTime.Now.AddDays(-Store.Settings.HistoryDays);
            lock (_gate) _history.AddRange(records.Where(r => r.Time >= cutoff));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    public void SaveHistory(bool force)
    {
        List<ConnectionRecord> copy;
        lock (_gate)
        {
            if (!_historyDirty && !force) return;
            var cutoff = DateTime.Now.AddDays(-Store.Settings.HistoryDays);
            _history.RemoveAll(r => r.Time < cutoff);
            copy = _history.ToList();
            _historyDirty = false;
            _historySaved = DateTime.Now;
        }
        try
        {
            Directory.CreateDirectory(AppFirewallStore.DataFolder);
            var temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));
            File.Move(temp, HistoryPath, overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    /// <summary>Everything Tman should save on the way out.</summary>
    public void Save()
    {
        try { Store.Save(); } catch (Exception ex) { ErrorLog.Write(ex); }
        SaveHistory(force: false);
    }

    public static string ToCsv(IEnumerable<ConnectionRecord> records)
    {
        var sb = new StringBuilder("Time,App,Path,Host,IP,Port,Protocol,Direction,Verdict,Rule\r\n");
        foreach (var r in records)
            sb.Append(string.Join(',', new[] { r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.App, r.Path, r.Host, r.Ip, r.PortText, r.Protocol, r.Inbound ? "Inbound" : "Outbound", r.VerdictText, r.Rule }.Select(Csv))).Append("\r\n");
        return sb.ToString();
    }

    static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---- Blocklists ----

    public void ReloadBlocklist()
    {
        try { Blocklist = Blocklists.Merge(Store.Settings.BlocklistUrls, Store.Settings.DisabledLists); }
        catch (Exception ex) { ErrorLog.Write(ex); }
    }

    /// <summary>Downloads lists that are missing or older than a week, then rebuilds the merged set. Returns the problems by url.</summary>
    public async Task<Dictionary<string, string>> UpdateBlocklistsAsync(bool force)
    {
        var problems = await Blocklists.UpdateAsync(Store.Settings.BlocklistUrls, Store.Settings.DisabledLists, force).ConfigureAwait(false);
        ReloadBlocklist();
        return problems;
    }

    /// <summary>Writes the merged blocklist into the hosts file, through the same path the Hosts page uses, so a dated backup is made first. Returns how many names went in.</summary>
    public int WriteHostsBlock()
    {
        var file = HostsFile.Load(HostsFile.SystemPath);
        var allowed = Store.Rules.Where(r => r.Enabled && r.App == null && r.Action == RuleAction.Allow && r.IsDomainRule).ToList();
        int count = Blocklists.WriteToHosts(file, Blocklist.Domains, d => allowed.Any(r => r.MatchesDomain(d)));
        if (file.Dirty) file.Save(HostsFile.SystemPath, HostsFile.BackupFolder);
        NetworkConfig.FlushDns();
        Store.Settings.HostsBlockOn = count > 0;
        Store.Settings.HostsBlockWritten = DateTime.Now;
        Store.Settings.HostsBlockCount = count;
        Store.Save();
        return count;
    }

    public void RemoveHostsBlock()
    {
        var file = HostsFile.Load(HostsFile.SystemPath);
        Blocklists.RemoveFromHosts(file);
        if (file.Dirty) file.Save(HostsFile.SystemPath, HostsFile.BackupFolder);
        NetworkConfig.FlushDns();
        Store.Settings.HostsBlockOn = false;
        Store.Settings.HostsBlockCount = 0;
        Store.Save();
    }

    /// <summary>How many names the hosts file carries in Tman's section right now.</summary>
    public static int HostsBlockCountNow()
    {
        try { return Blocklists.CountInHosts(HostsFile.Load(HostsFile.SystemPath)); }
        catch { return 0; }
    }
}
