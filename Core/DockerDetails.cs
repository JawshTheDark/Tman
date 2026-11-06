using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tman.Core;

/// <summary>Reads Docker's inspect output, where nearly any field can be null or missing.</summary>
internal static class DockerJson
{
    public static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static string Text(JsonElement e, string name)
    {
        var v = Get(e, name);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => "",
        };
    }

    public static long Number(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : 0;

    public static bool Flag(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.True;

    public static List<string> Texts(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray().Select(i => i.ToString()).ToList() : new();

    public static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static IEnumerable<JsonProperty> Fields(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Object } v ? v.EnumerateObject() : Enumerable.Empty<JsonProperty>();

    /// <summary>True for null, nothing, zero, off and empty lists, which is how Docker writes a setting nobody chose.</summary>
    public static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => true,
        JsonValueKind.String => v.GetString()!.Length == 0,
        JsonValueKind.Number => v.GetDouble() == 0,
        JsonValueKind.Array => v.GetArrayLength() == 0,
        JsonValueKind.Object => !v.EnumerateObject().Any(),
        _ => false,
    };
}

/// <summary>An image's own defaults, which a container's settings are measured against.</summary>
public sealed class DockerImageDetails
{
    public DockerImageDetails(JsonElement root)
    {
        Config = DockerJson.Get(root, "Config");
        Id = DockerJson.Text(root, "Id");
        RepoDigests = DockerJson.Texts(root, "RepoDigests");
    }

    public string Id { get; }
    public List<string> RepoDigests { get; }
    internal JsonElement Config { get; }
}

/// <summary>The settings of one container, as <c>docker container inspect</c> reports them.</summary>
public sealed class DockerDetails
{
    const string ComposeLabel = "com.docker.compose.";

    readonly JsonElement _root, _config, _host, _settings;

    public DockerDetails(JsonElement root)
    {
        _root = root;
        _config = DockerJson.Get(root, "Config");
        _host = DockerJson.Get(root, "HostConfig");
        _settings = DockerJson.Get(root, "NetworkSettings");
        var state = DockerJson.Get(root, "State");
        Id = DockerJson.Text(root, "Id");
        Name = DockerJson.Text(root, "Name").TrimStart('/');
        Image = DockerJson.Text(_config, "Image");
        ImageId = DockerJson.Text(root, "Image");
        Running = DockerJson.Flag(state, "Running");
        Restarting = DockerJson.Flag(state, "Restarting");
        ExitCode = (int)DockerJson.Number(state, "ExitCode");
        Labels = DockerJson.Fields(_config, "Labels").ToDictionary(p => p.Name, p => p.Value.ToString());
        NetworkMode = DockerJson.Text(_host, "NetworkMode");
        Networks = DockerJson.Fields(_settings, "Networks").Select(p => p.Name).ToList();
        ChangedBytes = DockerJson.Number(root, "SizeRw");
    }

    public string Id { get; }
    public string Name { get; }
    /// <summary>The image as it was asked for, such as nginx:latest, even after that tag has moved on.</summary>
    public string Image { get; }
    /// <summary>The image this container really runs.</summary>
    public string ImageId { get; }
    public bool Running { get; }
    public bool Restarting { get; }
    public int ExitCode { get; }
    public Dictionary<string, string> Labels { get; }
    public string NetworkMode { get; }
    public List<string> Networks { get; }
    /// <summary>What's been written inside the container itself. Only filled in when asked for, as it takes a moment.</summary>
    public long ChangedBytes { get; }
    public bool AutoRemove => DockerJson.Flag(_host, "AutoRemove");

    public string Stack => Label("project");
    public string Service => Label("service");
    public string StackFolder => Label("project.working_dir");
    public List<string> StackFiles => Label("project.config_files").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    public string StackEnvFile => Label("project.environment_file").Split(',')[0].Trim();
    public bool InSwarm => Labels.ContainsKey("com.docker.swarm.service.name");

    string Label(string name) => Labels.GetValueOrDefault(ComposeLabel + name, "");

    /// <summary>Other containers that lean on this one, which is worth saying before it's replaced.</summary>
    public bool DependsOn(DockerDetails other)
    {
        if (NetworkMode == $"container:{other.Id}" || NetworkMode == $"container:{other.Name}") return true;
        bool Names(string text) => text.Split(':')[0].TrimStart('/') == other.Name || text.Split(':')[0] == other.Id;
        return DockerJson.Texts(_host, "VolumesFrom").Any(Names) || DockerJson.Texts(_host, "Links").Any(Names);
    }

    /// <summary>What Tman knows about one published port, for the network map.</summary>
    public List<MapPort> PublishedPorts()
    {
        var ports = new List<MapPort>();
        foreach (var p in ReadPorts())
            ports.Add(new MapPort($"{p.Host}→{p.Target}{(p.Protocol == "tcp" ? "" : "/" + p.Protocol)}", p.Open));
        return ports;
    }

    /// <summary>Why this container can't join other networks, or null if it can.</summary>
    public string? NetworkLock => NetworkMode switch
    {
        "host" => "It uses the host network, so it can't join others. Change that in its settings.",
        "none" => "It has networking turned off, so it can't join others. Change that in its settings.",
        _ => NetworkMode.StartsWith("container:") ? "It shares another container's network, so it can't join others." : null,
    };

    /// <summary>
    /// The settings that would make this same container again. Anything an image sets by itself is left out, so that an
    /// updated image brings its own values rather than this one's old ones. Docker's list of settings grows over time, so
    /// any setting Tman doesn't know is named in <paramref name="notCarried"/> rather than silently dropped.
    /// </summary>
    public ContainerPlan ToPlan(DockerImageDetails? image, string defaultLogDriver, List<string> notCarried)
    {
        var options = new List<string>();
        void Option(string flag, string? value = null) => options.Add(value == null ? flag : $"{flag} {ContainerPlan.QuoteWord(value)}");

        ReadHostOptions(Option, defaultLogDriver, notCarried);
        ReadConfigOptions(Option, image);

        var words = new List<string>();
        var command = DockerJson.Texts(_config, "Cmd");
        if (image == null)
        {
            words.AddRange(command);
        }
        else
        {
            var entrypoint = DockerJson.Texts(_config, "Entrypoint");
            bool newEntrypoint = !entrypoint.SequenceEqual(DockerJson.Texts(image.Config, "Entrypoint"));
            if (newEntrypoint)
            {
                // The flag takes one word. The rest of an entrypoint becomes the start of the command.
                Option("--entrypoint", entrypoint.FirstOrDefault() ?? "");
                words.AddRange(entrypoint.Skip(1));
            }
            if (newEntrypoint || !command.SequenceEqual(DockerJson.Texts(image.Config, "Cmd"))) words.AddRange(command);
        }

        var (network, alsoOn) = ReadNetworks(Option);
        bool ownHostname = DockerJson.Text(_config, "Hostname") != Id[..Math.Min(12, Id.Length)] && !network.StartsWith("container:") && network != "host";
        var ports = ReadPorts();
        bool open = ports.Any(p => p.Open);
        var plan = new ContainerPlan
        {
            Title = Labels.GetValueOrDefault("tman.template", ""),
            Name = Name,
            Image = Image,
            Restart = ReadRestart(),
            Network = network,
            Privileged = DockerJson.Flag(_host, "Privileged"),
            OpenToNetwork = open,
            Hostname = ownHostname ? DockerJson.Text(_config, "Hostname") : "",
            Command = string.Join(" ", words.Select(ContainerPlan.QuoteWord)),
            Options = string.Join(Environment.NewLine, options),
            Volumes = ReadVolumes(Option, notCarried),
            AlsoOn = alsoOn,
        };
        // Volumes can add options of their own, such as temporary folders.
        plan.Options = string.Join(Environment.NewLine, options);

        foreach (var p in ports)
            plan.Ports.Add(new PlanPort { Host = p.Host, Target = p.Target, Protocol = p.Protocol, Address = p.Address.Length > 0 && !(p.Local && !open) ? p.Address : "" });
        foreach (var unpublished in UnpublishedPorts(ports, image))
            plan.Ports.Add(unpublished);

        var imageEnv = image == null ? new HashSet<string>() : DockerJson.Texts(image.Config, "Env").ToHashSet();
        foreach (var e in DockerJson.Texts(_config, "Env").Where(e => !imageEnv.Contains(e)))
        {
            int equals = e.IndexOf('=');
            plan.Env.Add(new PlanEnv { Name = equals < 0 ? e : e[..equals], Value = equals < 0 ? "" : e[(equals + 1)..], Typed = true, KeepEmpty = true });
        }

        var imageLabels = image == null ? new Dictionary<string, string>() : DockerJson.Fields(image.Config, "Labels").ToDictionary(p => p.Name, p => p.Value.ToString());
        foreach (var (name, value) in Labels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            if (name == "tman.template" || name.StartsWith(ComposeLabel) || name.StartsWith("desktop.docker.io/")) continue;
            if (imageLabels.GetValueOrDefault(name) != value) plan.Labels.Add(new PlanEnv { Name = name, Value = value, Typed = true, KeepEmpty = true });
        }
        return plan;
    }

    string ReadRestart()
    {
        var policy = DockerJson.Get(_host, "RestartPolicy");
        var name = DockerJson.Text(policy, "Name");
        if (name.Length == 0) return ContainerPlan.RestartNever;
        long retries = DockerJson.Number(policy, "MaximumRetryCount");
        return name == "on-failure" && retries > 0 ? $"{name}:{retries}" : name;
    }

    // ---- Options with no field of their own in the form ----

    static readonly (string Field, string Flag)[] TextFlags =
    {
        ("CgroupParent", "--cgroup-parent"), ("CpusetCpus", "--cpuset-cpus"), ("CpusetMems", "--cpuset-mems"), ("PidMode", "--pid"),
        ("UTSMode", "--uts"), ("UsernsMode", "--userns"), ("VolumeDriver", "--volume-driver"),
    };

    static readonly (string Field, string Flag)[] NumberFlags =
    {
        ("CpuShares", "--cpu-shares"), ("CpuPeriod", "--cpu-period"), ("CpuQuota", "--cpu-quota"), ("CpuRealtimePeriod", "--cpu-rt-period"),
        ("CpuRealtimeRuntime", "--cpu-rt-runtime"), ("CpuCount", "--cpu-count"), ("CpuPercent", "--cpu-percent"), ("BlkioWeight", "--blkio-weight"),
        ("Memory", "--memory"), ("MemoryReservation", "--memory-reservation"), ("IOMaximumIOps", "--io-maxiops"),
        ("IOMaximumBandwidth", "--io-maxbandwidth"), ("OomScoreAdj", "--oom-score-adj"),
    };

    static readonly (string Field, string Flag)[] ListFlags =
    {
        ("CapAdd", "--cap-add"), ("CapDrop", "--cap-drop"), ("Dns", "--dns"), ("DnsOptions", "--dns-option"), ("DnsSearch", "--dns-search"),
        ("ExtraHosts", "--add-host"), ("GroupAdd", "--group-add"), ("SecurityOpt", "--security-opt"), ("VolumesFrom", "--volumes-from"),
        ("DeviceCgroupRules", "--device-cgroup-rule"),
    };

    static readonly (string Field, string Flag)[] MapFlags = { ("Sysctls", "--sysctl"), ("StorageOpt", "--storage-opt"), ("Annotations", "--annotation") };

    static readonly (string Field, string Flag)[] OnFlags =
    {
        ("AutoRemove", "--rm"), ("PublishAllPorts", "--publish-all"), ("ReadonlyRootfs", "--read-only"), ("Init", "--init"), ("OomKillDisable", "--oom-kill-disable"),
    };

    /// <summary>Fields read elsewhere, or ones that only ever hold what Docker put there itself.</summary>
    static readonly HashSet<string> Elsewhere = new()
    {
        "Binds", "Mounts", "Tmpfs", "NetworkMode", "PortBindings", "RestartPolicy", "Privileged", "LogConfig", "Links", "Devices", "DeviceRequests",
        "Ulimits", "IpcMode", "CgroupnsMode", "Runtime", "Isolation", "ShmSize", "NanoCpus", "MemorySwap", "MemorySwappiness", "PidsLimit",
        "ConsoleSize", "ContainerIDFile", "MaskedPaths", "ReadonlyPaths",
    };

    const long DefaultShmSize = 64 * 1024 * 1024;

    void ReadHostOptions(Action<string, string?> option, string defaultLogDriver, List<string> notCarried)
    {
        foreach (var (field, flag) in TextFlags)
        {
            if (DockerJson.Text(_host, field) is { Length: > 0 } text) option(flag, text);
        }
        foreach (var (field, flag) in NumberFlags)
        {
            if (DockerJson.Number(_host, field) is not 0 and var number) option(flag, number.ToString());
        }
        foreach (var (field, flag) in ListFlags)
        {
            foreach (var item in DockerJson.Texts(_host, field)) option(flag, item);
        }
        foreach (var (field, flag) in MapFlags)
        {
            foreach (var pair in DockerJson.Fields(_host, field)) option(flag, $"{pair.Name}={pair.Value}");
        }
        foreach (var (field, flag) in OnFlags)
        {
            if (DockerJson.Flag(_host, field)) option(flag, null);
        }

        long cpus = DockerJson.Number(_host, "NanoCpus");
        if (cpus > 0) option("--cpus", (cpus / 1e9).ToString("0.###", CultureInfo.InvariantCulture));
        // Docker fills this in as twice the memory limit when nobody chose it.
        long swap = DockerJson.Number(_host, "MemorySwap");
        if (swap != 0 && swap != DockerJson.Number(_host, "Memory") * 2) option("--memory-swap", swap.ToString());
        if (DockerJson.Get(_host, "MemorySwappiness") is { ValueKind: JsonValueKind.Number } swappiness && swappiness.GetInt64() >= 0) option("--memory-swappiness", swappiness.ToString());
        if (DockerJson.Number(_host, "PidsLimit") > 0) option("--pids-limit", DockerJson.Number(_host, "PidsLimit").ToString());
        long shm = DockerJson.Number(_host, "ShmSize");
        if (shm != 0 && shm != DefaultShmSize) option("--shm-size", shm.ToString());
        if (DockerJson.Text(_host, "IpcMode") is { Length: > 0 } ipc && ipc is not ("private" or "shareable")) option("--ipc", ipc);
        if (DockerJson.Text(_host, "CgroupnsMode") is { Length: > 0 } cgroup && cgroup != "private") option("--cgroupns", cgroup);
        if (DockerJson.Text(_host, "Runtime") is { Length: > 0 } runtime && runtime != "runc") option("--runtime", runtime);
        if (DockerJson.Text(_host, "Isolation") is { Length: > 0 } isolation && isolation != "default") option("--isolation", isolation);

        var log = DockerJson.Get(_host, "LogConfig");
        if (DockerJson.Text(log, "Type") is { Length: > 0 } driver && driver != defaultLogDriver) option("--log-driver", driver);
        foreach (var pair in DockerJson.Fields(log, "Config")) option("--log-opt", $"{pair.Name}={pair.Value}");

        foreach (var u in DockerJson.Items(_host, "Ulimits"))
            option("--ulimit", $"{DockerJson.Text(u, "Name")}={DockerJson.Number(u, "Soft")}:{DockerJson.Number(u, "Hard")}");
        foreach (var d in DockerJson.Items(_host, "Devices"))
            option("--device", $"{DockerJson.Text(d, "PathOnHost")}:{DockerJson.Text(d, "PathInContainer")}:{DockerJson.Text(d, "CgroupPermissions")}");
        foreach (var link in DockerJson.Texts(_host, "Links"))
        {
            // Docker stores these as /other:/this/alias.
            var parts = link.Split(':');
            if (parts.Length == 2) option("--link", $"{parts[0].TrimStart('/')}:{parts[1].Split('/')[^1]}");
        }
        foreach (var request in DockerJson.Items(_host, "DeviceRequests"))
        {
            var ids = DockerJson.Texts(request, "DeviceIDs");
            long count = DockerJson.Number(request, "Count");
            if (ids.Count > 0) option("--gpus", "device=" + string.Join(',', ids));
            else if (count < 0) option("--gpus", "all");
            else notCarried.Add("a GPU request");
        }

        var known = TextFlags.Concat(NumberFlags).Concat(ListFlags).Concat(MapFlags).Concat(OnFlags).Select(f => f.Field).ToHashSet();
        foreach (var field in _host.EnumerateObject())
        {
            if (!known.Contains(field.Name) && !Elsewhere.Contains(field.Name) && !DockerJson.IsEmpty(field.Value)) notCarried.Add(field.Name);
        }
    }

    void ReadConfigOptions(Action<string, string?> option, DockerImageDetails? image)
    {
        string FromImage(string name) => image == null ? "" : DockerJson.Text(image.Config, name);

        if (DockerJson.Text(_config, "Domainname") is { Length: > 0 } domain) option("--domainname", domain);
        if (DockerJson.Text(_config, "User") is { Length: > 0 } user && user != FromImage("User")) option("--user", user);
        if (DockerJson.Text(_config, "WorkingDir") is { Length: > 0 } folder && folder != FromImage("WorkingDir")) option("--workdir", folder);
        if (DockerJson.Text(_config, "StopSignal") is { Length: > 0 } signal && signal != FromImage("StopSignal")) option("--stop-signal", signal);
        if (DockerJson.Number(_config, "StopTimeout") is > 0 and var timeout) option("--stop-timeout", timeout.ToString());
        if (DockerJson.Flag(_config, "Tty")) option("--tty", null);
        if (DockerJson.Flag(_config, "OpenStdin")) option("--interactive", null);
        if (DockerJson.Text(_config, "MacAddress") is { Length: > 0 } mac) option("--mac-address", mac);

        var imagePorts = image == null ? new HashSet<string>() : DockerJson.Fields(image.Config, "ExposedPorts").Select(p => p.Name).ToHashSet();
        var published = DockerJson.Fields(_host, "PortBindings").Select(p => p.Name).ToHashSet();
        foreach (var port in DockerJson.Fields(_config, "ExposedPorts"))
        {
            if (!imagePorts.Contains(port.Name) && !published.Contains(port.Name)) option("--expose", port.Name);
        }

        ReadHealth(option, image);
    }

    void ReadHealth(Action<string, string?> option, DockerImageDetails? image)
    {
        var health = DockerJson.Get(_config, "Healthcheck");
        if (health.ValueKind != JsonValueKind.Object) return;
        var imageHealth = image == null ? default : DockerJson.Get(image.Config, "Healthcheck");
        if (imageHealth.ValueKind == JsonValueKind.Object && imageHealth.GetRawText() == health.GetRawText()) return;

        var test = DockerJson.Texts(health, "Test");
        if (test.FirstOrDefault() == "NONE")
        {
            option("--no-healthcheck", null);
            return;
        }
        if (test.Count > 1) option("--health-cmd", test[0] == "CMD-SHELL" ? test[1] : string.Join(" ", test.Skip(1).Select(ContainerPlan.QuoteWord)));
        foreach (var (field, flag) in new[] { ("Interval", "--health-interval"), ("Timeout", "--health-timeout"), ("StartPeriod", "--health-start-period"), ("StartInterval", "--health-start-interval") })
        {
            if (DockerJson.Number(health, field) is > 0 and var nanoseconds) option(flag, (nanoseconds / 1e9).ToString("0.###", CultureInfo.InvariantCulture) + "s");
        }
        if (DockerJson.Number(health, "Retries") is > 0 and var retries) option("--health-retries", retries.ToString());
    }

    // ---- Ports ----

    sealed record BoundPort(string Host, string Target, string Protocol, string Address, bool Open, bool Local);

    /// <summary>Published ports, with runs of them put back into the ranges they were asked for as.</summary>
    List<BoundPort> ReadPorts()
    {
        var single = new List<(int Target, string Protocol, int Host, string HostText, string Address, bool Open, bool Local)>();
        var live = DockerJson.Get(_settings, "Ports");
        foreach (var port in DockerJson.Fields(_host, "PortBindings"))
        {
            var pieces = port.Name.Split('/');
            if (!int.TryParse(pieces[0], out var target)) continue;
            var protocol = pieces.Length > 1 ? pieces[1] : "tcp";
            if (port.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var binding in port.Value.EnumerateArray())
            {
                var address = DockerJson.Text(binding, "HostIp");
                var hostText = DockerJson.Text(binding, "HostPort");
                // An empty port means Docker chose one, so that's the one to keep.
                if (hostText.Length == 0) hostText = DockerJson.Items(live, port.Name).Select(b => DockerJson.Text(b, "HostPort")).FirstOrDefault(p => p.Length > 0) ?? "0";
                bool open = address is "" or "0.0.0.0" or "::";
                bool local = address is "127.0.0.1" or "::1";
                if (single.Any(s => s.Target == target && s.Protocol == protocol && s.HostText == hostText && s.Open == open && s.Local == local)) continue;
                single.Add((target, protocol, int.TryParse(hostText, out var host) ? host : 0, hostText, open ? "" : address, open, local));
            }
        }

        var ports = new List<BoundPort>();
        foreach (var run in single.OrderBy(s => s.Protocol).ThenBy(s => s.Target))
        {
            if (ports.Count > 0 && ports[^1] is var last && last.Protocol == run.Protocol && last.Address == run.Address && run.Host > 0
                && Last(last.Target) + 1 == run.Target && Last(last.Host) + 1 == run.Host)
            {
                ports[^1] = last with { Host = $"{First(last.Host)}-{run.Host}", Target = $"{First(last.Target)}-{run.Target}" };
                continue;
            }
            ports.Add(new BoundPort(run.HostText, run.Target.ToString(), run.Protocol, run.Address, run.Open, run.Local));
        }
        return ports;
    }

    static int First(string range) => int.TryParse(range.Split('-')[0], out var n) ? n : -2;
    static int Last(string range) => int.TryParse(range.Split('-')[^1], out var n) ? n : -2;

    /// <summary>Ports the image offers that aren't published, as empty rows, so that opening one only takes a number.</summary>
    IEnumerable<PlanPort> UnpublishedPorts(List<BoundPort> bound, DockerImageDetails? image)
    {
        var shown = new HashSet<string>();
        var offered = DockerJson.Fields(_config, "ExposedPorts").Select(p => p.Name);
        if (image != null) offered = offered.Concat(DockerJson.Fields(image.Config, "ExposedPorts").Select(p => p.Name));
        foreach (var port in offered.Where(p => p.Length > 0).OrderBy(p => p, StringComparer.Ordinal))
        {
            var pieces = port.Split('/');
            var protocol = pieces.Length > 1 ? pieces[1] : "tcp";
            if (!int.TryParse(pieces[0], out var number) || !shown.Add(port)) continue;
            if (bound.Any(b => b.Protocol == protocol && First(b.Target) <= number && number <= Last(b.Target))) continue;
            yield return new PlanPort { Target = pieces[0], Protocol = protocol };
        }
    }

    // ---- Networks ----

    /// <summary>
    /// The network mode says what the container started on, and the attached list says what it's on now. They differ
    /// once it's been connected or disconnected, as the network map does, and now is what has to carry over.
    /// </summary>
    (string Network, List<NetworkJoin> AlsoOn) ReadNetworks(Action<string, string?> option)
    {
        var mode = NetworkMode;
        if (mode is "host" or "none" || mode.StartsWith("container:")) return (mode, new());

        var wanted = mode is "" or "default" ? "bridge" : mode;
        // A container that failed to start has an empty list too, but it never left its network the way a disconnected one did.
        bool failedToStart = DockerJson.Text(DockerJson.Get(_root, "State"), "Error").Length > 0;
        var primary = Networks.Contains(wanted) || (Networks.Count == 0 && failedToStart) ? wanted : Networks.FirstOrDefault();
        if (primary == null) return ("none", new());

        var attached = DockerJson.Get(_settings, "Networks");
        foreach (var alias in Aliases(DockerJson.Get(attached, primary))) option("--network-alias", alias);
        var ipam = DockerJson.Get(DockerJson.Get(attached, primary), "IPAMConfig");
        if (DockerJson.Text(ipam, "IPv4Address") is { Length: > 0 } ip) option("--ip", ip);
        if (DockerJson.Text(ipam, "IPv6Address") is { Length: > 0 } ip6) option("--ip6", ip6);

        var others = new List<NetworkJoin>();
        foreach (var name in Networks.Where(n => n != primary))
        {
            var endpoint = DockerJson.Get(attached, name);
            var address = DockerJson.Get(endpoint, "IPAMConfig");
            others.Add(new NetworkJoin(name, Aliases(endpoint), DockerJson.Text(address, "IPv4Address"), DockerJson.Text(address, "IPv6Address"), DockerJson.Number(endpoint, "GwPriority")));
        }
        return (primary == "bridge" ? "" : primary, others);
    }

    List<string> Aliases(JsonElement endpoint) =>
        DockerJson.Texts(endpoint, "Aliases").Where(a => a != Id[..Math.Min(12, Id.Length)]).ToList();

    // ---- Storage ----

    static readonly Regex BindText = new(@"^(?<source>.+?):(?<target>/[^:]*|[A-Za-z]:\\[^:]*)(?::(?<options>[a-zA-Z,]+))?$", RegexOptions.Compiled);
    static readonly Regex DesktopFolder = new(@"^(?:/run/desktop/mnt/host|/host_mnt|/mnt/host)/(?<drive>[a-zA-Z])(?:/(?<rest>.*))?$", RegexOptions.Compiled);

    /// <summary>Docker Desktop stores a Windows folder under its own mount point. This is the folder you'd recognise.</summary>
    internal static string WindowsPath(string source)
    {
        var match = DesktopFolder.Match(source);
        return match.Success ? $"{match.Groups["drive"].Value.ToUpperInvariant()}:\\{match.Groups["rest"].Value.Replace('/', '\\')}" : source;
    }

    List<PlanVolume> ReadVolumes(Action<string, string?> option, List<string> notCarried)
    {
        var volumes = new List<PlanVolume>();
        var binds = new Dictionary<string, (string Source, string Options)>();
        foreach (var bind in DockerJson.Texts(_host, "Binds"))
        {
            var match = BindText.Match(bind);
            if (match.Success) binds[match.Groups["target"].Value] = (match.Groups["source"].Value, match.Groups["options"].Value);
        }

        var special = DockerJson.Items(_host, "Mounts")
            .Where(m => new[] { "BindOptions", "VolumeOptions", "TmpfsOptions", "ImageOptions", "ClusterOptions" }.Any(o => !DockerJson.IsEmpty(DockerJson.Get(m, o))))
            .Select(m => DockerJson.Text(m, "Target")).ToHashSet();

        foreach (var mount in DockerJson.Items(_root, "Mounts"))
        {
            var target = DockerJson.Text(mount, "Destination");
            var type = DockerJson.Text(mount, "Type");
            if (special.Contains(target)) notCarried.Add($"the mount options for {target}");
            switch (type)
            {
                case "bind" or "volume":
                    var written = binds.GetValueOrDefault(target);
                    var source = type == "volume" ? DockerJson.Text(mount, "Name") : written.Source ?? DockerJson.Text(mount, "Source");
                    var words = (written.Options ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                    volumes.Add(new PlanVolume
                    {
                        Source = WindowsPath(source),
                        Target = target,
                        ReadOnly = !DockerJson.Flag(mount, "RW"),
                        Options = string.Join(',', words.Where(w => w is not ("ro" or "rw"))),
                    });
                    break;
                case "tmpfs":
                    if (!DockerJson.Fields(_host, "Tmpfs").Any(t => t.Name == target)) option("--tmpfs", target);
                    break;
                default:
                    notCarried.Add($"the {type} mount at {target}");
                    break;
            }
        }
        foreach (var folder in DockerJson.Fields(_host, "Tmpfs"))
            option("--tmpfs", folder.Value.ToString() is { Length: > 0 } settings ? $"{folder.Name}:{settings}" : folder.Name);
        return volumes;
    }
}
