using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Tman.Core;

/// <summary>One blocklist as the page shows it.</summary>
public sealed record BlocklistInfo(string Url, string Name, bool Enabled, int Domains, DateTime? Saved, string? Problem)
{
    public string EnabledText => Enabled ? "Yes" : "No";
    public string DomainsText => Domains > 0 ? Domains.ToString("#,0") : "";
    public string SavedText => Saved is { } t ? Fmt.Ago(t) : "Never";
    public string StatusText => Problem ?? (Saved == null ? "Not downloaded yet" : "OK");
}

/// <summary>The merged domains of every list in use, with lookup that walks up through parent names.</summary>
public sealed class BlocklistSet
{
    public static readonly BlocklistSet Empty = new(new HashSet<string>(), new Dictionary<string, string>());

    readonly HashSet<string> _domains;
    readonly Dictionary<string, string> _source;

    public BlocklistSet(HashSet<string> domains, Dictionary<string, string> source)
    {
        _domains = domains;
        _source = source;
    }

    public int Count => _domains.Count;
    public IEnumerable<string> Domains => _domains;

    /// <summary>The list that blocks this name or one of its parents, or null when none does.</summary>
    public string? Blocks(string name)
    {
        name = name.TrimEnd('.').ToLowerInvariant();
        while (name.Length > 0)
        {
            if (_domains.Contains(name)) return _source.GetValueOrDefault(name, "blocklist");
            int dot = name.IndexOf('.');
            if (dot < 0) break;
            name = name[(dot + 1)..];
        }
        return null;
    }
}

/// <summary>
/// Domain blocklists in hosts, plain-domain or Adblock format, downloaded over https, saved under %APPDATA%\Tman and
/// refreshed weekly. The content is text from the internet: nothing in it is trusted beyond "this is a domain name".
/// </summary>
public static class Blocklists
{
    public static readonly string[] DefaultUrls =
    {
        "https://small.oisd.nl/domainswild2",
        "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/wildcard/light-onlydomains.txt",
        "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts",
    };

    public static readonly TimeSpan CacheLife = TimeSpan.FromDays(7);
    const long SizeLimit = 64 * 1024 * 1024;

    /// <summary>
    /// Names that never go into the hosts file, whatever a list says: Windows Update, activation, Defender and the
    /// Store need them, and Microsoft Defender treats hosts entries for its own domains as a hijack and deletes the
    /// whole section. Subdomains count too.
    /// </summary>
    public static readonly string[] ProtectedSuffixes =
    {
        "microsoft.com", "windowsupdate.com", "windows.com", "windows.net", "microsoftonline.com", "msftconnecttest.com", "msftncsi.com",
        "live.com", "office.com", "office.net", "office365.com", "outlook.com", "bing.com", "msn.com", "msedge.net", "azure.com", "azureedge.net",
        "skype.com", "xbox.com", "xboxlive.com", "msauth.net", "msauthimages.net", "msocdn.com", "microsoftstore.com", "sfx.ms", "s-microsoft.com",
        "localhost", "localdomain", "local", "home.arpa",
    };

    public static string DataFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman");

