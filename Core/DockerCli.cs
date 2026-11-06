using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tman.Core;

public enum DockerState { NotInstalled, Stopped, Running }

public sealed record DockerStatus(DockerState State, string? ClientVersion, string? EngineVersion, string? EngineOs, string? Problem);

public sealed record DockerContainer(string Id, string Name, string Image, string State, string Status, string Ports)
{
    /// <summary>A web address for the first published TCP port, or null when nothing is published.</summary>
    public string? Url => DockerCli.UrlFor(Ports);
}

public sealed record DockerStats(string Cpu, string Memory);

public sealed record DockerImage(string Id, string Repository, string Tag, string Size, string Created)
{
    /// <summary>What to pass to Docker: images without a name can only be reached by ID.</summary>
    public string Reference => Repository == "<none>" || Tag == "<none>" ? Id : $"{Repository}:{Tag}";
}

public sealed record DockerVolume(string Name, string Driver);

public sealed record DockerNetwork(string Id, string Name, string Driver, string Scope)
{
    public bool BuiltIn => Name is "bridge" or "host" or "none";
}

public sealed record DockerStack(string Name, string Status, string ConfigFiles);

/// <summary>Rows from Docker, or the reason there aren't any.</summary>
public sealed record DockerList<T>(List<T> Items, string? Error);

/// <summary>
/// Drives the docker command line. Arguments go straight to the process, never through a shell, because template values
/// come from a file on the internet. Reads and actions don't need administrator rights, only membership of docker-users.
/// </summary>
public static class DockerCli
{
    public const string DesktopPackage = "Docker.DockerDesktop";

    static string? _exe;

