using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tman.Core;

public enum RuleAction { Block, Allow }
public enum RuleDirection { Outgoing, Incoming, Both }
public enum RuleProtocol { Any, Tcp, Udp }

/// <summary>
/// One App firewall rule: what to do with traffic of one app (or every app) to a domain, some addresses, some ports,
/// in one direction. A rule with no domain, addresses or ports covers all of the app's traffic. Saved as JSON.
/// </summary>
public sealed class AppRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Full path of the program, or null for every program.</summary>
    public string? App { get; set; }
    public RuleAction Action { get; set; } = RuleAction.Block;
    public RuleDirection Direction { get; set; } = RuleDirection.Outgoing;
    /// <summary>A name, which also covers its subdomains, or a pattern with * such as *.ads.example.com or *tracker*. Empty for none.</summary>
    public string Domain { get; set; } = "";
    /// <summary>IP addresses or ranges in CIDR form, separated by commas or spaces. Empty for any.</summary>
    public string Addresses { get; set; } = "";
    /// <summary>Remote ports such as "80, 443, 6000-6100". Empty for any.</summary>
    public string Ports { get; set; } = "";
    public RuleProtocol Protocol { get; set; } = RuleProtocol.Any;
    /// <summary>Also applies to loopback, DHCP and the DNS servers, which Tman otherwise always lets through.</summary>
    public bool Force { get; set; }
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    /// <summary>Connections the filter engine refused because of this rule, since it was made.</summary>
    public long Hits { get; set; }

    [JsonIgnore] public string AppName => App == null ? "All apps" : Path.GetFileName(App);
    [JsonIgnore] public string ActionText => Action == RuleAction.Block ? "Block" : "Allow";
    [JsonIgnore] public string DirectionText => Direction switch { RuleDirection.Outgoing => "Outgoing", RuleDirection.Incoming => "Incoming", _ => "Both" };
    [JsonIgnore] public string EnabledText => Enabled ? "Yes" : "No";
    [JsonIgnore] public bool IsDomainRule => Domain.Length > 0;
    [JsonIgnore] public bool CoversEverything => Domain.Length == 0 && Addresses.Length == 0 && Ports.Length == 0 && Protocol == RuleProtocol.Any;

    [JsonIgnore]
    public string TargetText
    {
        get
        {
            var parts = new List<string>();
            if (Domain.Length > 0) parts.Add(Domain);
            if (Addresses.Length > 0) parts.Add(Addresses);
            if (Ports.Length > 0) parts.Add("port " + Ports);
            if (Protocol != RuleProtocol.Any) parts.Add(Protocol == RuleProtocol.Tcp ? "TCP" : "UDP");
            return parts.Count == 0 ? "All traffic" : string.Join(" · ", parts);
        }
    }

    /// <summary>One line naming the rule, for confirmations and filter names.</summary>
    [JsonIgnore] public string Summary => $"{ActionText} {AppName}: {TargetText.ToLowerInvariant()}, {DirectionText.ToLowerInvariant()}";

    public bool AppliesTo(string? appPath) => App == null || (appPath != null && string.Equals(App, appPath, StringComparison.OrdinalIgnoreCase));

    public bool MatchesDomain(string name) => Domain.Length > 0 && DomainMatches(Domain, name);

    /// <summary>A plain name matches itself and its subdomains; a pattern with * is a glob over the whole name.</summary>
    public static bool DomainMatches(string pattern, string name)
    {
        name = name.TrimEnd('.');
        pattern = pattern.Trim().TrimEnd('.');
        if (pattern.Length == 0 || name.Length == 0) return false;
        if (!pattern.Contains('*'))
            return name.Equals(pattern, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static List<(IPAddress Address, int Prefix)> ParseAddresses(string text)
    {
        var list = new List<(IPAddress, int)>();
        foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = part.IndexOf('/');
            var ipText = slash > 0 ? part[..slash] : part;
            if (!IPAddress.TryParse(ipText, out var ip) || ipText.Contains('%')) throw new FormatException($"\"{part}\" isn't an IP address or range. Use something like 203.0.113.5 or 203.0.113.0/24.");
            int bits = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            int prefix = bits;
            if (slash > 0 && (!int.TryParse(part[(slash + 1)..], out prefix) || prefix < 0 || prefix > bits))
                throw new FormatException($"\"{part}\" has a bad range length. Use /0 to /{bits}.");
            list.Add((ip, prefix));
        }
        return list;
    }

    public static List<(ushort Low, ushort High)> ParsePorts(string text)
    {
        var list = new List<(ushort, ushort)>();
        foreach (var part in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (!ushort.TryParse(part[..dash], out var low) || !ushort.TryParse(part[(dash + 1)..], out var high) || low > high)
                    throw new FormatException($"\"{part}\" isn't a port range. Use something like 6000-6100.");
                list.Add((low, high));
            }
            else if (ushort.TryParse(part, out var port)) list.Add((port, port));
            else throw new FormatException($"\"{part}\" isn't a port number.");
        }
        return list;
    }

    /// <summary>Why this rule can't be saved, or null if it can.</summary>
    public string? Validate()
    {
        if (App != null && (App.Length == 0 || App.IndexOfAny(Path.GetInvalidPathChars()) >= 0)) return "Choose a program file, or All apps.";
        if (Domain.Length > 0)
        {
            var plain = Domain.Replace("*", "a");
            if (Domain.Contains("://") || Domain.Contains('/') || !HostsFile.IsHostName(plain)) return $"\"{Domain}\" isn't a domain name or pattern. Use something like ads.example.com or *.example.com.";
        }
        try
        {
            ParseAddresses(Addresses);
            ParsePorts(Ports);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
        if (App == null && Action == RuleAction.Block && CoversEverything) return "A rule that blocks all traffic of every app would cut this PC off. Name a program, a domain, an address or a port.";
        if (Direction == RuleDirection.Incoming && Domain.Length > 0) return "Incoming connections have no domain. Use addresses or ports for them.";
        return null;
    }
}

