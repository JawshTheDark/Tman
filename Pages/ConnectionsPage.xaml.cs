using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public sealed class ConnectionItem : ObservableObject
{
    public required string Key { get; init; }
    public int Pid { get; init; }
    public string Process { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public string? ExePath { get; set; }
    public required string Protocol { get; init; }
    public required string LocalAddress { get; init; }
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public string RemotePortText => RemotePort > 0 ? RemotePort.ToString() : "";

    string _state = "";
    public string State { get => _state; set => Set(ref _state, value); }

    string? _host;
    public string? Host { get => _host; set { if (Set(ref _host, value)) Raise(nameof(RemoteDisplay)); } }
    public string RemoteDisplay => string.IsNullOrEmpty(_host) ? RemoteAddress : $"{_host} ({RemoteAddress})";
}

public partial class ConnectionsPage : UserControl, IPage
{
    static readonly string[] TcpStates = { "", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1", "FIN wait 2", "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB" };

    readonly ObservableCollection<ConnectionItem> _items = new();
    readonly Dictionary<string, ConnectionItem> _map = new();
    readonly ICollectionView _view;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Dictionary<string, string?> _dns = new();
    string _filter = "";

    public ConnectionsPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(ConnectionItem.Process), ListSortDirection.Ascending));
        _view.Filter = Matches;
        Search.TextChanged += t =>
        {
            _filter = t.Trim();
            _view.Refresh();
            UpdateCount();
        };
        _timer.Tick += async (_, _) => await Refresh();
    }

    public async void OnShown()
    {
        _timer.Start();
        await Refresh();
    }

    public void OnHidden() => _timer.Stop();
    public void FocusSearch() => Search.FocusBox();

    bool Matches(object o)
    {
        var c = (ConnectionItem)o;
        switch (KindFilter?.SelectedIndex ?? 0)
        {
            case 1 when c.State != "Established": return false;
            case 2 when c.Protocol.StartsWith("TCP") && c.State != "Listening": return false; // UDP endpoints always count as listening
            case 3 when !c.Protocol.StartsWith("TCP"): return false;
            case 4 when !c.Protocol.StartsWith("UDP"): return false;
        }
        return _filter.Length == 0 || Ui.Contains(c.Process, _filter) || Ui.Contains(c.RemoteDisplay, _filter) || Ui.Contains(c.LocalAddress, _filter)
               || c.LocalPort.ToString() == _filter || c.RemotePortText == _filter || c.Pid.ToString() == _filter;
    }

    void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view == null) return;
        _view.Refresh();
        UpdateCount();
    }

    void UpdateCount()
    {
        var shown = _view.Cast<object>().Count();
        CountText.Text = shown == _items.Count ? $"{_items.Count} connections" : $"{shown} of {_items.Count} connections";
    }

    async Task Refresh()
    {
        var rows = await Task.Run(ReadAll);
        var procs = SystemMonitor.Instance.Latest?.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First()) ?? new();

        var seen = new HashSet<string>();
        foreach (var r in rows)
        {
            seen.Add(r.Key);
            if (_map.TryGetValue(r.Key, out var existing))
            {
                existing.State = r.State;
                continue;
            }
            if (procs.TryGetValue(r.Pid, out var p))
            {
                r.Process = p.Name;
                r.Icon = p.Details.Icon;
                r.ExePath = p.Details.Path;
            }
            else
            {
                r.Process = r.Pid == 0 ? "System Idle Process" : r.Pid == 4 ? "System" : $"PID {r.Pid}";
                r.Icon = Icons.DefaultExe;
            }
            _map[r.Key] = r;
            _items.Add(r);
            if (ResolveBox.IsChecked == true) Resolve(r);
        }
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(_items[i].Key)) continue;
            _map.Remove(_items[i].Key);
            _items.RemoveAt(i);
        }
        UpdateCount();
    }

    void Resolve_Click(object sender, RoutedEventArgs e)
    {
        var on = ResolveBox.IsChecked == true;
        foreach (var c in _items)
        {
            if (on) Resolve(c);
            else c.Host = null;
        }
    }

    async void Resolve(ConnectionItem c)
    {
        if (c.RemoteAddress.Length == 0 || c.RemoteAddress is "0.0.0.0" or "::" or "*") return;
        if (_dns.TryGetValue(c.RemoteAddress, out var cached))
        {
            c.Host = cached;
            return;
        }
        _dns[c.RemoteAddress] = null;
        try
        {
            var lookup = Dns.GetHostEntryAsync(c.RemoteAddress);
            if (await Task.WhenAny(lookup, Task.Delay(4000)) != lookup) return;
            var host = lookup.Result.HostName;
            if (host == c.RemoteAddress) return;
            _dns[c.RemoteAddress] = host;
            if (ResolveBox.IsChecked == true)
                foreach (var item in _items.Where(i => i.RemoteAddress == c.RemoteAddress)) item.Host = host;
        }
        catch { }
    }

    // ---- IP Helper tables ----

    const int AF_INET = 2, AF_INET6 = 23;
    const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;

    /// <summary>Every TCP and UDP endpoint Windows knows about right now, with the owning process. Also feeds the App firewall page for a standard user.</summary>
    internal static List<ConnectionItem> ReadAll()
    {
        var list = new List<ConnectionItem>();
        try
        {
            ReadTable(list, tcp: true, AF_INET);
            ReadTable(list, tcp: true, AF_INET6);
            ReadTable(list, tcp: false, AF_INET);
            ReadTable(list, tcp: false, AF_INET6);
        }
        catch { }
        return list;
    }

    static unsafe void ReadTable(List<ConnectionItem> list, bool tcp, int af)
    {
        int size = 0;
        if (tcp) Native.GetExtendedTcpTable(null, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        else Native.GetExtendedUdpTable(null, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buf = new byte[size + 4096];
            size = buf.Length;
            fixed (byte* p = buf)
            {
                uint r = tcp
                    ? Native.GetExtendedTcpTable(p, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                    : Native.GetExtendedUdpTable(p, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                if (r == 122) continue; // ERROR_INSUFFICIENT_BUFFER: table grew, retry
                if (r != 0) return;
                int n = *(int*)p;
                byte* row = p + 4;
                var proto = (tcp ? "TCP" : "UDP") + (af == AF_INET6 ? "v6" : "");
                for (int i = 0; i < n; i++)
                {
                    if (tcp && af == AF_INET)
                    {
                        var u = (uint*)row;
                        Add(list, proto, (int)u[5], V4(u[1]), Port(u[2]), u[0] == 2 ? "" : V4(u[3]), u[0] == 2 ? 0 : Port(u[4]), (int)u[0]);
                        row += 24;
                    }
                    else if (tcp)
                    {
                        var u = (uint*)row;
                        uint state = u[12];
                        Add(list, proto, (int)u[13], V6(row, u[4]), Port(u[5]), state == 2 ? "" : V6(row + 24, u[10]), state == 2 ? 0 : Port(u[11]), (int)state);
                        row += 56;
                    }
                    else if (af == AF_INET)
                    {
                        var u = (uint*)row;
                        Add(list, proto, (int)u[2], V4(u[0]), Port(u[1]), "", 0, 0);
                        row += 12;
                    }
                    else
                    {
                        var u = (uint*)row;
                        Add(list, proto, (int)u[6], V6(row, u[4]), Port(u[5]), "", 0, 0);
                        row += 28;
                    }
                }
                return;
            }
        }
    }

    static void Add(List<ConnectionItem> list, string proto, int pid, string local, int localPort, string remote, int remotePort, int state)
    {
        list.Add(new ConnectionItem
        {
            Key = $"{proto}|{pid}|{local}|{localPort}|{remote}|{remotePort}",
            Pid = pid,
            Protocol = proto,
            LocalAddress = local,
            LocalPort = localPort,
            RemoteAddress = remote,
            RemotePort = remotePort,
            State = proto.StartsWith("UDP") ? "" : state > 0 && state < TcpStates.Length ? TcpStates[state] : state.ToString(),
        });
    }

    static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    static string V4(uint addr) => new IPAddress(addr).ToString();

    static unsafe string V6(byte* addr, uint scope)
    {
        var ip = new IPAddress(new ReadOnlySpan<byte>(addr, 16), scope);
        return ip.Equals(IPAddress.IPv6Any) ? "::" : ip.ToString();
    }

    // ---- Actions ----

    List<ConnectionItem> Selected => List.SelectedItems.Cast<ConnectionItem>().ToList();

    void EndProcess_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected.GroupBy(c => c.Pid).Select(g => g.First()).Where(c => c.Pid > 4).ToList();
        if (targets.Count == 0) return;
        if (!Ui.Confirm($"End {string.Join(", ", targets.Select(t => $"{t.Process} ({t.Pid})"))}? Unsaved data in it will be lost.")) return;
        var errors = new List<string>();
        foreach (var t in targets)
            if (!ProcessActions.Terminate(t.Pid, out var err)) errors.Add($"{t.Process} ({t.Pid}): {err}");
        if (errors.Count > 0) Ui.Error("Couldn't end:\n\n" + string.Join("\n", errors));
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e) => Ui.OpenFileLocation(Selected.FirstOrDefault()?.ExePath);

    async void Block_Click(object sender, RoutedEventArgs e)
    {
        var rows = Selected;
        if (rows.Count == 0) return;
        if (!MainWindow.IsAdmin)
        {
            Ui.Error("Blocking a program in the firewall needs administrator rights. Use File > Restart as administrator.");
            return;
        }
        var programs = rows.Select(c => c.ExePath).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (programs.Count == 0)
        {
            Ui.Error($"Tman can't tell which program file {rows[0].Process} runs from, so it can't make a rule for it.");
            return;
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        var risk = programs.Any(p => p.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            ? "\n\nThis is part of Windows. Blocking it can break Windows features such as updates, sign-in or name lookups." : "";
        var list = string.Join("\n", programs.Select(p => $"{p}\nRule name: {FirewallStore.BlockRuleName(p)}"));
        if (!Ui.Confirm($"Block all outgoing traffic from this program on every network?\n\n{list}\n\nIt won't be able to reach the internet or your network until you delete the rule on the Firewall page. Connections already open may keep working for a while.{risk}", "Block in firewall")) return;
        var result = await Task.Run(() =>
        {
            var errors = new List<string>();
            var existing = new List<string>();
            foreach (var p in programs)
            {
                try
                {
                    if (!FirewallStore.AddOutboundBlock(p)) existing.Add(p);
                }
                catch (Exception ex)
                {
                    errors.Add($"{p}: {ex.Message}");
                }
            }
            return (errors, existing);
        });
        if (result.errors.Count > 0) Ui.Error("Couldn't add the firewall rule:\n\n" + string.Join("\n", result.errors));
        if (result.existing.Count > 0) Ui.Error("Tman has already added a rule for this. If the program still gets through, check that the rule is enabled on the Firewall page:\n\n" + string.Join("\n", result.existing));
    }

    void CopyRemote_Click(object sender, RoutedEventArgs e) =>
        Ui.Copy(string.Join(Environment.NewLine, Selected.Where(c => c.RemoteAddress.Length > 0).Select(c => $"{c.RemoteAddress}:{c.RemotePort}")));

    void CopyRow_Click(object sender, RoutedEventArgs e) =>
        Ui.Copy(string.Join(Environment.NewLine, Selected.Select(c =>
            $"{c.Process}\t{c.Pid}\t{c.Protocol}\t{c.LocalAddress}:{c.LocalPort}\t{c.RemoteDisplay}{(c.RemotePort > 0 ? ":" + c.RemotePort : "")}\t{c.State}")));
}
