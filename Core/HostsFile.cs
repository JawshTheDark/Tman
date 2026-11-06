using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tman.Core;

public enum HostsLineKind { Blank, Comment, Entry, Invalid }

/// <summary>One line of the hosts file. Lines nobody edits are written back exactly as they were read.</summary>
public sealed class HostsLine
{
    public string Text { get; set; } = "";
    public string Eol { get; set; } = "";
    public HostsLineKind Kind { get; set; }
    public bool Enabled { get; set; }
    public string Ip { get; set; } = "";
    public string Hosts { get; set; } = "";
    public string Comment { get; set; } = "";

    public string EnabledText => Enabled ? "Yes" : "No";
    public string Warning => HostsFile.WarningFor(Ip, Hosts) ?? "";
}

/// <summary>
/// The hosts file as a list of lines that keeps its encoding, line endings, comments and order. Every read and write
/// names its path, so nothing here is tied to the real file.
/// </summary>
public sealed class HostsFile
{
    public static string SystemPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    public static string BackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "backups");

    /// <summary>Sites worth a warning when an entry redirects them. Subdomains count too.</summary>
    static readonly string[] WellKnown =
    {
        "google.com", "youtube.com", "gmail.com", "microsoft.com", "windows.com", "windowsupdate.com", "live.com", "office.com", "outlook.com",
        "bing.com", "apple.com", "icloud.com", "amazon.com", "facebook.com", "instagram.com", "whatsapp.com", "x.com", "twitter.com", "linkedin.com",
        "github.com", "paypal.com", "netflix.com", "reddit.com", "wikipedia.org", "steampowered.com", "steamcommunity.com", "discord.com",
        "dropbox.com", "cloudflare.com", "yahoo.com", "ebay.com", "chase.com", "bankofamerica.com", "wellsfargo.com", "citi.com",
    };

    /// <summary>The examples Microsoft ships in every hosts file are documentation, not entries someone disabled.</summary>
    static readonly string[] Samples = { "rhino.acme.com", "x.acme.com" };

    static HostsFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    readonly Encoding _encoding;
    readonly byte[] _preamble;
    byte[] _original;

    public List<HostsLine> Lines { get; private set; }
    public string EncodingName { get; }
    public string Eol { get; }

    HostsFile(Encoding encoding, byte[] preamble, string encodingName, string text, byte[] original)
    {
        _encoding = encoding;
        _preamble = preamble;
        _original = original;
        EncodingName = encodingName;
        Lines = Split(text);
        Eol = Lines.Where(l => l.Eol.Length > 0).GroupBy(l => l.Eol).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "\r\n";
    }

    public string EolName => Eol switch { "\r\n" => "Windows line endings", "\n" => "Unix line endings", _ => "old Mac line endings" };

    /// <summary>A missing file reads as an empty one: Windows works fine without it.</summary>
    public static HostsFile Load(string path) => Parse(File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>());

    public static HostsFile Parse(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new HostsFile(new UTF8Encoding(false), bytes[..3], "UTF-8 with BOM", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new HostsFile(new UnicodeEncoding(false, false), bytes[..2], "UTF-16", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new HostsFile(new UnicodeEncoding(true, false), bytes[..2], "UTF-16 big endian", Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), bytes);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return new HostsFile(new UTF8Encoding(false), Array.Empty<byte>(), "UTF-8", text, bytes);
        }
        catch (DecoderFallbackException)
        {
            var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return new HostsFile(ansi, Array.Empty<byte>(), "ANSI", ansi.GetString(bytes), bytes);
        }
    }

    static List<HostsLine> Split(string text)
    {
        var lines = new List<HostsLine>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\r' && c != '\n') continue;
            int len = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            lines.Add(Classify(new HostsLine { Text = text[start..i], Eol = text.Substring(i, len) }));
            i += len - 1;
            start = i + 1;
        }
        if (start < text.Length) lines.Add(Classify(new HostsLine { Text = text[start..] }));
        return lines;
    }

    static HostsLine Classify(HostsLine line)
    {
        var text = line.Text.Trim();
        line.Ip = line.Hosts = line.Comment = "";
        line.Enabled = false;
        if (text.Length == 0)
        {
            line.Kind = HostsLineKind.Blank;
            return line;
        }
        bool commented = text[0] == '#';
        var body = commented ? text.TrimStart('#').TrimStart() : text;
        int hash = body.IndexOf('#');
        var fields = (hash >= 0 ? body[..hash] : body).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool entry = fields.Length >= 2 && IsIp(fields[0]) && fields.Skip(1).All(IsHostName);
        if (entry && commented && fields.Skip(1).All(h => Samples.Contains(h, StringComparer.OrdinalIgnoreCase))) entry = false;
        if (!entry)
        {
            line.Kind = commented ? HostsLineKind.Comment : HostsLineKind.Invalid;
            return line;
        }
        line.Kind = HostsLineKind.Entry;
        line.Enabled = !commented;
        line.Ip = fields[0];
        line.Hosts = string.Join(' ', fields.Skip(1));
        line.Comment = hash >= 0 ? body[(hash + 1)..].Trim() : "";
        return line;
    }

    // ---- Reading it back ----

    public IEnumerable<HostsLine> Entries => Lines.Where(l => l.Kind == HostsLineKind.Entry);

    public string ToText() => string.Concat(Lines.Select(l => l.Text + l.Eol));

    public byte[] ToBytes() => _preamble.Concat(_encoding.GetBytes(ToText())).ToArray();

    public bool Dirty => !ToBytes().SequenceEqual(_original);

    /// <summary>What a save would change, as text lines removed and added. Order changes alone don't show.</summary>
    public (List<string> Removed, List<string> Added) Changes()
    {
        var was = Parse(_original).Lines.Select(l => l.Text).ToList();
        var now = Lines.Select(l => l.Text).ToList();
        return (Minus(was, now), Minus(now, was));
    }

    static List<string> Minus(List<string> from, List<string> take)
    {
        var counts = take.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var left = new List<string>();
        foreach (var text in from)
        {
            if (counts.TryGetValue(text, out int n) && n > 0) counts[text] = n - 1;
            else if (text.Trim().Length > 0) left.Add(text);
        }
        return left;
    }

    /// <summary>Text lines Windows will ignore because they aren't comments or valid entries.</summary>
    public List<string> Problems() => Lines
        .Select((l, i) => (Line: l, Number: i + 1))
        .Where(x => x.Line.Kind == HostsLineKind.Invalid)
        .Select(x => $"Line {x.Number}: {x.Line.Text.Trim()}")
        .ToList();

    public bool CanEncode(string text) => _encoding.GetString(_encoding.GetBytes(text)) == text;

    // ---- Editing ----

    static string Format(string ip, string hosts, string comment) =>
        $"{ip}  {hosts}" + (comment.Length > 0 ? $"  # {comment}" : "");

    public HostsLine Add(string ip, string hosts, string comment)
    {
        // A file without a final line break keeps that shape: the new last line goes without one too.
        var last = Lines.LastOrDefault();
        var eol = last == null ? Eol : last.Eol;
        if (last != null && last.Eol.Length == 0) last.Eol = Eol;
        var line = Classify(new HostsLine { Text = Format(ip, hosts, comment), Eol = eol });
        Lines.Add(line);
        return line;
    }

    public void Update(HostsLine line, string ip, string hosts, string comment)
    {
        line.Text = (line.Enabled ? "" : "# ") + Format(ip, hosts, comment);
        Classify(line);
    }

    public void Remove(HostsLine line)
    {
        // The line before takes over "last line" duties when the removed one had no line break.
        int index = Lines.IndexOf(line);
        if (index < 0) return;
        if (line.Eol.Length == 0 && index > 0) Lines[index - 1].Eol = "";
        Lines.RemoveAt(index);
    }

    public void SetEnabled(HostsLine line, bool enabled)
    {
        if (line.Kind != HostsLineKind.Entry || line.Enabled == enabled) return;
        line.Text = enabled ? line.Text.TrimStart().TrimStart('#').TrimStart() : "# " + line.Text;
        Classify(line);
    }

    /// <summary>Replaces everything with edited raw text. New line breaks follow the file's own.</summary>
    public void SetText(string raw)
    {
        var lines = Split(raw);
        foreach (var l in lines)
            if (l.Eol.Length > 0) l.Eol = Eol;
        Lines = lines;
    }

    // ---- Saving ----

    /// <summary>
    /// Writes the lines to <paramref name="target"/> after copying what's there to <paramref name="backupDir"/>.
    /// Refuses if the file changed since it was read. Returns the backup's path, or null if there was no file to copy.
    /// </summary>
    public string? Save(string target, string backupDir)
    {
        var onDisk = File.Exists(target) ? File.ReadAllBytes(target) : Array.Empty<byte>();
        if (!onDisk.SequenceEqual(_original))
            throw new IOException("The hosts file changed on disk after Tman read it, so nothing was saved. Reload it and make your changes again.");
        var backup = onDisk.Length > 0 ? WriteBackup(onDisk, backupDir) : null;
        var bytes = ToBytes();
        WriteChecked(target, bytes);
        _original = bytes;
        return backup;
    }

    /// <summary>Copies a backup over <paramref name="target"/>, keeping a copy of what it replaces. Returns that copy's path.</summary>
    public static string? Restore(string backupFile, string target, string backupDir)
    {
        var bytes = File.ReadAllBytes(backupFile);
        var onDisk = File.Exists(target) ? File.ReadAllBytes(target) : Array.Empty<byte>();
        var backup = onDisk.Length > 0 ? WriteBackup(onDisk, backupDir) : null;
        WriteChecked(target, bytes);
        return backup;
    }

    static void WriteChecked(string target, byte[] bytes)
    {
        File.WriteAllBytes(target, bytes);
        if (!File.ReadAllBytes(target).SequenceEqual(bytes))
            throw new IOException("The hosts file didn't keep what Tman wrote. Antivirus software may have changed it straight after saving.");
    }

    static string WriteBackup(byte[] bytes, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(backupDir, $"hosts-{stamp}.bak");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(backupDir, $"hosts-{stamp}-{i}.bak");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public sealed record Backup(string Path, DateTime Time, long Size, int Entries)
    {
        public string TimeText => Time.ToString("g");
        public string SizeText => Fmt.Bytes(Size);
    }

    public static List<Backup> ListBackups(string backupDir)
    {
        if (!Directory.Exists(backupDir)) return new List<Backup>();
        return Directory.GetFiles(backupDir, "hosts-*.bak")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new Backup(f.FullName, f.LastWriteTime, f.Length, Parse(File.ReadAllBytes(f.FullName)).Entries.Count()))
            .ToList();
    }

    // ---- Validating ----

    public static bool IsIp(string s)
    {
        if (s.Contains('%') || s.Contains('[') || !IPAddress.TryParse(s, out var ip)) return false;
        return ip.AddressFamily == AddressFamily.InterNetworkV6 || s.Count(c => c == '.') == 3;
    }

    public static bool IsHostName(string s)
    {
        if (s.Length == 0 || s.Length > 253) return false;
        foreach (var label in s.TrimEnd('.').Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        }
        return true;
    }

    /// <summary>Why this entry can't be saved, or null if it can.</summary>
    public string? Validate(string ip, string hosts, string comment)
    {
        if (!IsIp(ip)) return "That isn't a valid IP address. Use something like 192.168.1.10 or ::1.";
        var names = hosts.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0) return "Enter at least one host name.";
        foreach (var name in names)
        {
            if (name.Contains('*')) return $"\"{name}\" won't work: the hosts file has no wildcards, so each name needs its own entry.";
            if (name.Contains("://") || name.Contains('/')) return $"\"{name}\" won't work: use only the host name, such as example.com, without http:// or a path.";
            if (!IsHostName(name)) return $"\"{name}\" isn't a valid host name. Use letters, digits, hyphens and dots.";
        }
        if (comment.Contains('\r') || comment.Contains('\n')) return "A comment has to fit on one line.";
        if (!CanEncode(comment)) return "This comment has characters the file's encoding can't store. Try plain text.";
        return null;
    }

    /// <summary>A warning when an entry sends a well-known site anywhere but its real address, otherwise null.</summary>
    public static string? WarningFor(string ip, string hosts)
    {
        var site = hosts.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(h =>
            WellKnown.Any(d => h.TrimEnd('.').Equals(d, StringComparison.OrdinalIgnoreCase) || h.TrimEnd('.').EndsWith("." + d, StringComparison.OrdinalIgnoreCase)));
        if (site == null) return null;
        bool blocks = ip is "0.0.0.0" or "::" || (IPAddress.TryParse(ip, out var address) && IPAddress.IsLoopback(address));
        return blocks ? $"Blocks {site}" : $"Sends {site} to {ip}";
    }
}