/// <summary>Page settings that aren't the main settings file's business.</summary>
public sealed class AppFirewallSettings
{
    /// <summary>Start the connection monitor as soon as Tman starts (as administrator), so history and prompts work from the tray.</summary>
    public bool StartWithTman { get; set; }
    /// <summary>Ask about each app the first time it connects and has no rule.</summary>
    public bool PromptMode { get; set; }
    public int HistoryDays { get; set; } = 7;
    public List<string> BlocklistUrls { get; set; } = Blocklists.DefaultUrls.ToList();
    /// <summary>Lists in <see cref="BlocklistUrls"/> that are kept but not used.</summary>
    public List<string> DisabledLists { get; set; } = new();
    /// <summary>The merged blocklist is written into the hosts file, so it works with Tman closed.</summary>
    public bool HostsBlockOn { get; set; }
    public DateTime? HostsBlockWritten { get; set; }
    public int HostsBlockCount { get; set; }
}

/// <summary>Rules and settings, in %APPDATA%\Tman\app-firewall.json. Load once; save after each change.</summary>
public sealed class AppFirewallStore
{
    public List<AppRule> Rules { get; set; } = new();
    public AppFirewallSettings Settings { get; set; } = new();

    public static string DataFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman");
    public static string FilePath => Path.Combine(DataFolder, "app-firewall.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppFirewallStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var store = JsonSerializer.Deserialize<AppFirewallStore>(File.ReadAllText(FilePath), Json) ?? new AppFirewallStore();
                store.Rules.RemoveAll(r => r.Validate() != null);
                if (store.Settings.BlocklistUrls.Count == 0) store.Settings.BlocklistUrls = Blocklists.DefaultUrls.ToList();
                store.Settings.HistoryDays = Math.Clamp(store.Settings.HistoryDays, 1, 365);
                return store;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
        return new AppFirewallStore();
    }

    public void Save()
    {
        lock (this)
        {
            Directory.CreateDirectory(DataFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
    }
}
