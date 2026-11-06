using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tman.Core;

/// <summary>A published port as the map labels it. Open ones can be reached from other devices.</summary>
public sealed record MapPort(string Text, bool Open);

public sealed record MapContainer(string Name, string Image, bool Running, string Stack, string? Lock, List<string> Networks, List<MapPort> Ports);

public sealed record MapNetwork(string Name, string Driver, string Subnet, bool Internal, string Stack)
{
    /// <summary>Docker's own networks. Containers can't be moved on or off host and none.</summary>
    public bool Fixed => Name is "host" or "none";
    public bool BuiltIn => Fixed || Name == "bridge";
}

/// <summary>What's connected to what, as Docker reports it now.</summary>
public sealed record DockerMap(List<MapContainer> Containers, List<MapNetwork> Networks)
{
    public static readonly DockerMap Empty = new(new(), new());

    /// <summary>A short summary, so the drawing is only rebuilt when something on it changed.</summary>
    public string Signature => string.Join('|', Containers.Select(c => $"{c.Name}:{c.Running}:{string.Join(',', c.Networks)}:{string.Join(',', c.Ports.Select(p => p.Text + p.Open))}"))
        + "#" + string.Join('|', Networks.Select(n => $"{n.Name}:{n.Subnet}:{n.Internal}"));
}

public sealed record NewNetwork(string Name, string Subnet, string Gateway, bool Internal)
{
    public static readonly Regex NameText = new(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.Compiled);
    static readonly Regex Cidr = new(@"^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$", RegexOptions.Compiled);
    static readonly Regex Address = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled);

    public string[] CreateArgs()
    {
        var args = new List<string> { "network", "create" };
        if (Internal) args.Add("--internal");
        if (Subnet.Trim().Length > 0) args.AddRange(new[] { "--subnet", Subnet.Trim() });
        if (Gateway.Trim().Length > 0) args.AddRange(new[] { "--gateway", Gateway.Trim() });
        args.Add(Name.Trim());
        return args.ToArray();
    }

    public List<string> Problems(IEnumerable<string> taken)
    {
        var problems = new List<string>();
        if (!NameText.IsMatch(Name.Trim())) problems.Add("The name can use letters, numbers, dots, dashes and underscores, and must start with a letter or number.");
        else if (taken.Contains(Name.Trim(), StringComparer.OrdinalIgnoreCase)) problems.Add($"There's already a network called {Name.Trim()}.");
        if (Subnet.Trim().Length > 0 && !Cidr.IsMatch(Subnet.Trim())) problems.Add("The address range should look like 172.30.0.0/24.");
        if (Gateway.Trim().Length > 0 && !Address.IsMatch(Gateway.Trim())) problems.Add("The gateway should look like 172.30.0.1.");
        if (Gateway.Trim().Length > 0 && Subnet.Trim().Length == 0) problems.Add("A gateway needs an address range to sit in.");
        return problems;
    }
}

/// <summary>
/// Changes drawn on the network map but not made yet. Nothing here touches Docker. The map shows these as dashed lines,
/// the page shows the commands they'd run, and only Apply runs them.
/// </summary>
public sealed class NetworkDraft
{
    readonly HashSet<(string Container, string Network)> _connect = new(), _disconnect = new();
    readonly List<NewNetwork> _created = new();
    readonly HashSet<string> _removed = new();

    public bool IsEmpty => _connect.Count == 0 && _disconnect.Count == 0 && _created.Count == 0 && _removed.Count == 0;
    public IReadOnlyCollection<(string Container, string Network)> Connecting => _connect;
    public IReadOnlyCollection<(string Container, string Network)> Disconnecting => _disconnect;
    public IReadOnlyList<NewNetwork> Created => _created;
    public IReadOnlyCollection<string> Removed => _removed;
    public bool HasLosses => _disconnect.Count > 0 || _removed.Count > 0;

    public string Signature => string.Join('|', _connect.Order()) + "#" + string.Join('|', _disconnect.Order()) + "#" + string.Join('|', _created.Select(c => c.Name)) + "#" + string.Join('|', _removed.Order());

    public void Clear()
    {
        _connect.Clear();
        _disconnect.Clear();
        _created.Clear();
        _removed.Clear();
    }

    /// <summary>Drawing a line where one was just struck out brings it back, rather than making two opposite changes.</summary>
    public void Connect(string container, string network)
    {
        if (!_disconnect.Remove((container, network))) _connect.Add((container, network));
    }

    public void Disconnect(string container, string network)
    {
        if (!_connect.Remove((container, network))) _disconnect.Add((container, network));
    }

    public void Create(NewNetwork network) => _created.Add(network);

    /// <summary>Docker won't remove a network that's in use, so its containers come off it first.</summary>
    public void Remove(MapNetwork network, IEnumerable<MapContainer> containers)
    {
        foreach (var c in containers.Where(c => c.Networks.Contains(network.Name))) _disconnect.Add((c.Name, network.Name));
        _connect.RemoveWhere(l => l.Network == network.Name);
        _removed.Add(network.Name);
    }

    /// <summary>A network that was only drawn just goes away, along with its lines.</summary>
    public void Forget(string network)
    {
        _created.RemoveAll(n => n.Name == network);
        _connect.RemoveWhere(l => l.Network == network);
    }