    /// <summary>Docker on PATH, then Docker Desktop's usual folder, which a PATH from before the install won't include.</summary>
    public static string? FindExe()
    {
        if (_exe != null && File.Exists(_exe)) return _exe;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('"')).Where(Path.IsPathFullyQualified)
            .Append(Path.Combine(ProgramFiles, "Docker", "Docker", "resources", "bin"));
        foreach (var folder in folders)
        {
            var candidate = Path.Combine(folder, "docker.exe");
            if (File.Exists(candidate)) return _exe = candidate;
        }
        return _exe = null;
    }

    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>Docker Desktop's app, or null if this PC only has the command line tools.</summary>
    public static string? DesktopApp()
    {
        var path = Path.Combine(ProgramFiles, "Docker", "Docker", "Docker Desktop.exe");
        return File.Exists(path) ? path : null;
    }

    static Task<WingetCli.Result> Run(int timeoutMs, params string[] args)
    {
        var exe = FindExe();
        return exe == null
            ? Task.FromResult(new WingetCli.Result(Cmd.FailedToStart, new List<string> { "Docker isn't installed." }))
            : WingetCli.RunAsync(exe, args, timeoutMs);
    }

    /// <summary>Streams a Docker command's output. Returns its exit code.</summary>
    public static Task<int> StreamAsync(IEnumerable<string> args, Action<string> onLine, CancellationToken cancel = default)
    {
        var exe = FindExe();
        if (exe != null) return WingetCli.StreamAsync(exe, args, onLine, cancel);
        onLine("Docker isn't installed.");
        return Task.FromResult(Cmd.FailedToStart);
    }

    public static async Task<DockerStatus> StatusAsync()
    {
        if (FindExe() == null) return new DockerStatus(DockerState.NotInstalled, null, null, null, null);
        var r = await Run(15000, "version", "--format", "{{json .}}").ConfigureAwait(false);
        var version = JsonLines(r.Lines).FirstOrDefault();
        string? client = null, engine = null, os = null;
        if (version.ValueKind == JsonValueKind.Object)
        {
            if (version.TryGetProperty("Client", out var c) && c.ValueKind == JsonValueKind.Object) client = Text(c, "Version");
            if (version.TryGetProperty("Server", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                engine = Text(s, "Version");
                os = Text(s, "Os");
            }
        }
        return !string.IsNullOrEmpty(engine)
            ? new DockerStatus(DockerState.Running, client, engine, os, null)
            : new DockerStatus(DockerState.Stopped, client, null, null, Explain(r.Lines));
    }

    public static async Task<DockerList<DockerContainer>> ContainersAsync()
    {
        var r = await Run(20000, "ps", "--all", "--no-trunc", "--format", "{{json .}}").ConfigureAwait(false);
        return Rows(r, e => new DockerContainer(Text(e, "ID"), Text(e, "Names"), Text(e, "Image"), Text(e, "State"), Text(e, "Status"), Text(e, "Ports")));
    }

    /// <summary>Live figures for running containers, by full ID. Takes a second or two, because Docker samples them.</summary>
    public static async Task<Dictionary<string, DockerStats>> StatsAsync()
    {
        var r = await Run(20000, "stats", "--no-stream", "--no-trunc", "--format", "{{json .}}").ConfigureAwait(false);
        var stats = new Dictionary<string, DockerStats>();
        if (r.Code != 0) return stats;
        foreach (var e in JsonLines(r.Lines))
            stats[Text(e, "ID")] = new DockerStats(Text(e, "CPUPerc"), Text(e, "MemUsage"));
        return stats;
    }

    public static async Task<DockerList<DockerImage>> ImagesAsync()
    {
        var r = await Run(20000, "images", "--format", "{{json .}}").ConfigureAwait(false);
        return Rows(r, e => new DockerImage(Text(e, "ID"), Text(e, "Repository"), Text(e, "Tag"), Text(e, "Size"), Text(e, "CreatedSince")));
    }

    public static async Task<DockerList<DockerVolume>> VolumesAsync()
    {
        var r = await Run(20000, "volume", "ls", "--format", "{{json .}}").ConfigureAwait(false);
        return Rows(r, e => new DockerVolume(Text(e, "Name"), Text(e, "Driver")));
    }

    public static async Task<DockerList<DockerNetwork>> NetworksAsync()
    {
        var r = await Run(20000, "network", "ls", "--format", "{{json .}}").ConfigureAwait(false);
        return Rows(r, e => new DockerNetwork(Text(e, "ID"), Text(e, "Name"), Text(e, "Driver"), Text(e, "Scope")));
    }

    /// <summary>Compose prints one JSON array here, unlike the one-object-per-line commands.</summary>
    public static async Task<DockerList<DockerStack>> StacksAsync()
    {
        var r = await Run(20000, "compose", "ls", "--all", "--format", "json").ConfigureAwait(false);
        if (r.Code != 0) return new DockerList<DockerStack>(new(), Explain(r.Lines));
        var stacks = new List<DockerStack>();
        var array = r.Lines.FirstOrDefault(l => l.TrimStart().StartsWith('['));
        if (array == null) return new DockerList<DockerStack>(stacks, null);
        try
        {
            using var doc = JsonDocument.Parse(array);
            foreach (var e in doc.RootElement.EnumerateArray())
                stacks.Add(new DockerStack(Text(e, "Name"), Text(e, "Status"), Text(e, "ConfigFiles")));
        }
        catch (JsonException)
        {
            return new DockerList<DockerStack>(stacks, "Docker's list of stacks couldn't be read.");
        }
        return new DockerList<DockerStack>(stacks, null);
    }

    public static string[] ComposeArgs(string project, params string[] command) =>
        new[] { "compose", "--ansi", "never", "--progress", "plain", "--project-name", project }.Concat(command).ToArray();

    static DockerList<T> Rows<T>(WingetCli.Result r, Func<JsonElement, T> read) =>
        r.Code != 0 ? new DockerList<T>(new List<T>(), Explain(r.Lines)) : new DockerList<T>(JsonLines(r.Lines).Select(read).ToList(), null);

    /// <summary>The JSON lines in Docker's output. Warnings can share the stream, so anything else is skipped.</summary>
    static List<JsonElement> JsonLines(IEnumerable<string> lines)
    {
        var rows = new List<JsonElement>();
        foreach (var line in lines)
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                rows.Add(doc.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return rows;
    }

    static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Null ? "" : v.ToString() : "";

    /// <summary>Docker's error in words someone can act on.</summary>
    public static string Explain(IEnumerable<string> lines)
    {
        var text = string.Join(" ", lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('{')));
        if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || text.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            return "Windows refused access to Docker. Your account may need adding to the docker-users group, followed by signing out and back in.";
        if (text.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase) || text.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pipe", StringComparison.OrdinalIgnoreCase) || text.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase))
            return "Docker's engine isn't running.";
        return text.Length > 0 ? text : "Docker didn't say why.";
    }

    static readonly Regex Published = new(@"(?:0\.0\.0\.0|127\.0\.0\.1|\[::1?\]|::):(\d+)(?:-\d+)?->\d+(?:-\d+)?/tcp", RegexOptions.Compiled);

    public static string? UrlFor(string ports)
    {
        var match = Published.Match(ports);
        return match.Success ? $"http://localhost:{match.Groups[1].Value}" : null;
    }

    static readonly Regex ImageName = new(@"^[a-z0-9][a-zA-Z0-9._/:@-]{0,254}$", RegexOptions.Compiled);
    static readonly Regex ContainerName = new(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$", RegexOptions.Compiled);
    static readonly Regex ProjectName = new(@"^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.Compiled);

    public static bool IsValidImage(string text) => ImageName.IsMatch(text);
    public static bool IsValidName(string text) => ContainerName.IsMatch(text);
    public static bool IsValidProject(string text) => ProjectName.IsMatch(text);
}