    static string Folder => Path.Combine(DataFolder, "blocklists");
    static string CachePath(string url) => Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".txt");

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Tman");
        return client;
    }

    /// <summary>A short name for a list: its GitHub account and file, or its host.</summary>
    public static string Name(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
            return parts[0] + (parts.Length > 2 ? " " + Path.GetFileNameWithoutExtension(parts[^1]) : "");
        return uri.Host + (parts.Length > 0 ? " " + parts[^1] : "");
    }

    public static bool IsProtected(string name)
    {
        name = name.TrimEnd('.');
        foreach (var suffix in ProtectedSuffixes)
            if (name.Equals(suffix, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>What's known about each list without touching the network.</summary>
    public static List<BlocklistInfo> Describe(IEnumerable<string> urls, ICollection<string> disabled, IReadOnlyDictionary<string, string>? problems = null)
    {
        var list = new List<BlocklistInfo>();
        foreach (var url in urls)
        {
            var path = CachePath(url);
            int count = 0;
            DateTime? saved = null;
            if (File.Exists(path))
            {
                saved = File.GetLastWriteTime(path);
                count = CountCached(path);
            }
            list.Add(new BlocklistInfo(url, Name(url), !disabled.Contains(url), count, saved, problems?.GetValueOrDefault(url)));
        }
        return list;
    }

    static readonly Dictionary<string, (DateTime Written, int Count)> CountCache = new();

    static int CountCached(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        lock (CountCache)
        {
            if (CountCache.TryGetValue(path, out var c) && c.Written == written) return c.Count;
        }
        int count = Parse(File.ReadAllText(path)).Count;
        lock (CountCache) CountCache[path] = (written, count);
        return count;
    }

    /// <summary>True when any list in use has never been downloaded or is older than a week.</summary>
    public static bool NeedsUpdate(IEnumerable<string> urls, ICollection<string> disabled) =>
        urls.Where(u => !disabled.Contains(u)).Any(u => !File.Exists(CachePath(u)) || DateTime.Now - File.GetLastWriteTime(CachePath(u)) > CacheLife);

    /// <summary>Downloads the lists that need it. Returns the problems, by url; a list that fails keeps its old copy.</summary>
    public static async Task<Dictionary<string, string>> UpdateAsync(IEnumerable<string> urls, ICollection<string> disabled, bool force)
    {
        var problems = new Dictionary<string, string>();
        foreach (var url in urls.Where(u => !disabled.Contains(u)))
        {
            var path = CachePath(url);
            if (!force && File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < CacheLife) continue;
            try
            {
                await UpdateOneAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                problems[url] = ex.Message;
            }
        }
        return problems;
    }

    /// <summary>Downloads one list and saves it if it parses to at least one domain. Throws with a readable message.</summary>
    public static async Task<int> UpdateOneAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Tman only downloads from https addresses.");
        string text;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("The list isn't there any more. Its author may have moved or removed it.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                bytes.Write(buffer, 0, read);
                if (bytes.Length > SizeLimit) throw new InvalidOperationException("The file is too large to be a blocklist.");
            }
            text = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"The download failed: {ex.Message}");
        }
        var domains = Parse(text);
        if (domains.Count == 0) throw new InvalidOperationException("That address didn't return a blocklist.");
        Directory.CreateDirectory(Folder);
        // Only the domains are kept, one per line, so the saved copy is as plain as it can be.
        await File.WriteAllTextAsync(CachePath(url), string.Join('\n', domains)).ConfigureAwait(false);
        return domains.Count;
    }

    /// <summary>
    /// Reads hosts lines ("0.0.0.0 name"), plain names, "*.name" wildcards and Adblock "||name^" entries. Anything else
    /// is ignored. Names are lower-cased and checked as host names, so a line can't carry anything but a name.
    /// </summary>
    public static HashSet<string> Parse(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '!' or '[') continue;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].TrimEnd();
            string name;
            if (line.StartsWith("||") && line.EndsWith('^'))
            {
                name = line[2..^1];
                if (name.Contains('/') || name.Contains('*') || name.Contains('$')) continue;
            }
            else
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2 && HostsFile.IsIp(fields[0]))
                {
                    // Only sinkhole lines count, and only whole lines of names: a line with anything else on it is dropped.
                    if (fields[0] is not ("0.0.0.0" or "127.0.0.1" or "::" or "::1" or "0:0:0:0:0:0:0:0" or "0:0:0:0:0:0:0:1")) continue;
                    if (!fields.Skip(1).All(HostsFile.IsHostName)) continue;
                    foreach (var f in fields.Skip(1)) Add(set, f);
                    continue;
                }
                if (fields.Length != 1) continue;
                name = fields[0];
                if (name.StartsWith("*.")) name = name[2..];
            }
            Add(set, name);
        }
        return set;
    }

    static void Add(HashSet<string> set, string name)
    {
        name = name.ToLowerInvariant().TrimEnd('.');
        if (name.Length == 0 || name is "localhost" or "localhost.localdomain" or "local" or "broadcasthost" or "ip6-localhost" or "ip6-loopback" or "ip6-localnet" or "ip6-mcastprefix" or "ip6-allnodes" or "ip6-allrouters" or "ip6-allhosts" or "0.0.0.0") return;
        if (!name.Contains('.') || !HostsFile.IsHostName(name)) return;
        set.Add(name);
    }

    /// <summary>Everything in the lists in use, read from the saved copies. Nothing is downloaded here.</summary>
    public static BlocklistSet Merge(IEnumerable<string> urls, ICollection<string> disabled)
    {
        var domains = new HashSet<string>(StringComparer.Ordinal);
        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in urls.Where(u => !disabled.Contains(u)))
        {
            var path = CachePath(url);
            if (!File.Exists(path)) continue;
            string name = Name(url);
            foreach (var d in Parse(File.ReadAllText(path)))
                if (domains.Add(d)) source[d] = name;
        }
        return new BlocklistSet(domains, source);
    }

    // ---- The hosts file section ----

    public const string StartMarker = "# ==== Tman blocklist start: Tman rewrites everything between these markers; your own entries above are left alone ====";
    public const string EndMarker = "# ==== Tman blocklist end ====";
    const int NamesPerLine = 9;

    /// <summary>How many names the Tman section of a hosts file holds, or 0 when there isn't one.</summary>
    public static int CountInHosts(HostsFile file)
    {
        bool inside = false;
        int count = 0;
        foreach (var line in file.Lines)
        {
            if (line.Text.Trim() == StartMarker) inside = true;
            else if (line.Text.Trim() == EndMarker) inside = false;
            else if (inside && line.Kind == HostsLineKind.Entry) count += line.Hosts.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        return count;
    }

    /// <summary>Takes the Tman section out of a hosts file, leaving everything else as it was.</summary>
    public static void RemoveFromHosts(HostsFile file)
    {
        bool inside = false;
        foreach (var line in file.Lines.ToList())
        {
            var text = line.Text.Trim();
            if (text == StartMarker) inside = true;
            if (inside) file.Remove(line);
            if (text == EndMarker) inside = false;
        }
    }

    /// <summary>
    /// Replaces the Tman section of a hosts file with the given names, several per line, skipping protected names and
    /// names the file already has an entry for. Returns how many names were written.
    /// </summary>
    public static int WriteToHosts(HostsFile file, IEnumerable<string> domains, Func<string, bool>? skip = null)
    {
        RemoveFromHosts(file);
        var present = new HashSet<string>(file.Entries.SelectMany(e => e.Hosts.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        var names = domains.Where(d => !IsProtected(d) && !present.Contains(d) && (skip == null || !skip(d))).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (names.Count == 0) return 0;
        file.AddComment(StartMarker);
        file.AddComment($"# {names.Count:#,0} names from the App firewall page's blocklists, written {DateTime.Now:yyyy-MM-dd HH:mm}. Each resolves to 0.0.0.0, so nothing on this PC can reach it.");
        for (int i = 0; i < names.Count; i += NamesPerLine)
            file.Add("0.0.0.0", string.Join(' ', names.Skip(i).Take(NamesPerLine)), "");
        file.AddComment(EndMarker);
        return names.Count;
    }
}
