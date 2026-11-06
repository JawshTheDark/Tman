using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tman.Core;

public enum DockerTemplateKind { Container = 1, Swarm = 2, Compose = 3, Edge = 4 }

public sealed record TemplatePort(string Host, string Container, string Protocol);
public sealed record TemplateVolume(string Target, string? Bind, bool ReadOnly);
public sealed record TemplateChoice(string Text, string Value);
public sealed record TemplateEnv(string Name, string Label, string Description, string Value, bool Preset, List<TemplateChoice> Choices);

/// <summary>One entry in a Portainer v3 template list. Everything here came from a file on the internet.</summary>
public sealed class DockerTemplate
{
    public int Id { get; init; }
    public DockerTemplateKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Note { get; init; } = "";
    public string[] Categories { get; init; } = Array.Empty<string>();
    public string Platform { get; init; } = "";
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public List<TemplatePort> Ports { get; init; } = new();
    public List<TemplateVolume> Volumes { get; init; } = new();
    public List<TemplateEnv> Env { get; init; } = new();
    public List<KeyValuePair<string, string>> Labels { get; init; } = new();
    public string Restart { get; init; } = "";
    public bool Privileged { get; init; }
    public string Network { get; init; } = "";
    public string Command { get; init; } = "";
    public string Hostname { get; init; } = "";
    public bool Interactive { get; init; }
    public string? StackUrl { get; init; }
    /// <summary>The Compose file itself, for one of your own templates.</summary>
    public string? ComposeText { get; init; }
    /// <summary>Other <c>docker run</c> options, one per line. Only your own templates can have these, because the form's warnings don't look inside them.</summary>
    public string Options { get; init; } = "";
    public bool IsMine { get; init; }
    public string Maintainer { get; init; } = "";
    public string Logo { get; init; } = "";

    /// <summary>Which list this came from, by the name of its GitHub account.</summary>
    public string SourceList { get; set; } = "";

    /// <summary>What sets this apart from the app's other versions, for the version picker.</summary>
    public string VersionText => Kind == DockerTemplateKind.Container
        ? $"{Image} · {SourceList}"
        : $"{KindText} stack · {SourceList}";

    public string KindText => Kind switch
    {
        DockerTemplateKind.Container => "Container",
        DockerTemplateKind.Compose or DockerTemplateKind.Edge => "Compose",
        _ => "Swarm",
    };

    public string CategoryText => string.Join(", ", Categories);
    public string Source => Kind == DockerTemplateKind.Container ? Image : StackUrl ?? "";

    /// <summary>Why this template can't be deployed from here, or null if it can.</summary>
    public string? CantDeploy(string? engineOs)
    {
        if (Kind == DockerTemplateKind.Swarm) return "This one needs Docker Swarm, which Tman doesn't set up.";
        if (Kind != DockerTemplateKind.Container && StackUrl == null && ComposeText == null) return "The template doesn't say where its Compose file is.";
        if (engineOs != null && Platform.Length > 0 && !Platform.Equals(engineOs, StringComparison.OrdinalIgnoreCase))
            return $"This one is for {Platform} containers, and Docker is running {engineOs} containers.";
        return null;
    }
}

/// <summary>One app, however many lists mention it. Versions come best first.</summary>
public sealed class DockerApp
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] Categories { get; init; } = Array.Empty<string>();
    public List<string> Logos { get; init; } = new();
    public List<DockerTemplate> Versions { get; init; } = new();
}

public sealed record TemplateLists(List<DockerTemplate> Templates, int Loaded, List<string> Problems);

public static class DockerTemplates
{
    public static readonly string[] DefaultUrls =
    {
        "https://raw.githubusercontent.com/TomChantler/portainer-templates/refs/heads/v3/templates_v3.json",
        "https://raw.githubusercontent.com/Lissy93/portainer-templates/main/templates.json",
        "https://raw.githubusercontent.com/xneo1/portainer_templates/master/Template/template.json",
        "https://raw.githubusercontent.com/ntv-one/portainer/main/template.json",
        "https://raw.githubusercontent.com/dnburgess/self-hosted-template/master/template.json",
        "https://raw.githubusercontent.com/Qballjos/portainer_templates/master/Template/template.json",
        "https://raw.githubusercontent.com/SelfhostedPro/selfhosted_templates/portainer-2.0/Template/template.json",
        "https://raw.githubusercontent.com/technorabilia/portainer-templates/main/lsio/templates/templates.json",
        "https://raw.githubusercontent.com/mikestraney/portainer-templates/master/templates.json",
    };

    static readonly TimeSpan CacheLife = TimeSpan.FromDays(7);

    const long ListLimit = 16 * 1024 * 1024, StackLimit = 1024 * 1024;

    public static string DataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman");

