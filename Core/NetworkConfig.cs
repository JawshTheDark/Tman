using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tman.Core;

public sealed record DnsProvider(string Name, string[] V4, string[] V6)
{
    public override string ToString() => Name;
}

public sealed record AdapterInfo(string Id, string Name, string Description, string Status, bool Up, long Speed, string Ip, string Gateway, string Mac,
    List<string> DnsV4, List<string> DnsV6, List<string> StaticV4, List<string> StaticV6, int IndexV4, int IndexV6)
{
    public string SpeedText => Up && Speed > 0 ? Fmt.Bits(Speed / 8.0) : "";
    public string DnsText => string.Join(", ", DnsV4.Concat(DnsV6));
    public override string ToString() => Name;
}

public sealed record WifiProfile(string Name, string Security);

/// <summary>Network adapter details and the few settings Tman can change. Everything blocks, so call it off the UI thread.</summary>
public static class NetworkConfig
{
    public static readonly DnsProvider Automatic = new("Automatic (from the network)", Array.Empty<string>(), Array.Empty<string>());
    public static readonly DnsProvider Custom = new("Custom", Array.Empty<string>(), Array.Empty<string>());

    public static readonly DnsProvider[] Providers =
    {
        Automatic,
        new("Cloudflare", new[] { "1.1.1.1", "1.0.0.1" }, new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" }),
        new("Google", new[] { "8.8.8.8", "8.8.4.4" }, new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" }),
        new("Quad9", new[] { "9.9.9.9", "149.112.112.112" }, new[] { "2620:fe::fe", "2620:fe::9" }),
        Custom,
    };

    /// <summary>The adapters Network Connections lists: no loopback, tunnels, Wi-Fi Direct or the filter drivers stacked on real adapters.</summary>
    public static List<AdapterInfo> ReadAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) && !n.Name.Contains('*'))
        .Select(Read)
        .Where(a => a.IndexV4 > 0 || a.IndexV6 > 0)
        .OrderByDescending(a => a.Up)
        .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    static AdapterInfo Read(NetworkInterface n)
    {
        var p = n.GetIPProperties();
        var ips = p.UnicastAddresses.Select(a => a.Address)
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(a => a.ToString());
        var gateways = p.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.ToString());
        return new AdapterInfo(n.Id, n.Name, n.Description, StatusText(n.OperationalStatus), n.OperationalStatus == OperationalStatus.Up, n.Speed,
            string.Join(", ", ips), string.Join(", ", gateways), MacText(n),
            p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
            p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.ToString()).ToList(),
            StaticDns(n.Id, v6: false), StaticDns(n.Id, v6: true), Index(p, v6: false), Index(p, v6: true));
    }

    static int Index(IPInterfaceProperties p, bool v6)
    {
        // These throw when the protocol is turned off for the adapter.
        try { return v6 ? p.GetIPv6Properties()?.Index ?? 0 : p.GetIPv4Properties()?.Index ?? 0; }
        catch { return 0; }
    }

    static string StatusText(OperationalStatus s) => s switch
    {
        OperationalStatus.Up => "Connected",
        OperationalStatus.Down => "Disconnected",
        OperationalStatus.NotPresent => "Not present",
        OperationalStatus.LowerLayerDown => "Disconnected",
        _ => s.ToString(),
    };

    static string MacText(NetworkInterface n)
    {
        var bytes = n.GetPhysicalAddress().GetAddressBytes();
        return bytes.Length == 0 ? "None" : string.Join('-', bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>The servers set by hand for an adapter; an empty list means they come from the network.</summary>
    static List<string> StaticDns(string id, bool v6)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{(v6 ? "Tcpip6" : "Tcpip")}\Parameters\Interfaces\{id}");
            return key?.GetValue("NameServer") is string s
                ? s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Names the DNS setting: Automatic, the provider it matches, or Custom.</summary>
    public static DnsProvider CurrentProvider(AdapterInfo a)
    {
        if (a.StaticV4.Count == 0 && a.StaticV6.Count == 0) return Automatic;
        return Providers.FirstOrDefault(p => p.V4.Length > 0
            && a.StaticV4.All(s => p.V4.Contains(s, StringComparer.OrdinalIgnoreCase))
            && a.StaticV6.All(s => p.V6.Contains(s, StringComparer.OrdinalIgnoreCase))) ?? Custom;
    }

    /// <summary>netsh commands that set one address family. No servers means back to automatic.</summary>
    public static List<string> DnsCommands(int index, bool v6, IReadOnlyList<string> servers)
    {
        var family = v6 ? "ipv6" : "ipv4";
        if (servers.Count == 0) return new List<string> { $"interface {family} set dnsservers name={index} source=dhcp" };
        var commands = new List<string> { $"interface {family} set dnsservers name={index} source=static address={servers[0]} register=primary validate=no" };
        for (int i = 1; i < servers.Count; i++)
            commands.Add($"interface {family} add dnsservers name={index} address={servers[i]} index={i + 1} validate=no");
        return commands;
    }

    /// <summary>Applies DNS servers to an adapter. Returns why it failed, or null.</summary>
    public static string? SetDns(AdapterInfo a, IReadOnlyList<string> v4, IReadOnlyList<string> v6)
    {
        var commands = new List<string>();
        if (a.IndexV4 > 0) commands.AddRange(DnsCommands(a.IndexV4, false, v4));
        if (a.IndexV6 > 0) commands.AddRange(DnsCommands(a.IndexV6, true, v6));
        foreach (var command in commands)
        {
            var result = Cmd.Run(Cmd.System("netsh.exe"), command);
            if (!result.Ok) return result.Output.Trim();
        }
        return null;
    }

    /// <summary>Returns why the flush failed, or null.</summary>
    public static string? FlushDns()
    {
        var result = Cmd.Run(Cmd.System("ipconfig.exe"), "/flushdns");
        return result.Ok ? null : result.Output.Trim();
    }

    /// <summary>Turns an adapter off and on again. Returns why it failed, or null.</summary>
    public static string? RestartAdapter(AdapterInfo a)
    {
        try
        {
            if (Wmi.Query($"SELECT DeviceID FROM Win32_NetworkAdapter WHERE GUID='{a.Id}'").FirstOrDefault() is not ManagementObject adapter)
                return "Windows didn't find this adapter.";
            uint off = Convert.ToUInt32(adapter.InvokeMethod("Disable", null));
            if (off != 0) return $"Windows couldn't turn it off (error {off}). The adapter wasn't changed.";
            Thread.Sleep(1500);
            uint on = Convert.ToUInt32(adapter.InvokeMethod("Enable", null));
            return on == 0 ? null : $"The adapter was turned off but Windows couldn't turn it back on (error {on}). Enable it in Network Connections (ncpa.cpl).";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Saved Wi-Fi networks, or a sentence saying why there are none. Passwords are never asked for.</summary>
    public static (List<WifiProfile> Profiles, string? Note) ReadWifi()
    {
        var hasAdapter = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        var netsh = Cmd.System("netsh.exe");
        var list = Cmd.Run(netsh, "wlan show profiles");
        var names = ParseProfileNames(list.Output);
        if (names.Count == 0)
        {
            if (!hasAdapter) return (new(), "This PC has no Wi-Fi adapter, so there are no saved Wi-Fi networks.");
            return (new(), list.ExitCode == Cmd.FailedToStart || list.Output.Contains("wlansvc", StringComparison.OrdinalIgnoreCase)
                ? "Windows couldn't list Wi-Fi networks: " + list.Output.Trim()
                : "There are no saved Wi-Fi networks on this PC.");
        }
        var profiles = new List<WifiProfile>();
        foreach (var name in names)
        {
            // A name with a quote can't be passed to netsh safely, so it shows without its security type.
            var security = name.Contains('"') ? "" : ParseSecurity(Cmd.Run(netsh, $"wlan show profile name=\"{name}\"").Output);
            profiles.Add(new WifiProfile(name, security.Length > 0 ? security : "Unknown"));
        }
        return (profiles, null);
    }

    /// <summary>Profile lines are indented "label : name". The label is translated, so only the shape is used.</summary>
    public static List<string> ParseProfileNames(string output)
    {
        var names = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            int at = line.IndexOf(" : ", StringComparison.Ordinal);
            if (at > 0 && char.IsWhiteSpace(line[0]) && line[(at + 3)..].Trim().Length > 0) names.Add(line[(at + 3)..].Trim());
        }
        return names;
    }

    /// <summary>Security values are the same in every language, unlike their labels.</summary>
    public static string ParseSecurity(string output)
    {
        var found = Regex.Matches(output, @"^\s*\S.*?\s:\s(Open|Shared|WEP|WPA\S*|Wi-Fi Enhanced Open\S*|802\.1X)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", found);
    }
}
