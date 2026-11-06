using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tman.Core;

namespace Tman.Pages;

public sealed record PingRow(int Sequence, string Time, string From, string Rtt, string Ttl, string Result);

public sealed record LookupRow(string Type, string Address, string Reverse);

public sealed class TraceHop : ObservableObject
{
    public int Hop { get; init; }

    string _address = "";
    public string Address { get => _address; set => Set(ref _address, value); }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    string _times = "";
    public string Times { get => _times; set => Set(ref _times, value); }
}

public partial class NetworkToolsPage : UserControl, IPage
{
    const int MaxPingRows = 500, MaxHops = 30;
    static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2), HopTimeout = TimeSpan.FromSeconds(1.5);
    static readonly byte[] Payload = new byte[32];

    readonly ObservableCollection<PingRow> _pings = new();
    readonly ObservableCollection<TraceHop> _hops = new();
    CancellationTokenSource? _pingStop, _traceStop;
    bool _adaptersLoaded, _wifiLoaded;

    public NetworkToolsPage()
    {
        InitializeComponent();
        foreach (var grid in new[] { PingList, TraceList, LookupList, AdapterList, WifiList }) Ui.SetupGrid(grid);
        PingList.ItemsSource = _pings;
        TraceList.ItemsSource = _hops;
        PingHost.Text = TraceHost.Text = "1.1.1.1";
        DnsProviderBox.ItemsSource = NetworkConfig.Providers;
        Tabs.SelectionChanged += Tabs_SelectionChanged;
    }

    public void OnHidden()
    {
        _pingStop?.Cancel();
        _traceStop?.Cancel();
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        if (Tabs.SelectedItem == AdaptersTab || Tabs.SelectedItem == DnsTab)
        {
            if (!_adaptersLoaded) LoadAdapters();
        }
        else if (Tabs.SelectedItem == WifiTab && !_wifiLoaded)
        {
            LoadWifi();
        }
    }

    static async Task<IPAddress> Resolve(string host, CancellationToken cancel)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host, cancel);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
    }

    static string Plain(Exception ex, string host) =>
        ex is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }
            ? $"Couldn't find \"{host}\". Check the spelling, or look it up on the DNS lookup tab."
            : $"Couldn't reach \"{host}\": {ex.InnerException?.Message ?? ex.Message}";

    // ---- Ping ----

    void PingHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _pingStop == null) Ping_Click(sender, e);
    }

    async void Ping_Click(object sender, RoutedEventArgs e)
    {
        if (_pingStop != null)
        {
            _pingStop.Cancel();
            return;
        }
        var host = PingHost.Text.Trim();
        if (host.Length == 0) return;
        using var stop = _pingStop = new CancellationTokenSource();
        PingButton.Content = "Stop";
        _pings.Clear();
        PingStats.Text = "";
        PingMeaning.Text = $"Pinging {host}…";
        int sent = 0;
        var times = new List<long>();
        try
        {
            var address = await Resolve(host, stop.Token);
            using var ping = new Ping();
            while (true)
            {
                var watch = Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(address, PingTimeout, Payload, null, stop.Token);
                sent++;
                bool ok = reply.Status == IPStatus.Success;
                if (ok) times.Add(reply.RoundtripTime);
                if (_pings.Count == MaxPingRows) _pings.RemoveAt(0);
                _pings.Add(new PingRow(sent, DateTime.Now.ToString("T"), ok ? reply.Address.ToString() : "", ok ? Ms(reply.RoundtripTime) : "", ok ? reply.Options?.Ttl.ToString() ?? "" : "", StatusText(reply.Status)));
                PingList.ScrollIntoView(_pings[^1]);
                ShowPingStats(host, sent, times);
                var wait = TimeSpan.FromSeconds(1) - watch.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (sent == 0) PingMeaning.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            PingMeaning.Text = Plain(ex, host);
        }
        _pingStop = null;
        PingButton.Content = "Start";
    }

    static string Ms(long ms) => ms < 1 ? "under 1 ms" : $"{ms} ms";

    void ShowPingStats(string host, int sent, List<long> times)
    {
        int lost = sent - times.Count;
        PingStats.Text = $"Sent {sent} · Received {times.Count} · Lost {lost} ({lost * 100 / sent}%)"
                         + (times.Count > 0 ? $" · Lowest {Ms(times.Min())} · Average {Ms((long)times.Average())} · Highest {Ms(times.Max())}" : "");
        PingMeaning.Text = lost == 0 ? $"Every reply came back, so {host} can be reached."
            : times.Count == 0 ? "No replies. The host may be down, or it may be set to ignore ping."
            : "Some replies were lost, which points to a poor connection somewhere on the way.";
    }

    static string StatusText(IPStatus status) => status switch
    {
        IPStatus.Success => "Reply",
        IPStatus.TimedOut => "Timed out",
        IPStatus.DestinationHostUnreachable => "Host unreachable",
        IPStatus.DestinationNetworkUnreachable => "Network unreachable",
        IPStatus.TtlExpired => "Too many hops",
        _ => status.ToString(),
    };

    // ---- Traceroute ----

    void TraceHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _traceStop == null) Trace_Click(sender, e);
    }

    async void Trace_Click(object sender, RoutedEventArgs e)
    {
        if (_traceStop != null)
        {
            _traceStop.Cancel();
            return;
        }
        var host = TraceHost.Text.Trim();
        if (host.Length == 0) return;
        using var stop = _traceStop = new CancellationTokenSource();
        TraceButton.Content = "Stop";
        _hops.Clear();
        TraceSummary.Text = $"Tracing the route to {host}…";
        try
        {
            var address = await Resolve(host, stop.Token);
            using var ping = new Ping();
            int reached = 0;
            for (int ttl = 1; ttl <= MaxHops && reached == 0; ttl++)
            {
                var hop = new TraceHop { Hop = ttl };
                _hops.Add(hop);
                TraceList.ScrollIntoView(hop);
                var times = new List<string>();
                for (int probe = 0; probe < 3; probe++)
                {
                    var watch = Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(address, HopTimeout, Payload, new PingOptions(ttl, false), stop.Token);
                    bool answered = reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded;
                    times.Add(!answered ? "*" : watch.ElapsedMilliseconds < 1 ? "<1 ms" : $"{watch.ElapsedMilliseconds} ms");
                    hop.Times = string.Join("  ", times);
                    if (answered && hop.Address.Length == 0)
                    {
                        hop.Address = reply.Address.ToString();
                        FillHopName(hop, reply.Address);
                    }
                    if (reply.Status == IPStatus.Success) reached = ttl;
                }
                if (hop.Address.Length == 0) hop.Address = "No reply";
            }
            TraceSummary.Text = reached > 0
                ? $"Reached {host} in {reached} {(reached == 1 ? "hop" : "hops")}. Each hop is a router on the way. A * means it didn't answer, which is normal for many routers."
                : $"Didn't reach {host} within {MaxHops} hops. It may be blocking these probes even if it's working.";
        }
        catch (OperationCanceledException)
        {
            TraceSummary.Text = "Stopped.";
        }
        catch (Exception ex)
        {
            TraceSummary.Text = Plain(ex, host);
        }
        _traceStop = null;
        TraceButton.Content = "Start";
    }

    static async void FillHopName(TraceHop hop, IPAddress address) => hop.Name = await ReverseName(address) ?? "";

    static async Task<string?> ReverseName(IPAddress address)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address);
            if (await Task.WhenAny(lookup, Task.Delay(3000)) != lookup) return null;
            var name = lookup.Result.HostName;
            return name == address.ToString() ? null : name;
        }
        catch
        {
            return null;
        }
    }

    // ---- DNS lookup ----

    void LookupHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LookupButton.IsEnabled) Lookup_Click(sender, e);
    }

    async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        var name = LookupHost.Text.Trim();
        if (name.Length == 0) return;
        LookupButton.IsEnabled = false;
        LookupList.ItemsSource = null;
        LookupSummary.Text = $"Looking up {name}…";
        if (IPAddress.TryParse(name, out var ip))
        {
            var reverse = await ReverseName(ip);
            LookupList.ItemsSource = new[] { new LookupRow("Reverse", ip.ToString(), reverse ?? "No name found") };
            LookupSummary.Text = reverse != null ? $"{ip} belongs to {reverse}." : $"No name is published for {ip}, which is common for home and private addresses.";
        }
        else
        {
            var v4 = await Addresses(name, AddressFamily.InterNetwork);
            var v6 = await Addresses(name, AddressFamily.InterNetworkV6);
            var rows = new List<LookupRow>();
            foreach (var (type, list) in new[] { ("A", v4), ("AAAA", v6) })
                foreach (var address in list)
                    rows.Add(new LookupRow(type, address.ToString(), await ReverseName(address) ?? "None"));
            LookupList.ItemsSource = rows;
            LookupSummary.Text = rows.Count == 0
                ? $"No addresses found for {name}. Check the spelling. If it should exist, the name or your DNS may be down."
                : $"{name} has {v4.Length} IPv4 and {v6.Length} IPv6 {(rows.Count == 1 ? "address" : "addresses")}. Answers come through Windows, so the hosts file and the DNS cache count too.";
        }
        LookupButton.IsEnabled = true;
    }

    static async Task<IPAddress[]> Addresses(string name, AddressFamily family)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await Dns.GetHostAddressesAsync(name, family, timeout.Token);
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    // ---- Adapters ----

    async void LoadAdapters()
    {
        _adaptersLoaded = true;
        var selected = (AdapterList.SelectedItem as AdapterInfo)?.Id;
        var dnsSelected = (DnsAdapter.SelectedItem as AdapterInfo)?.Id;
        try
        {
            var adapters = await Task.Run(NetworkConfig.ReadAdapters);
            AdapterList.ItemsSource = adapters;
            AdapterList.SelectedItem = adapters.FirstOrDefault(a => a.Id == selected);
            DnsAdapter.ItemsSource = adapters;
            DnsAdapter.SelectedItem = adapters.FirstOrDefault(a => a.Id == dnsSelected) ?? adapters.FirstOrDefault(a => a.Up) ?? adapters.FirstOrDefault();
            AdapterNote.Text = adapters.Count == 0 ? "Windows reports no network adapters." : "";
        }
        catch (Exception ex)
        {
            AdapterNote.Text = $"Can't read the network adapters: {ex.Message}";
        }
    }

    void RefreshAdapters_Click(object sender, RoutedEventArgs e) => LoadAdapters();

    void AdapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var a = AdapterList.SelectedItem as AdapterInfo;
        RestartButton.IsEnabled = a != null;
        GatewayButton.IsEnabled = a != null && a.Gateway.Length > 0;
        AdapterDetails.Visibility = a != null ? Visibility.Visible : Visibility.Collapsed;
        if (a == null) return;
        AdapterTitle.ToolTip = AdapterTitle.Text = a.Description;
        AdapterMac.Text = $"MAC address {a.Mac}";
        AdapterAddresses.ToolTip = AdapterAddresses.Text = a.Ip.Length > 0 ? a.Ip : "No IP address";
    }

    async void PingGateway_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterList.SelectedItem is not AdapterInfo { Gateway.Length: > 0 } a) return;
        _pingStop?.Cancel();
        while (_pingStop != null) await Task.Delay(50);
        PingHost.Text = a.Gateway.Split(',')[0].Trim();
        Tabs.SelectedIndex = 0;
        Ping_Click(sender, e);
    }

    async void Flush_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Flush the DNS cache?\n\nWindows forgets the names it has looked up, so the next visit to each site takes a moment longer. Nothing else changes.")) return;
        var error = await Task.Run(NetworkConfig.FlushDns);
        if (error != null) Ui.Error($"Couldn't flush the DNS cache:\n\n{error}");
        else AdapterNote.Text = "DNS cache flushed.";
    }

    async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterList.SelectedItem is not AdapterInfo a) return;
        if (!MainWindow.IsAdmin)
        {
            Ui.Error("Restarting an adapter needs administrator rights. Use File > Restart as administrator.");
            return;
        }
        if (!Ui.Confirm($"Restart {a.Name}?\n\nThe adapter is turned off and back on, so this connection drops for a few seconds. Downloads, calls and remote sessions on it will be cut off, and anyone connected to this PC remotely can lose access.")) return;
        RestartButton.IsEnabled = false;
        AdapterNote.Text = $"Restarting {a.Name}…";
        var error = await Task.Run(() => NetworkConfig.RestartAdapter(a));
        if (error != null) Ui.Error($"Couldn't restart {a.Name}:\n\n{error}");
        AdapterNote.Text = error == null ? $"{a.Name} restarted." : "";
        RestartButton.IsEnabled = true;
        LoadAdapters();
    }

    // ---- DNS provider ----

    void DnsAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DnsApply.IsEnabled = DnsAdapter.SelectedItem != null;
        if (DnsAdapter.SelectedItem is not AdapterInfo a) return;
        var current = NetworkConfig.CurrentProvider(a);
        DnsCurrent.Text = a.StaticV4.Count + a.StaticV6.Count == 0 ? current.Name : $"{current.Name}: {string.Join(", ", a.StaticV4.Concat(a.StaticV6))}";
        DnsInUse.Text = a.DnsText.Length > 0 ? a.DnsText : "None";
        if (ReferenceEquals(DnsProviderBox.SelectedItem, current)) ShowProvider();
        else DnsProviderBox.SelectedItem = current;
    }

    void DnsProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowProvider();

    void ShowProvider()
    {
        if (DnsProviderBox.SelectedItem is not DnsProvider p) return;
        bool custom = p == NetworkConfig.Custom;
        var adapter = DnsAdapter.SelectedItem as AdapterInfo;
        var v4 = custom ? adapter?.StaticV4 ?? new List<string>() : p.V4.ToList();
        var v6 = custom ? adapter?.StaticV6 ?? new List<string>() : p.V6.ToList();
        V4One.Text = v4.ElementAtOrDefault(0) ?? "";
        V4Two.Text = v4.ElementAtOrDefault(1) ?? "";
        V6One.Text = v6.ElementAtOrDefault(0) ?? "";
        V6Two.Text = v6.ElementAtOrDefault(1) ?? "";
        foreach (var box in new[] { V4One, V4Two, V6One, V6Two })
        {
            box.IsReadOnly = !custom;
            box.IsEnabled = p != NetworkConfig.Automatic;
        }
        DnsHint.Text = p == NetworkConfig.Automatic ? "Your router or network decides, which is how Windows starts out."
            : custom ? "Enter a preferred and an alternate server. Left empty, an address family stays automatic."
            : "A public DNS service. Names that only your router knows may stop working.";
    }

    static List<string>? Servers(TextBox one, TextBox two, AddressFamily family)
    {
        var servers = new[] { one.Text.Trim(), two.Text.Trim() }.Where(s => s.Length > 0).ToList();
        return servers.All(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == family) ? servers : null;
    }

    async void DnsApply_Click(object sender, RoutedEventArgs e)
    {
        if (DnsAdapter.SelectedItem is not AdapterInfo a || DnsProviderBox.SelectedItem is not DnsProvider p) return;
        if (!MainWindow.IsAdmin)
        {
            Ui.Error("Changing DNS needs administrator rights. Use File > Restart as administrator.");
            return;
        }
        var v4 = Servers(V4One, V4Two, AddressFamily.InterNetwork);
        var v6 = Servers(V6One, V6Two, AddressFamily.InterNetworkV6);
        if (v4 == null || v6 == null)
        {
            Ui.Error($"That isn't a valid {(v4 == null ? "IPv4" : "IPv6")} address.");
            return;
        }
        if (p == NetworkConfig.Custom && v4.Count + v6.Count == 0)
        {
            Ui.Error("Enter at least one DNS server, or choose Automatic.");
            return;
        }
        var details = p == NetworkConfig.Automatic
            ? "Windows will go back to the DNS servers your network gives it."
            : $"IPv4: {(v4.Count > 0 ? string.Join(", ", v4) : "automatic")}\nIPv6: {(v6.Count > 0 ? string.Join(", ", v6) : "automatic")}\n\nEvery name this PC looks up on this adapter goes to these servers. If they can't be reached, websites stop loading until you switch back to Automatic.";
        if (!Ui.Confirm($"Change DNS for {a.Name} to {p.Name}?\n\n{details}", "DNS provider")) return;
        DnsApply.IsEnabled = false;
        var error = await Task.Run(() =>
        {
            var failed = NetworkConfig.SetDns(a, v4, v6);
            if (failed == null) NetworkConfig.FlushDns();
            return failed;
        });
        DnsApply.IsEnabled = true;
        if (error != null) Ui.Error($"Couldn't change DNS for {a.Name}:\n\n{error}");
        LoadAdapters();
    }

    // ---- Wi-Fi ----

    async void LoadWifi()
    {
        _wifiLoaded = true;
        WifiNote.Text = "Reading saved Wi-Fi networks…";
        var (profiles, note) = await Task.Run(NetworkConfig.ReadWifi);
        WifiList.ItemsSource = profiles;
        WifiNote.Text = note ?? $"{profiles.Count} saved {(profiles.Count == 1 ? "network" : "networks")}. Tman never reads their passwords.";
    }
}