    static string CachePath(string url) => Path.Combine(DataFolder, "docker-templates", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".json");

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tman");
        return client;
    }

    /// <summary>The name a list goes by: the GitHub account for GitHub files, otherwise the website.</summary>
    public static string SourceName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase) && parts.Length > 0 ? parts[0] : uri.Host;
    }

    /// <summary>
    /// Gets every list, from its saved copy while that's recent. A list that can't be downloaded falls back to its saved copy
    /// and is reported, so one dead address never empties the page.
    /// </summary>
    /// <param name="cacheOnly">Stay off the network, and use whatever is saved however old.</param>
    public static async Task<TemplateLists> LoadAllAsync(IReadOnlyList<string> urls, bool force, bool cacheOnly = false)
    {
        var results = await Task.WhenAll(urls.Select(url => Task.Run(() => LoadOne(url, force, cacheOnly)))).ConfigureAwait(false);
        var templates = results.SelectMany(r => r.Templates).ToList();
        return new TemplateLists(templates, results.Count(r => r.Templates.Count > 0), results.Where(r => r.Problem != null).Select(r => r.Problem!).ToList());
    }

    static async Task<(List<DockerTemplate> Templates, string? Problem)> LoadOne(string url, bool force, bool cacheOnly)
    {
        var saved = ReadCache(url);
        if (cacheOnly) return (saved?.Templates ?? new(), null);
        if (!force && saved is { } fresh && DateTime.Now - fresh.Saved < CacheLife) return (fresh.Templates, null);
        try
        {
            return (await UpdateAsync(url).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (saved?.Templates ?? new(), $"{SourceName(url)}: {ex.Message}");
        }
    }

    /// <summary>The list saved by the last update, with when it was saved. Null if there isn't a usable one.</summary>
    public static (List<DockerTemplate> Templates, DateTime Saved)? ReadCache(string url)
    {
        try
        {
            var path = CachePath(url);
            if (!File.Exists(path)) return null;
            var templates = Parse(File.ReadAllText(path), SourceName(url));
            return templates.Count > 0 ? (templates, File.GetLastWriteTime(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads a list and saves it, replacing the old one only if the new one is good. Throws with a readable message.</summary>
    public static async Task<List<DockerTemplate>> UpdateAsync(string url)
    {
        var json = await FetchAsync(url, ListLimit).ConfigureAwait(false);
        List<DockerTemplate> templates;
        try
        {
            templates = Parse(json, SourceName(url));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("That address didn't return a template list.");
        }
        if (templates.Count == 0) throw new InvalidOperationException("That list has no templates in it.");
        var path = CachePath(url);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        return templates;
    }

    public static Task<string> FetchStackAsync(DockerTemplate template)
    {
        if (template.ComposeText != null) return Task.FromResult(template.ComposeText);
        return template.StackUrl == null ? throw new InvalidOperationException("The template doesn't say where its Compose file is.") : FetchAsync(template.StackUrl, StackLimit);
    }

    static async Task<string> FetchAsync(string url, long limit)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Tman only downloads from https addresses.");
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("The file isn't there any more. Its author may have moved or removed it.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                bytes.Write(buffer, 0, read);
                if (bytes.Length > limit) throw new InvalidOperationException("The file is too large to be what it should be.");
            }
            return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"The download failed: {ex.Message}");
        }
    }

    /// <summary>Reads a list, skipping entries it can't make sense of. Throws only if the file as a whole isn't one.</summary>
    /// <param name="mine">The file is your own, so it can hold what a list from the internet isn't trusted with.</param>
    public static List<DockerTemplate> Parse(string json, string source = "", bool mine = false)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("templates", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new JsonException("No templates array.");
        var templates = new List<DockerTemplate>();
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || ReadTemplate(e, mine) is not { } template) continue;
            template.SourceList = source;
            templates.Add(template);
        }
        return templates;
    }

    static DockerTemplate? ReadTemplate(JsonElement e, bool mine)
    {
        if (!e.TryGetProperty("type", out var type) || !type.TryGetInt32(out var kind) || kind is < 1 or > 4) return null;
        var title = Text(e, "title").Trim();
        var image = Text(e, "image").Trim();
        if (title.Length == 0) return null;
        if ((DockerTemplateKind)kind == DockerTemplateKind.Container && !DockerCli.IsValidImage(image)) return null;
        return new DockerTemplate
        {
            Id = e.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0,
            Kind = (DockerTemplateKind)kind,
            Title = title,
            Description = Plain(Text(e, "description")),
            Note = Plain(Text(e, "note")),
            Categories = Items(e, "categories").Append(e.TryGetProperty("category", out var single) ? single : default)
                .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()!.Trim() : "").Where(c => c.Length > 0).ToArray(),
            Logo = Text(e, "logo").Trim(),
            Platform = Text(e, "platform"),
            Name = Text(e, "name"),
            Image = image,
            Ports = Items(e, "ports").Select(ReadPort).OfType<TemplatePort>().ToList(),
            Volumes = Items(e, "volumes").Select(ReadVolume).OfType<TemplateVolume>().ToList(),
            Env = Items(e, "env").Select(ReadEnv).OfType<TemplateEnv>().ToList(),
            Labels = Items(e, "labels").Where(l => l.ValueKind == JsonValueKind.Object && Text(l, "name").Length > 0)
                .Select(l => new KeyValuePair<string, string>(Text(l, "name"), Text(l, "value"))).ToList(),
            Restart = Text(e, "restart_policy"),
            Privileged = Flag(e, "privileged"),
            Network = Text(e, "network"),
            Command = Text(e, "command"),
            Hostname = Text(e, "hostname"),
            Interactive = Flag(e, "interactive"),
            StackUrl = FindStack(e),
            ComposeText = mine && Text(e, DockerMyTemplates.ComposeKey) is { Length: > 0 } compose ? compose : null,
            Options = mine ? Text(e, DockerMyTemplates.OptionsKey) : "",
            IsMine = mine,
            Maintainer = Text(e, "maintainer").Trim(),
        };
    }

    static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static string Text(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => "",
        };
    }

    static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Notes are written as HTML for Portainer. Tman shows them as plain text, so nothing in them can run or link anywhere.</summary>
    static string Plain(string html)
    {
        // Some authors write Markdown links instead. Only the words are kept.
        var text = Regex.Replace(html, @"\[([^\]]+)\]\(https?://[^)]*\)", "$1");
        text = Regex.Replace(text, @"<\s*(br|/p|/li|/div)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", ""));
        return string.Join("\n", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    static readonly Regex PortText = new(@"^(?:(?<host>\d*(?:-\d+)?):)?(?<target>\d+(?:-\d+)?)(?:/(?<protocol>tcp|udp))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static TemplatePort? ReadPort(JsonElement e)
    {
        // Older lists name each port: { "web": "8080/tcp" }.
        var text = e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Select(p => p.Value.ToString()).FirstOrDefault() : e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
        var match = PortText.Match(text?.Trim() ?? "");
        if (!match.Success) return null;
        var protocol = match.Groups["protocol"].Success ? match.Groups["protocol"].Value.ToLowerInvariant() : "tcp";
        return new TemplatePort(match.Groups["host"].Value, match.Groups["target"].Value, protocol);
    }

    static TemplateVolume? ReadVolume(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var target = Text(e, "container").Trim();
        if (target.Length == 0) return null;
        var bind = Text(e, "bind").Trim();
        bool readOnly = Flag(e, "readonly");
        // Some authors put the flag in the path itself.
        if (bind.EndsWith(":ro")) (bind, readOnly) = (bind[..^3], true);
        if (target.EndsWith(":ro")) (target, readOnly) = (target[..^3], true);
        return new TemplateVolume(target, bind.Length > 0 ? bind : null, readOnly);
    }

    static TemplateEnv? ReadEnv(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var name = Text(e, "name").Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.-]*$")) return null;
        var value = Text(e, "default");
        var choices = new List<TemplateChoice>();
        foreach (var c in Items(e, "select"))
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            choices.Add(new TemplateChoice(Text(c, "text"), Text(c, "value")));
            if (Flag(c, "default")) value = Text(c, "value");
        }
        if (Text(e, "set") is { Length: > 0 } set) value = set;
        var label = Text(e, "label").Trim();
        return new TemplateEnv(name, label.Length > 0 ? label : name, Plain(Text(e, "description")), value, Flag(e, "preset"), choices);
    }

    /// <summary>The raw file for a stack. Only GitHub repositories can be turned into one, and all of them in this format are.</summary>
    static string? FindStack(JsonElement e)
    {
        if (e.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object)
        {
            var match = Regex.Match(Text(repo, "url").Trim(), @"^https://github\.com/([\w.-]+)/([\w.-]+?)(?:\.git)?/?$");
            var file = Text(repo, "stackfile").Trim().TrimStart('/');
            if (match.Success && file.Length > 0 && !file.Contains("..") && !file.Contains('?') && !file.Contains('#'))
                return $"https://raw.githubusercontent.com/{match.Groups[1].Value}/{match.Groups[2].Value}/HEAD/{file}";
        }
        return Text(e, "stackfile").Trim() is { Length: > 0 } direct && direct.StartsWith("https://") ? direct : null;
    }

    /// <summary>
    /// Folds every list into one entry per app. Two templates are the same app if they share a name or an image, and exact
    /// copies disappear. Different builds of an app stay as its versions, because they aren't interchangeable.
    /// </summary>
    public static List<DockerApp> Merge(IReadOnlyList<DockerTemplate> templates)
    {
        var parent = Enumerable.Range(0, templates.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        var seen = new Dictionary<string, int>();
        void Link(string key, int index)
        {
            if (seen.TryGetValue(key, out var other)) parent[Find(index)] = Find(other);
            else seen[key] = index;
        }

        for (int i = 0; i < templates.Count; i++)
        {
            Link("title:" + TitleKey(templates[i].Title), i);
            if (templates[i].Image.Length > 0) Link("image:" + ImageKey(templates[i].Image), i);
        }

        var apps = new List<DockerApp>();
        foreach (var group in Enumerable.Range(0, templates.Count).GroupBy(Find))
        {
            var variants = group.Select(i => templates[i])
                .GroupBy(VersionKey)
                .Select(same => same.OrderByDescending(Richness).First())
                .OrderByDescending(v => KindRank(v.Kind)).ThenByDescending(Richness)
                .ToList();
            var members = group.Select(i => templates[i]).ToList();
            var main = variants[0];
            var title = BestTitle(members);
            apps.Add(new DockerApp
            {
                Title = title,
                Description = main.Description.Length > 0 ? main.Description : members.FirstOrDefault(m => m.Description.Length > 0)?.Description ?? "",
                Categories = members.SelectMany(m => m.Categories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Logos = IconAddresses(title, members),
                Versions = variants,
            });
        }
        return apps.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What most lists call it, in the most readable spelling, without tags like "(container)".</summary>
    static string BestTitle(List<DockerTemplate> members) =>
        members.Select(m => CleanTitle(m.Title)).GroupBy(TitleKey)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Length).First()
            .GroupBy(t => t, StringComparer.Ordinal)
            .OrderByDescending(g => g.Key.Any(char.IsUpper)).ThenByDescending(g => g.Count()).First().Key;

    static string CleanTitle(string title)
    {
        var clean = Regex.Replace(title, @"\([^)]*\)|\{[^}]*\}|\[[^\]]*\]", "").Trim();
        return clean.Length > 0 ? clean : title;
    }

    const string IconSet = "https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/";

    /// <summary>
    /// Everywhere an app's picture might be, best first. WPF can't draw SVG, and many lists link one from the dashboard-icons
    /// set, so those switch to the same set's PNG. That set, which covers most self-hosted apps, is also the fallback for
    /// apps with a dead logo or none, tried under the app's name and then its image's name. Logos from templates named after
    /// the app come first, then the most common, because apps built on a shared image bring along their own branding.
    /// </summary>
    internal static List<string> IconAddresses(string title, List<DockerTemplate> members)
    {
        var name = TitleKey(title);
        var addresses = members.Where(m => m.Logo.Length > 0).GroupBy(m => ToPng(m.Logo))
            .OrderByDescending(g => g.Any(m => TitleKey(m.Title) == name)).ThenByDescending(g => g.Count())
            .Select(g => g.Key).ToList();
        addresses.Add(IconSet + Slug(title) + ".png");
        foreach (var image in members.Where(t => t.Image.Length > 0).Select(t => ImageKey(t.Image).Split('/')[^1]))
            addresses.Add(IconSet + Slug(image) + ".png");
        return addresses.Distinct().ToList();
    }

    static string ToPng(string logo) =>
        logo.Contains("dashboard-icons", StringComparison.OrdinalIgnoreCase) && logo.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? logo[..^4].Replace("/svg/", "/png/") + ".png"
            : logo;

    static int KindRank(DockerTemplateKind kind) => kind switch
    {
        DockerTemplateKind.Container => 3,
        DockerTemplateKind.Compose => 2,
        DockerTemplateKind.Edge => 1,
        _ => 0,
    };

    /// <summary>How much a template tells you. Between copies, the fullest one is kept.</summary>
    static int Richness(DockerTemplate t) =>
        (t.Logo.Length > 0 ? 2 : 0) + (t.Description.Length > 0 ? 1 : 0) + (t.Categories.Length > 0 ? 1 : 0) + Math.Min(t.Ports.Count, 2) + Math.Min(t.Env.Count, 2) + Math.Min(t.Volumes.Count, 2);

    static string VersionKey(DockerTemplate t) =>
        t.Kind == DockerTemplateKind.Container ? "c:" + FullImage(t.Image) : "s:" + (t.StackUrl ?? t.Title).ToLowerInvariant();

    /// <summary>Spelling aside, "Calibre-Web", "calibre web" and "Calibre Web (container)" are one name.</summary>
    internal static string TitleKey(string title)
    {
        var key = Regex.Replace(CleanTitle(title).ToLowerInvariant(), "[^a-z0-9]", "");
        return key.Length > 0 ? key : title.ToLowerInvariant();
    }

    /// <summary>The same image reached through a different registry address is still the same image.</summary>
    static string BareImage(string image)
    {
        var name = Regex.Replace(image.Trim().ToLowerInvariant(), @"^(docker\.io|index\.docker\.io|registry\.hub\.docker\.com|lscr\.io|ghcr\.io)/", "");
        return name.StartsWith("library/") ? name["library/".Length..] : name;
    }

    /// <summary>An image without its version, for telling which app something is.</summary>
    public static string ImageKey(string image) => Regex.Replace(BareImage(image), @"(@.*|:[^/]*)$", "");

    static string FullImage(string image)
    {
        var name = BareImage(image);
        return Regex.IsMatch(name, @"(@.*|:[^/]*)$") ? name : name + ":latest";
    }

    public static string Slug(string text)
    {
        var slug = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "app" : slug.Length > 50 ? slug[..50].TrimEnd('-') : slug;
    }

    public static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:=@,{}".Contains(c)) ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    public static HashSet<int> BusyPorts(string protocol)
    {
        try
        {
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            return (protocol == "udp" ? ip.GetActiveUdpListeners() : ip.GetActiveTcpListeners()).Select(p => p.Port).ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return new HashSet<int>(); // Docker reports a clash itself if it comes to that
        }
    }

    /// <summary>The next free port from <paramref name="wanted"/>, or the port itself if none is near.</summary>
    public static int FreePort(int wanted, HashSet<int> busy)
    {
        for (int port = wanted; port <= Math.Min(65535, wanted + 200); port++)
        {
            if (busy.Add(port)) return port;
        }
        return wanted;
    }

    /// <summary>Paths in a Compose file that reach into the PC running Docker, or give a container more than a container should have.</summary>
    public static List<string> StackWarnings(string yaml)
    {
        var warnings = new List<string>();
        if (Regex.IsMatch(yaml, @"^\s*privileged:\s*[""']?true", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            warnings.Add("It runs a privileged container, which has almost full control of Docker's virtual machine.");
        if (yaml.Contains("docker.sock"))
            warnings.Add("It gets the Docker socket, so it can start, stop and read every other container.");
        if (Regex.IsMatch(yaml, @"^\s*network_mode:\s*[""']?host", RegexOptions.Multiline))
            warnings.Add("It uses host networking. Its ports may not be reachable from Windows unless Docker Desktop's host networking is turned on.");
        if (Regex.IsMatch(yaml, @"^\s*build:", RegexOptions.Multiline))
            warnings.Add("It builds an image from source, which can take a while and may need more files than this one.");
        if (Regex.IsMatch(yaml, @"^\s*external:\s*true", RegexOptions.Multiline))
            warnings.Add("It expects a network or volume that must already exist, so it may stop with an error until you create it.");
        if (Regex.IsMatch(yaml, @"^\s*-\s*[""']?/(portainer|volume1|opt/mediadepot|media/|path/to)", RegexOptions.Multiline))
            warnings.Add("Its data paths came from the author's server. Unchanged, they land inside Docker's virtual machine rather than a folder you can browse. Change them to Windows folders below if you want the files.");
        return warnings;
    }
}

public sealed class PlanPort
{
    public string Host { get; set; } = "";
    public string Target { get; set; } = "";
    public string Protocol { get; set; } = "tcp";
    /// <summary>The address a running container's port answers on, when "this PC or the network" can't describe it.</summary>
    public string Address { get; init; } = "";

    public bool IsBlank => Host.Trim().Length == 0 && Target.Trim().Length == 0;
}

public sealed class PlanVolume
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool ReadOnly { get; set; }
    /// <summary>Docker's other mount options, such as "z", as they were written.</summary>
    public string Options { get; init; } = "";
    /// <summary>The template pointed this at a path Windows doesn't have, so Tman chose a Docker volume instead.</summary>
    public string? OriginalBind { get; init; }

    public bool IsBlank => Source.Trim().Length == 0 && Target.Trim().Length == 0;

    /// <summary>A volume nobody named, which Docker calls by a long random name.</summary>
    public bool Unnamed => Regex.IsMatch(Source.Trim(), "^[0-9a-f]{64}$");

    /// <summary>A folder rather than a Docker volume.</summary>
    public bool IsFolder => Source.Contains('/') || Source.Contains('\\');
}

public sealed class PlanEnv
{
    public string Name { get; set; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string Value { get; set; } = "";
    public bool Preset { get; init; }
    public List<TemplateChoice> Choices { get; init; } = new();
    /// <summary>The name can be edited, because no template fixed it.</summary>
    public bool Typed { get; init; }
    /// <summary>Passed even when empty, because the container has it set that way.</summary>
    public bool KeepEmpty { get; init; }
}

/// <summary>A network a container joins after it's made, because <c>docker create</c> only takes one with all its options.</summary>
public sealed record NetworkJoin(string Network, List<string> Aliases, string Ip, string Ip6, long Priority)
{
    public string[] ConnectArgs(string container)
    {
        var args = new List<string> { "network", "connect" };
        foreach (var alias in Aliases) args.AddRange(new[] { "--alias", alias });
        if (Ip.Length > 0) args.AddRange(new[] { "--ip", Ip });
        if (Ip6.Length > 0) args.AddRange(new[] { "--ip6", Ip6 });
        if (Priority != 0) args.AddRange(new[] { "--gw-priority", Priority.ToString() });
        args.Add(Network);
        args.Add(container);
        return args.ToArray();
    }

    /// <summary>Records compare their lists by reference, and two plans of one container must compare equal.</summary>
    public string Text => string.Join(' ', ConnectArgs(""));
}

/// <summary>A single container, as the deploy form edits it.</summary>
public sealed class ContainerPlan
{
    public const string RestartNever = "no";
    public static readonly string[] RestartPolicies = { RestartNever, "unless-stopped", "always", "on-failure" };

    public string Title { get; init; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string Restart { get; set; } = RestartNever;
    /// <summary>Empty for Docker's default network. Otherwise "host", "none", "container:…" or a network's name.</summary>
    public string Network { get; set; } = "";
    public bool Privileged { get; set; }
    public bool OpenToNetwork { get; set; }
    public bool Tty { get; init; }
    public string Hostname { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>Templates write commands with $VARIABLES in them. A running container's command is already final.</summary>
    public bool FillCommand { get; init; }
    /// <summary>Any other <c>docker run</c> options, one per line.</summary>
    public string Options { get; set; } = "";
    public List<PlanPort> Ports { get; init; } = new();
    public List<PlanVolume> Volumes { get; init; } = new();
    public List<PlanEnv> Env { get; init; } = new();
    public List<PlanEnv> Labels { get; init; } = new();
    /// <summary>Networks beyond the first, which only a running container can already be on.</summary>
    public List<NetworkJoin> AlsoOn { get; init; } = new();

    public bool HostNetwork => Network == "host";

    /// <summary>Networks where Docker refuses published ports or a hostname, or ignores them.</summary>
    bool SharedNetwork => HostNetwork || Network.StartsWith("container:");

    /// <summary>Starts from a template, with settings chosen to work on Windows and to stay off the network until asked.</summary>
    public static ContainerPlan From(DockerTemplate t, IReadOnlyCollection<string> takenNames, HashSet<int> busyTcp, HashSet<int> busyUdp)
    {
        var name = DockerTemplates.Slug(t.Name.Length > 0 ? t.Name : t.Title);
        var unique = name;
        for (int i = 2; takenNames.Contains(unique); i++) unique = $"{name}-{i}";

        var ports = t.Ports.Select(p => new PlanPort { Host = HostPort(p, busyTcp, busyUdp), Target = p.Container, Protocol = p.Protocol }).ToList();
        var volumes = new List<PlanVolume>();
        var used = new HashSet<string>();
        foreach (var v in t.Volumes)
        {
            // Your own template's paths are ones you chose, so they stay.
            bool keep = v.Bind != null && (t.IsMine || KeepBind(v.Bind));
            var source = keep ? v.Bind! : VolumeName(unique, v.Target, used);
            volumes.Add(new PlanVolume { Source = source, Target = v.Target, ReadOnly = v.ReadOnly, OriginalBind = keep || t.IsMine ? null : v.Bind });
        }

        return new ContainerPlan
        {
            Title = t.Title,
            Name = unique,
            Image = t.Image,
            Restart = t.Restart.Length > 0 && (t.IsMine || RestartPolicies.Contains(t.Restart)) ? t.Restart : RestartNever,
            // Docker Desktop's host networking is opt-in, so published ports are the safer way in whenever a template lists any.
            Network = t.IsMine ? t.Network : t.Network == "host" && ports.Count == 0 ? "host" : "",
            Privileged = t.Privileged,
            Tty = t.Interactive,
            Hostname = t.Hostname,
            Command = t.Command,
            FillCommand = !t.IsMine,
            Options = t.Options,
            Ports = ports,
            Volumes = volumes,
            Env = t.Env.Select(e => ToPlan(e, t.IsMine)).ToList(),
            Labels = t.Labels.Select(l => new PlanEnv { Name = l.Key, Value = l.Value, Typed = true }).ToList(),
        };
    }

    /// <summary>A form row for a template setting. Templates carry their author's time zone, so TZ starts as this PC's.</summary>
    internal static PlanEnv ToPlan(TemplateEnv e, bool asSaved = false) => new()
    {
        Name = e.Name, Label = e.Label, Description = e.Description, Preset = e.Preset, Choices = e.Choices, Typed = asSaved,
        Value = !asSaved && e.Name == "TZ" && e.Choices.Count == 0 && TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var zone) ? zone : e.Value,
    };

    static string HostPort(TemplatePort p, HashSet<int> busyTcp, HashSet<int> busyUdp)
    {
        var wanted = p.Host.Length > 0 ? p.Host : p.Container;
        if (!int.TryParse(wanted, out var port)) return wanted; // a range stays as the template wrote it
        return DockerTemplates.FreePort(port, p.Protocol == "udp" ? busyUdp : busyTcp).ToString();
    }

    /// <summary>
    /// Only paths that mean something inside Docker's Linux machine stay as they are. The rest are folders on the template
    /// author's server, which don't exist here, so their data goes in a Docker volume unless you choose a folder.
    /// </summary>
    static bool KeepBind(string bind)
    {
        if (!bind.StartsWith('/')) return true; // already a volume name
        if (bind == "/") return true;
        return new[] { "/var/run/", "/run/", "/etc/", "/proc", "/sys", "/dev/", "/var/lib/docker", "/lib/modules" }.Any(bind.StartsWith);
    }

    static string VolumeName(string container, string target, HashSet<string> used)
    {
        var last = target.TrimEnd('/').Split('/').LastOrDefault(s => s.Length > 0) ?? "data";
        var name = $"{container}-{DockerTemplates.Slug(last)}";
        var unique = name;
        for (int i = 2; !used.Add(unique); i++) unique = $"{name}-{i}";
        return unique;
    }

    static readonly Regex PortNumber = new(@"^\d{1,5}(-\d{1,5})?$", RegexOptions.Compiled);
    static readonly Regex EnvName = new(@"^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.Compiled);
    static readonly Regex MountPath = new(@"^(/|[A-Za-z]:[\\/])", RegexOptions.Compiled);

    /// <summary>What's wrong with the form, in the order it appears.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (!DockerCli.IsValidName(Name)) problems.Add("The name can use letters, numbers, dots, dashes and underscores, and must start with a letter or number.");
        if (!DockerCli.IsValidImage(Image)) problems.Add("The image name isn't valid.");
        var ports = Ports.Where(p => !p.IsBlank).ToList();
        foreach (var p in ports)
        {
            if (!PortNumber.IsMatch(p.Target.Trim())) problems.Add($"\"{p.Target}\" isn't a port number inside the container.");
            if (p.Host.Trim().Length > 0 && !PortNumber.IsMatch(p.Host.Trim())) problems.Add($"Port {p.Target}: \"{p.Host}\" isn't a port number.");
        }
        var hostPorts = ports.Where(p => p.Host.Trim().Length > 0).GroupBy(p => (p.Host.Trim(), p.Protocol)).Where(g => g.Count() > 1);
        foreach (var repeat in hostPorts) problems.Add($"Port {repeat.Key.Item1} is used twice.");
        foreach (var v in Volumes.Where(v => !v.IsBlank))
        {
            if (v.Source.Trim().Length == 0) problems.Add($"{v.Target} needs a volume name or a folder.");
            else if (v.Source.Trim().StartsWith('-')) problems.Add($"{v.Target}: \"{v.Source}\" isn't a valid volume name or folder.");
            if (!MountPath.IsMatch(v.Target.Trim())) problems.Add($"\"{v.Target}\" isn't a path inside the container. It should start with a slash.");
        }
        foreach (var pair in Env.Concat(Labels).Where(e => e.Typed && e.Name.Trim().Length == 0 && e.Value.Length > 0))
            problems.Add($"\"{pair.Value}\" needs a name.");
        foreach (var e in Env.Where(e => e.Typed && e.Name.Trim().Length > 0 && !EnvName.IsMatch(e.Name.Trim())))
            problems.Add($"\"{e.Name}\" can't be a setting's name. Use letters, numbers and underscores.");
        if (Words(Options).FirstOrDefault() is { } first && !first.StartsWith('-')) problems.Add("Other options start with a dash, such as --memory 512m.");
        return problems;
    }

    /// <summary>Things worth knowing before this runs. Most come from the template, not from anything you chose.</summary>
    public List<string> Warnings()
    {
        var warnings = new List<string>();
        if (Privileged) warnings.Add("Privileged: this container gets almost full control of Docker's virtual machine.");
        if (Volumes.Any(v => v.Source.Contains("docker.sock"))) warnings.Add("Docker socket: this container can start, stop and read every other container.");
        if (Volumes.Any(v => v.Source == "/" || v.Source.StartsWith("/proc") || v.Source.StartsWith("/sys") || v.Source.StartsWith("/dev/")))
            warnings.Add("It can see Docker's virtual machine, not only its own files.");
        if (HostNetwork) warnings.Add("Host network: its ports may not be reachable from Windows unless Docker Desktop's host networking is turned on.");
        if (OpenToNetwork && !SharedNetwork && Ports.Any(p => p.Host.Trim().Length > 0)) warnings.Add("Other devices on your network can reach these ports, and many templates ship with no password or a default one.");
        return warnings;
    }

    public IEnumerable<string> RunArgs() => Args("run", Name.Trim(), Restart);

    /// <summary>The same as <see cref="RunArgs"/>, without starting it.</summary>
    public IEnumerable<string> CreateArgs() => Args("create", Name.Trim(), Restart);

    /// <summary>Everything that only a new container can change. The name and restart policy aren't in it, because a live container can change those.</summary>
    public string Fingerprint() => string.Join('\0', Args("create", "", RestartNever).Concat(AlsoOn.Select(n => n.Text)));

    List<string> Args(string verb, string name, string restart)
    {
        var args = new List<string> { verb };
        if (verb == "run") args.Add("--detach");
        args.AddRange(new[] { "--name", name });
        if (Tty) args.AddRange(new[] { "--interactive", "--tty" });
        if (restart != RestartNever) args.AddRange(new[] { "--restart", restart });
        if (Network.Length > 0) args.AddRange(new[] { "--network", Network });
        if (Hostname.Length > 0 && !SharedNetwork) args.AddRange(new[] { "--hostname", Hostname });
        if (Privileged) args.Add("--privileged");
        if (!SharedNetwork)
        {
            foreach (var p in Ports.Where(p => p.Host.Trim().Length > 0))
                args.AddRange(new[] { "--publish", $"{Bound(p)}{p.Host.Trim()}:{p.Target.Trim()}/{p.Protocol}" });
        }
        foreach (var v in Volumes.Where(v => !v.IsBlank))
        {
            var options = string.Join(',', new[] { v.ReadOnly ? "ro" : "", v.Options }.Where(o => o.Length > 0));
            args.AddRange(new[] { "--volume", $"{v.Source.Trim()}:{v.Target.Trim()}{(options.Length > 0 ? ":" + options : "")}" });
        }
        foreach (var e in Env.Where(e => e.Name.Trim().Length > 0 && (e.Value.Length > 0 || e.KeepEmpty)))
            args.AddRange(new[] { "--env", $"{e.Name.Trim()}={e.Value}" });
        foreach (var l in Labels.Where(l => l.Name.Trim().Length > 0))
            args.AddRange(new[] { "--label", $"{l.Name.Trim()}={l.Value}" });
        if (Title.Length > 0) args.AddRange(new[] { "--label", $"tman.template={Title}" });
        args.AddRange(Words(Options));
        args.Add(Image.Trim());
        args.AddRange(CommandWords());
        return args;
    }

    string Bound(PlanPort p)
    {
        if (p.Address.Length == 0) return OpenToNetwork ? "" : "127.0.0.1:";
        return p.Address.Contains(':') ? $"[{p.Address}]:" : $"{p.Address}:";
    }

    /// <summary>Docker takes a command as words, and doesn't fill in $VARIABLES itself.</summary>
    public List<string> CommandWords()
    {
        if (!FillCommand) return Words(Command);
        return Words(Regex.Replace(Command, @"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?", m =>
            Env.FirstOrDefault(e => e.Name == m.Groups[1].Value) is { Value.Length: > 0 } e ? e.Value : m.Value));
    }

    /// <summary>Splits a line into words the way a shell would, honouring quotes.</summary>
    public static List<string> Words(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        bool started = false;
        foreach (char c in line)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                started = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (started || word.Length > 0) words.Add(word.ToString());
                word.Clear();
                started = false;
            }
            else
            {
                word.Append(c);
            }
        }
        if (started || word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>Writes one word so that <see cref="Words"/> reads it back exactly.</summary>
    public static string QuoteWord(string word)
    {
        if (word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'')) return word;
        if (!word.Contains('"')) return $"\"{word}\"";
        return !word.Contains('\'') ? $"'{word}'" : "\"" + word.Replace("\"", "\"'\"'\"") + "\"";
    }

    public string Preview() => "docker " + string.Join(" ", RunArgs().Select(DockerTemplates.Quote));
}

/// <summary>A Compose stack, as the deploy form edits it. Either a new one from a template, or one that's already deployed.</summary>
public sealed class StackPlan
{
    public string Title { get; init; } = "";
    public string Project { get; set; } = "";
    public string Yaml { get; set; } = "";
    public List<PlanEnv> Env { get; init; } = new();
    /// <summary>Where a deployed stack's files are. Null for a new stack.</summary>
    public DockerStackFiles? Deployed { get; init; }
    /// <summary>A deployed stack's .env file exactly as written, since it may not be one Tman wrote.</summary>
    public string EnvText { get; set; } = "";

    public static StackPlan From(DockerTemplate t, string yaml, IReadOnlyCollection<string> takenProjects)
    {
        var name = DockerTemplates.Slug(t.Name.Length > 0 ? t.Name : t.Title);
        var unique = name;
        for (int i = 2; takenProjects.Contains(unique); i++) unique = $"{name}-{i}";
        return new StackPlan
        {
            Title = t.Title,
            Project = unique,
            Yaml = yaml.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            Env = t.Env.Select(e => ContainerPlan.ToPlan(e, t.IsMine)).ToList(),
        };
    }

    /// <summary>Reads a deployed stack's files. Throws if they can't be read.</summary>
    public static StackPlan From(DockerStackFiles deployed)
    {
        var yaml = File.ReadAllText(deployed.Files[0]);
        var env = File.Exists(deployed.EnvFile) ? File.ReadAllText(deployed.EnvFile) : "";
        return new StackPlan
        {
            Title = deployed.Project,
            Project = deployed.Project,
            Deployed = deployed,
            Yaml = yaml.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            EnvText = env.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
        };
    }

    public string Folder => Deployed?.Folder ?? Path.Combine(DockerTemplates.DataFolder, "docker-stacks", Project);
    public string ComposeFile => Deployed?.Files[0] ?? Path.Combine(Folder, "docker-compose.yml");
    public string EnvFile => Deployed?.EnvFile ?? Path.Combine(Folder, ".env");

    public List<string> Problems()
    {
        var problems = new List<string>();
        if (!DockerCli.IsValidProject(Project)) problems.Add("The stack name can use lowercase letters, numbers, dashes and underscores, and must start with a letter or number.");
        if (Yaml.Trim().Length == 0) problems.Add("The Compose file is empty.");
        // Compose reads these values from a file where an apostrophe would end the value early.
        foreach (var e in Env.Where(e => e.Value.Contains('\'')))
            problems.Add($"{e.Label}: stack settings can't contain an apostrophe. Remove it, or put the value in the Compose file itself.");
        return problems;
    }

    /// <summary>Writes the Compose file and, beside it, the values it fills in from the form.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(ComposeFile, Yaml);
        if (Deployed != null)
        {
            if (EnvText.Length > 0 || File.Exists(EnvFile)) File.WriteAllText(EnvFile, EnvText);
            return;
        }
        var values = Env.Where(e => e.Value.Length > 0).Select(e => $"{e.Name}='{e.Value.Replace("\r", "").Replace("\n", " ")}'");
        File.WriteAllLines(EnvFile, values);
    }

    public string[] UpArgs() => Deployed != null
        ? Deployed.Args("up", "--detach", "--remove-orphans")
        : DockerCli.ComposeArgs(Project, "--project-directory", Folder, "--file", ComposeFile, "up", "--detach");

    public string Preview()
    {
        var up = "docker " + string.Join(" ", UpArgs().Select(DockerTemplates.Quote));
        return Deployed == null ? up : "docker " + string.Join(" ", Deployed.Args("config", "--quiet").Select(DockerTemplates.Quote)) + Environment.NewLine + up;
    }
}
