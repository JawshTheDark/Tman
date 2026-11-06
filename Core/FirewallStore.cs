using System.Runtime.InteropServices;
using System.Text;

namespace Tman.Core;

public sealed record FirewallProfile(int Type, string Name, bool Enabled, bool BlockInbound, bool BlockOutbound, bool Active)
{
    public string StateText => Enabled ? "On" : "Off";
    public string ActiveText => Active ? "Active" : "";
    public string ToggleText => Enabled ? "Turn off" : "Turn on";
    public string DefaultsText => $"In: {Word(BlockInbound)}   Out: {Word(BlockOutbound)}";
    public string DefaultsHint => $"Connections with no matching rule: inbound are {Word(BlockInbound).ToLowerInvariant()}ed, outbound are {Word(BlockOutbound).ToLowerInvariant()}ed.";
    static string Word(bool block) => block ? "Block" : "Allow";
}

public sealed class FirewallRule
{
    public required string Key { get; init; }
    public required string RawName { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public bool Inbound { get; init; }
    public bool Allow { get; init; }
    public bool Enabled { get; init; }
    public string Profiles { get; init; } = "";
    public string Program { get; init; } = "";
    public string Service { get; init; } = "";
    public bool Package { get; init; }
    public string Ports { get; init; } = "";
    public string Addresses { get; init; } = "";
    public string Group { get; init; } = "";
    public bool Microsoft { get; init; }

    public string DirectionText => Inbound ? "Inbound" : "Outbound";
    public string ActionText => Allow ? "Allow" : "Block";
    public string EnabledText => Enabled ? "Yes" : "No";
    public string ProgramName => Program.Length > 0 ? Path.GetFileName(Program) : Service.Length > 0 ? Service : Package ? "App package" : "Any";
    public string ProgramFull => Program.Length > 0 ? Program + (Service.Length > 0 ? $" (service {Service})" : "") : Service.Length > 0 ? $"Service {Service}" : Package ? "App package" : "Any program";
}

/// <summary>Windows Firewall through its COM policy object. Blocks, so call it off the UI thread.</summary>
public static class FirewallStore
{
    const int DirIn = 1, DirOut = 2, ActionBlock = 0, ActionAllow = 1, ProfileAll = 0x7FFFFFFF;

    /// <summary>Group given to rules Tman adds, so they can be found on the Firewall page.</summary>
    public const string TmanGroup = "Tman";

    /// <summary>MSIX desktop apps all share this group id.</summary>
    const string PackagedAppGroup = "{78E1CD88-49E3-476E-B926-580E596AD309}";

    static readonly (int Type, string Name)[] ProfileTypes = { (1, "Domain"), (2, "Private"), (4, "Public") };

    static dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException("Windows Firewall isn't available on this PC.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Firewall isn't available on this PC.");
    }