    public void Restore(string network)
    {
        _removed.Remove(network);
        _disconnect.RemoveWhere(l => l.Network == network);
    }

    /// <summary>Drops anything Docker has since made true or impossible, such as a change to a container that's gone.</summary>
    public void Prune(DockerMap map)
    {
        bool Has(string c, string n) => map.Containers.Any(x => x.Name == c && x.Networks.Contains(n));
        var networks = map.Networks.Select(n => n.Name).Concat(_created.Select(n => n.Name)).ToHashSet();
        _connect.RemoveWhere(l => Has(l.Container, l.Network) || !map.Containers.Any(c => c.Name == l.Container) || !networks.Contains(l.Network));
        _disconnect.RemoveWhere(l => !Has(l.Container, l.Network));
        _created.RemoveAll(n => map.Networks.Any(m => m.Name == n.Name));
        _removed.RemoveWhere(n => !map.Networks.Any(m => m.Name == n));
    }

    /// <summary>Connections come before disconnections, so a container moving between networks is never left on none.</summary>
    public List<string[]> Commands()
    {
        var commands = new List<string[]>();
        commands.AddRange(_created.Select(n => n.CreateArgs()));
        commands.AddRange(_connect.OrderBy(l => l.Container).ThenBy(l => l.Network).Select(l => new[] { "network", "connect", l.Network, l.Container }));
        commands.AddRange(_disconnect.OrderBy(l => l.Container).ThenBy(l => l.Network).Select(l => new[] { "network", "disconnect", l.Network, l.Container }));
        commands.AddRange(_removed.Order().Select(n => new[] { "network", "rm", n }));
        return commands;
    }

    public string Preview() => string.Join(Environment.NewLine, Commands().Select(c => "docker " + string.Join(" ", c.Select(DockerTemplates.Quote))));

    public List<string> Warnings(DockerMap map)
    {
        var warnings = new List<string>();
        foreach (var c in map.Containers)
        {
            var left = c.Networks.Count(n => !_disconnect.Contains((c.Name, n))) + _connect.Count(l => l.Container == c.Name);
            if (c.Networks.Count > 0 && left == 0) warnings.Add($"{c.Name} will have no network at all.");
            bool touched = _disconnect.Any(l => l.Container == c.Name) || _connect.Any(l => l.Container == c.Name);
            if (touched && c.Stack.Length > 0) warnings.Add($"{c.Name} belongs to the stack {c.Stack}, so this only lasts until the stack is next changed. Its Compose file is what keeps it.");
        }
        foreach (var n in map.Networks.Where(n => _removed.Contains(n.Name) && n.Stack.Length > 0))
            warnings.Add($"{n.Name} belongs to the stack {n.Stack}, which makes it again the next time it's changed.");
        return warnings;
    }

    /// <summary>What the confirmation has to name, because these take effect straight away.</summary>
    public string LossText()
    {
        var lines = new List<string>();
        foreach (var (container, network) in _disconnect.OrderBy(l => l.Container))
            lines.Add($"{container} loses its connection to {network} straight away.");
        foreach (var name in _removed.Order()) lines.Add($"The network {name} is deleted. No files are lost.");
        return string.Join("\n", lines);
    }
}

public static class DockerMapReader
{
    /// <summary>What the map draws. Docker's built-in networks only appear when something uses them, except the default one.</summary>
    public static async Task<DockerMap> ReadAsync()
    {
        var ids = (await DockerCli.ContainersAsync().ConfigureAwait(false)).Items.Select(c => c.Id).ToList();
        var details = await DockerCli.InspectAsync(ids).ConfigureAwait(false);
        var containers = details.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new MapContainer(c.Name, c.Image, c.Running, c.Stack, c.NetworkLock, Attached(c, details), c.PublishedPorts())).ToList();

        var networks = new List<MapNetwork>();
        foreach (var n in await DockerCli.InspectNetworksAsync().ConfigureAwait(false))
        {
            var ipam = DockerJson.Get(n, "IPAM");
            var subnet = DockerJson.Items(ipam, "Config").Select(c => DockerJson.Text(c, "Subnet")).FirstOrDefault(s => s.Length > 0) ?? "";
            var stack = DockerJson.Fields(n, "Labels").Where(l => l.Name == "com.docker.compose.project").Select(l => l.Value.ToString()).FirstOrDefault() ?? "";
            var network = new MapNetwork(DockerJson.Text(n, "Name"), DockerJson.Text(n, "Driver"), subnet, DockerJson.Flag(n, "Internal"), stack);
            if (!network.Fixed || containers.Any(c => c.Networks.Contains(network.Name))) networks.Add(network);
        }
        return new DockerMap(containers, networks.OrderBy(n => n.BuiltIn).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>A container borrowing another's network is on whatever that one is on, which is more use on a map than nothing.</summary>
    static List<string> Attached(DockerDetails c, List<DockerDetails> all)
    {
        if (!c.NetworkMode.StartsWith("container:")) return c.Networks;
        var host = c.NetworkMode["container:".Length..];
        return all.FirstOrDefault(o => o.Id == host || o.Name == host)?.Networks ?? c.Networks;
    }
}