    public static List<FirewallProfile> ReadProfiles()
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        int active = (int)policy.CurrentProfileTypes;
        var list = new List<FirewallProfile>();
        foreach (var (type, name) in ProfileTypes)
            list.Add(new FirewallProfile(type, name, (bool)policy.FirewallEnabled[type],
                (int)policy.DefaultInboundAction[type] == ActionBlock, (int)policy.DefaultOutboundAction[type] == ActionBlock, (active & type) != 0));
        return list;
    }

    public static List<FirewallRule> ReadRules()
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        var strings = new Dictionary<string, string>();
        var list = new List<FirewallRule>();
        foreach (dynamic r in policy.Rules)
        {
            string rawName = Text(r.Name), rawGroup = Text(r.Grouping), program = Text(r.ApplicationName), service = Text(r.serviceName);
            int protocol = (int)r.Protocol;
            string local = Text(r.LocalAddresses), remote = Text(r.RemoteAddresses);
            string name = Resolve(rawName, strings);
            if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) && program.Length > 0) name = Path.GetFileNameWithoutExtension(program);
            list.Add(new FirewallRule
            {
                Key = KeyOf(r),
                RawName = rawName,
                Name = name,
                Description = Resolve(Text(r.Description), strings),
                Inbound = (int)r.Direction == DirIn,
                Allow = (int)r.Action == ActionAllow,
                Enabled = (bool)r.Enabled,
                Profiles = ProfileText((int)r.Profiles),
                Program = program,
                Service = service == "*" ? "" : service,
                Package = program.Length == 0 && Text(r.LocalUserOwner).Length > 0,
                Ports = PortText(protocol, Text(r.LocalPorts), Text(r.RemotePorts)),
                Addresses = AddressText(local, remote),
                Group = rawGroup == PackagedAppGroup ? "Packaged app" : Resolve(rawGroup, strings),
                Microsoft = IsMicrosoft(rawName, rawGroup, program),
            });
        }
        return list;
    }

    public static void SetEnabled(FirewallRule rule, bool enabled)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        // Identical copies of a rule share a key, so look for a copy that actually needs changing.
        bool found = false;
        foreach (dynamic r in policy.Rules)
        {
            if ((string)KeyOf(r) != rule.Key) continue;
            found = true;
            if ((bool)r.Enabled == enabled) continue;
            r.Enabled = enabled;
            return;
        }
        if (!found) throw new InvalidOperationException($"The rule \"{rule.Name}\" no longer exists. Refresh the list.");
    }

    public static void Delete(FirewallRule rule)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        dynamic live = Find(policy, rule);
        int sameName = 0;
        foreach (dynamic r in policy.Rules)
            if (Text(r.Name) == rule.RawName) sameName++;
        if (sameName <= 1)
        {
            policy.Rules.Remove(rule.RawName);
            return;
        }
        // Remove goes by name and many rules share one, so this rule gets a unique name first.
        string unique = "Tman delete " + Guid.NewGuid().ToString("N");
        live.Name = unique;
        try
        {
            policy.Rules.Remove(unique);
        }
        catch
        {
            live.Name = rule.RawName;
            throw;
        }
    }

    public static void SetProfileEnabled(int type, bool on)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        policy.FirewallEnabled[type] = on;
    }

    public static string BlockRuleName(string program) => $"Tman block {Path.GetFileNameWithoutExtension(program)}";

    /// <summary>Adds an outbound block rule for a program on every profile. Returns false if Tman's rule for it already exists.</summary>
    public static bool AddOutboundBlock(string program)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        string name = BlockRuleName(program);
        foreach (dynamic r in policy.Rules)
            if (Text(r.Name) == name && string.Equals(Text(r.ApplicationName), program, StringComparison.OrdinalIgnoreCase)) return false;
        policy.Rules.Add(NewBlockRule(program));
        return true;
    }

    static dynamic NewBlockRule(string program)
    {
        dynamic rule = Create("HNetCfg.FWRule");
        rule.Name = BlockRuleName(program);
        rule.Description = "Added by Tman from the Connections page. Delete this rule on the Firewall page to unblock the program.";
        rule.ApplicationName = program;
        rule.Direction = DirOut;
        rule.Action = ActionBlock;
        rule.Profiles = ProfileAll;
        rule.Grouping = TmanGroup;
        rule.Enabled = true;
        return rule;
    }

    // The same rule's key is built on reading and on finding it again, because names aren't unique.
    static string KeyOf(dynamic r) => string.Join('\u0001', new[]
    {
        Text(r.Name), ((int)r.Direction).ToString(), ((int)r.Action).ToString(), ((int)r.Profiles).ToString(), Text(r.ApplicationName), Text(r.serviceName),
        ((int)r.Protocol).ToString(), Text(r.LocalPorts), Text(r.RemotePorts), Text(r.LocalAddresses), Text(r.RemoteAddresses), Text(r.Grouping), Text(r.LocalUserOwner),
    });

    static dynamic Find(dynamic policy, FirewallRule rule)
    {
        foreach (dynamic r in policy.Rules)
            if ((string)KeyOf(r) == rule.Key) return r;
        throw new InvalidOperationException($"The rule \"{rule.Name}\" no longer exists. Refresh the list.");
    }

    static string Text(object? value) => value as string ?? "";

    static string ProfileText(int bits)
    {
        if ((bits & 7) == 7) return "All";
        var names = ProfileTypes.Where(p => (bits & p.Type) != 0).Select(p => p.Name).ToList();
        return names.Count > 0 ? string.Join(", ", names) : "None";
    }

    static string PortText(int protocol, string local, string remote)
    {
        string name = protocol switch { 6 => "TCP", 17 => "UDP", 1 => "ICMP", 58 => "ICMPv6", 256 => "Any", _ => $"Protocol {protocol}" };
        var parts = new List<string> { name };
        if (local.Length > 0 && local != "*") parts.Add(local);
        if (remote.Length > 0 && remote != "*") parts.Add("remote " + remote);
        return string.Join(' ', parts);
    }

    static string AddressText(string local, string remote)
    {
        bool anyLocal = local.Length == 0 || local == "*", anyRemote = remote.Length == 0 || remote == "*";
        if (anyLocal && anyRemote) return "";
        return string.Join(" · ", new[] { anyLocal ? "" : "Local " + local, anyRemote ? "" : "Remote " + remote }.Where(s => s.Length > 0));
    }

    /// <summary>Microsoft's two package publisher ids.</summary>
    static bool MicrosoftPublisher(string text) =>
        text.Contains("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase) || text.Contains("_cw5n1h2txyewy", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A rule's program decides this when it has one. Otherwise Windows' own rules give it away through a name or
    /// group stored as a resource string in a system DLL or a Microsoft package.
    /// </summary>
    static bool IsMicrosoft(string rawName, string rawGroup, string program)
    {
        if (program.Length > 0)
        {
            var path = Environment.ExpandEnvironmentVariables(program);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return string.Equals(path, "System", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)
                   || MicrosoftPublisher(path)
                   || new[] { @"\Microsoft\", @"\Microsoft ", @"\Microsoft.", @"\Windows Defender", @"\Windows Media Player\" }
                       .Any(part => path.Contains(part, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var raw in new[] { rawGroup, rawName })
        {
            if (raw.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("@{Microsoft", StringComparison.OrdinalIgnoreCase)) return true;
            if (!raw.StartsWith('@') || raw.StartsWith("@{")) continue;
            var file = Environment.ExpandEnvironmentVariables(raw[1..].Split(',')[0]);
            if (!file.Contains('\\') || file.StartsWith(Environment.SystemDirectory + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, StringBuilder output, uint size, IntPtr reserved);

    /// <summary>Turns an "@file.dll,-123" or "@{package?resource}" string into its text.</summary>
    static string Resolve(string raw, Dictionary<string, string> cache)
    {
        if (!raw.StartsWith('@')) return raw;
        if (cache.TryGetValue(raw, out var known)) return known;
        var text = new StringBuilder(512);
        string result = SHLoadIndirectString(raw, text, (uint)text.Capacity, IntPtr.Zero) == 0 && text.Length > 0 ? text.ToString() : Fallback(raw);
        return cache[raw] = result;
    }

    /// <summary>When a package string won't load (the app may be gone), its package name is still readable.</summary>
    static string Fallback(string raw)
    {
        if (!raw.StartsWith("@{")) return raw;
        int end = raw.IndexOf('_');
        return end > 2 ? raw[2..end] : raw;
    }
}
