using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tman.Core;

/// <summary>
/// Drives winget. It has no structured output for list, upgrade or search, so its text tables are parsed here. Its pipe
/// output is UTF-8 whatever the console code page, which is why this has its own runner instead of using <see cref="Cmd"/>.
/// </summary>
public static class WingetCli
{
    /// <summary>A table row. <see cref="Extra"/> is the column after Version: Available for list and upgrade, Match for search.</summary>
    public sealed record Row(string Name, string Id, string Version, string Extra, string Source, int Table)
    {
        /// <summary>Winget shortened the ID to fit its table, so it can't be passed back to winget as it stands.</summary>
        public bool IdCut => Id.EndsWith('…');
        public string IdStart => Id.TrimEnd('…');
    }

    public sealed record Result(int Code, List<string> Lines);

    public const int SearchLimit = 40;

    /// <summary>The app execution alias, which Explorer's PATH normally finds too. An elevated or stripped-down PATH may not.</summary>
    static readonly string Exe = FindExe();

    static string FindExe()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : "winget";
    }

    /// <summary>Winget found nothing to show, which is not a failure for a list or a search.</summary>
    public static bool IsNoMatch(int code) => (uint)code is 0x8A150014 or 0x8A15002B;

    public static bool IsValidId(string id) => id.Length is > 0 and <= 128 && Regex.IsMatch(id, @"^[\p{L}\p{N}][\p{L}\p{N}._+\-]*$");

    // Read-only queries stay on the winget source and accept nothing: only the msstore source has agreements to accept.
    public static Task<Result> ListAsync() => QueryAsync(60000, default, "list", "--source", "winget");
    public static Task<Result> UpgradesAsync() => QueryAsync(120000, default, "upgrade", "--source", "winget");
    public static Task<Result> SearchAsync(string text, CancellationToken cancel) =>
        QueryAsync(30000, cancel, "search", "--query", text, "--source", "winget", "--count", SearchLimit.ToString());

    public static Task<int> InstallAsync(string id, Action<string> onLine) => StreamAsync(Exe, new[]
        { "install", "--id", id, "--exact", "--source", "winget", "--no-upgrade", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" }, onLine);

    public static Task<int> UpgradeAsync(string id, Action<string> onLine) => StreamAsync(Exe, new[]
        { "upgrade", "--id", id, "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" }, onLine);

    public static Task<int> UninstallAsync(string id, Action<string> onLine) => StreamAsync(Exe, new[]
        { "uninstall", "--id", id, "--exact", "--source", "winget", "--silent", "--disable-interactivity" }, onLine);

    public static async Task<string?> VersionAsync()
    {
        var r = await RunAsync(Exe, new[] { "--version" }, 15000).ConfigureAwait(false);
        return r.Code == 0 ? r.Lines.LastOrDefault()?.Trim().TrimStart('v') : null;
    }

    /// <summary>Full IDs of what's installed, for rows where the table shortened the ID. Empty if winget export fails.</summary>
    public static async Task<List<string>> ExportedIdsAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tman-winget-{Guid.NewGuid():N}.json");
        try
        {
            await RunAsync(Exe, new[] { "export", "--output", path, "--source", "winget", "--disable-interactivity" }, 60000).ConfigureAwait(false);
            return File.Exists(path) ? ReadPackageIds(File.ReadAllText(path)).Winget : new List<string>();
        }
        catch { return new List<string>(); }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Reads a winget export file: the IDs from the winget source, and how many came from other sources. Throws on bad JSON.</summary>
    public static (List<string> Winget, int Other) ReadPackageIds(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var ids = new List<string>();
        int other = 0;
        foreach (var source in doc.RootElement.GetProperty("Sources").EnumerateArray())
        {
            bool fromWinget = !source.TryGetProperty("SourceDetails", out var details) || !details.TryGetProperty("Name", out var name)
                              || string.Equals(name.GetString(), "winget", StringComparison.OrdinalIgnoreCase);
            foreach (var package in source.GetProperty("Packages").EnumerateArray())
            {
                if (!fromWinget) other++;
                else if (package.GetProperty("PackageIdentifier").GetString() is { } id) ids.Add(id);
            }
        }
        return (ids, other);
    }

    static async Task<Result> QueryAsync(int timeoutMs, CancellationToken cancel, params string[] args)
    {
        args = args.Append("--disable-interactivity").ToArray();
        var result = await RunAsync(Exe, args, timeoutMs, cancel).ConfigureAwait(false);
        // Two winget processes starting at once can trip over each other, with an internal error from one of them.
        if ((uint)result.Code != 0x8A150001 || cancel.IsCancellationRequested) return result;
        await Task.Delay(500).ConfigureAwait(false);
        return await RunAsync(Exe, args, timeoutMs, cancel).ConfigureAwait(false);
    }

    /// <summary>Runs any tool that writes UTF-8 and collects its output. Never throws.</summary>
    public static async Task<Result> RunAsync(string file, IEnumerable<string> args, int timeoutMs, CancellationToken cancel = default)
    {
        var lines = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(timeoutMs);
        int code = await StreamAsync(file, args, line =>
        {
            lock (lines) lines.Add(line);
        }, timeout.Token).ConfigureAwait(false);
        return new Result(code, lines);
    }

    /// <summary>Like <see cref="Cmd.StreamAsync"/>, with UTF-8 decoding and arguments that need no quoting by the caller.</summary>
    public static async Task<int> StreamAsync(string file, IEnumerable<string> args, Action<string> onLine, CancellationToken cancel = default)
    {
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            process = Process.Start(info);
            if (process == null)
            {
                onLine($"Couldn't start {Path.GetFileName(file)}.");
                return Cmd.FailedToStart;
            }
            process.StandardInput.Close();
            var running = process;
            using var stop = cancel.Register(() =>
            {
                try { running.Kill(entireProcessTree: true); } catch { }
            });
            await Task.WhenAll(
                Pump(process.StandardOutput.BaseStream, onLine),
                Pump(process.StandardError.BaseStream, onLine),
                process.WaitForExitAsync()).ConfigureAwait(false);
            return cancel.IsCancellationRequested ? Cmd.Cancelled : process.ExitCode;
        }
        catch (Exception ex)
        {
            onLine($"Couldn't run {Path.GetFileName(file)}: {ex.Message}");
            return Cmd.FailedToStart;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>A carriage return ends a line too, so a spinner frame or a progress bar arrives as its own line.</summary>
    static async Task Pump(Stream stream, Action<string> onLine)
    {
        var bytes = new byte[4096];
        var chars = new char[8192];
        var line = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        int read;
        while ((read = await stream.ReadAsync(bytes).ConfigureAwait(false)) > 0)
        {
            int count = decoder.GetChars(bytes, 0, read, chars, 0);
            for (int i = 0; i < count; i++)
            {
                char c = chars[i];
                if (c != '\r' && c != '\n')
                {
                    line.Append(c);
                    continue;
                }
                if (line.Length > 0) onLine(line.ToString());
                line.Clear();
            }
        }
        if (line.Length > 0) onLine(line.ToString());
    }

    // ---- Output lines ----

    static readonly Regex Escapes = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07|[\x00-\x08\x0B-\x1F\x7F﻿]", RegexOptions.Compiled);
    static readonly Regex Amount = new(@"\d+(\.\d+)?\s*%|[\d.]+\s*[KMGT]i?B\s*/\s*[\d.]+\s*[KMGT]i?B", RegexOptions.Compiled);

    static string Clean(string line) => Escapes.Replace(line, "").TrimEnd();

    static bool IsRule(string line) => line.Length >= 8 && line.Trim().All(c => c == '-');

    static bool IsSpinner(string line) => line.All(c => c is ' ' or '-' or '\\' or '|' or '/');

    /// <summary>A line worth keeping in the log, with terminal control codes removed; null for spinner frames.</summary>
    public static string? LogLine(string line)
    {
        var clean = Clean(line);
        return clean.Length == 0 || (IsSpinner(clean) && !IsRule(clean)) ? null : clean;
    }

    /// <summary>The figures from a redrawn progress bar, such as "1.2 MB / 4.0 MB" or "40%"; null for any other line.</summary>
    public static string? ProgressOf(string line)
    {
        if (!line.Contains('█') && !line.Contains('▒') && !Regex.IsMatch(line, @"^\s*[\d.]+\s*(%|[KMGT]i?B\s*/)")) return null;
        return Amount.Match(line) is { Success: true } m ? m.Value : "";
    }

    // ---- Tables ----

    /// <summary>
    /// Reads every table in winget's output. Columns are found from the header line and measured in terminal cells, since
    /// CJK names take two cells per character. Header words change with the Windows language, so columns go by position.
    /// Anything that doesn't line up with the columns, such as "32 upgrades available.", is not a row.
    /// </summary>
    public static List<Row> ParseTables(IEnumerable<string> output)
    {
        var lines = output.Select(Clean).Where(l => l.Length > 0 && (IsRule(l) || !IsSpinner(l))).ToList();
        var rules = Enumerable.Range(1, Math.Max(0, lines.Count - 1)).Where(i => IsRule(lines[i])).ToList();
        var rows = new List<Row>();
        for (int t = 0; t < rules.Count; t++)
        {
            var starts = ColumnStarts(lines[rules[t] - 1]);
            if (starts.Length < 3) continue;
            int end = t + 1 < rules.Count ? rules[t + 1] - 1 : lines.Count;
            var cells = lines.Skip(rules[t] + 1).Take(end - rules[t] - 1)
                .Select(l => Cut(l, starts)).OfType<string[]>().ToList();

            // The last column is Source only when more than one source was searched; otherwise it's Available or Match.
            bool hasSource = starts.Length > 3 && cells.All(c => c[4].Trim() is "" or "winget" or "msstore" or "winget-font");
            foreach (var c in cells)
                rows.Add(new Row(c[0], c[1], c[2], (hasSource ? c[3] : c[3] + c[4]).Trim(), hasSource ? c[4].Trim() : "", t));
        }
        return rows;
    }

    static int[] ColumnStarts(string header)
    {
        // A spinner frame can be left in front of the header when output isn't split on carriage returns.
        var text = Regex.Replace(header, @"^[\s\-\\|/]+(?=\S)", "");
        var starts = new List<int> { 0 };
        foreach (Match word in Regex.Matches(text, @"\S+").Skip(1))
            starts.Add(CellsIn(text.AsSpan(0, word.Index)));
        return starts.ToArray();
    }

    /// <summary>Name, ID, version, then the rest split in front of the last column. Null if the line isn't a row.</summary>
    static string[]? Cut(string line, int[] starts)
    {
        var at = CellMap(line);
        if (!Aligned(at, line, starts) && !Realign(at, line, ref starts)) return null;
        var cols = starts;
        string Text(int from, int to) => line[IndexAt(at, line, cols[from])..(to < cols.Length ? IndexAt(at, line, cols[to]) : line.Length)];

        var id = Text(1, 2).Trim();
        var version = Text(2, 3).Trim();
        // Winget's own IDs have no spaces. Entries it only found in Windows' app lists can, and those start ARP\ or MSIX\.
        bool idOk = id.Length > 0 && (!id.Contains(' ') || id.StartsWith(@"ARP\") || id.StartsWith(@"MSIX\"));
        if (!idOk || version.Length == 0) return null;
        return new[] { Text(0, 1).Trim(), id, version, cols.Length > 3 ? Text(3, cols.Length - 1) : "", cols.Length > 3 ? Text(cols.Length - 1, cols.Length) : "" };
    }

    /// <summary>
    /// The ID, Version and last columns must each have a gap in front of them. Columns in between aren't checked, because
    /// a two-word header such as Korean's Available looks like two columns.
    /// </summary>
    static bool Aligned(int[] at, string line, int[] starts)
    {
        foreach (int i in new[] { 1, 2, starts.Length - 1 })
        {
            int before = starts[i] - 1;
            if (before < at.Length && line[at[before]] != ' ') return false;
        }
        return true;
    }

    /// <summary>
    /// When a name's characters don't measure the way winget measured them (some emoji, for example), the rest of the row
    /// sits a few cells off. Plain ASCII can't be off, which keeps sentences after a table from being taken for rows.
    /// </summary>
    static bool Realign(int[] at, string line, ref int[] starts)
    {
        if (line.All(char.IsAscii)) return false;
        for (int shift = 1; shift <= 8; shift++)
            foreach (int delta in new[] { -shift, shift })
            {
                var moved = starts.Select((s, i) => i == 0 ? 0 : s + delta).ToArray();
                if (moved[1] <= 0 || !Aligned(at, line, moved)) continue;
                int id = IndexAt(at, line, moved[1]);
                int to = IndexAt(at, line, moved[2]), end = moved.Length > 3 ? IndexAt(at, line, moved[3]) : line.Length;
                if (id >= line.Length || line[id] == ' ' || !line[to..end].Any(char.IsDigit)) continue;
                starts = moved;
                return true;
            }
        return false;
    }

    /// <summary>For each terminal cell the line fills, the index of the character in it.</summary>
    static int[] CellMap(string line)
    {
        var map = new List<int>(line.Length);
        for (int i = 0; i < line.Length;)
        {
            var rune = Rune.GetRuneAt(line, i);
            for (int w = Width(rune); w > 0; w--) map.Add(i);
            i += rune.Utf16SequenceLength;
        }
        return map.ToArray();
    }

    static int IndexAt(int[] at, string line, int cell) => cell < at.Length ? at[cell] : line.Length;

    static int CellsIn(ReadOnlySpan<char> text)
    {
        int cells = 0;
        foreach (var rune in text.EnumerateRunes()) cells += Width(rune);
        return cells;
    }

    static int Width(Rune r)
    {
        int c = r.Value;
        if (c < 0x300) return 1;
        if (Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        bool wide = c is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1F64F or >= 0x1F680 and <= 0x1F6FF
            or >= 0x1F900 and <= 0x1F9FF or >= 0x20000 and <= 0x3FFFD;
        return wide ? 2 : 1;
    }

    // ---- Results ----

    /// <summary>What an exit code means, in plain words.</summary>
    public static string Describe(int code)
    {
        if (code == Cmd.FailedToStart) return MissingText;
        if (code == Cmd.Cancelled) return "Winget took too long or was stopped.";
        return (uint)code switch
        {
            0 => "Done.",
            0x8A150001 => "Winget hit an internal error. Try again.",
            0x8A150005 or 0x8A15006A => "Winget was stopped before it finished.",
            0x8A150008 => "The download failed. Check your internet connection and try again.",
            0x8A15000A or 0x8A15000B or 0x8A15000F or 0x8A15003F => "Winget's app list is damaged. Use Repair winget in System repair.",
            0x8A150010 => "This app has no installer for this PC.",
            0x8A150011 => "The download didn't match what winget expected, so it wasn't run. The publisher may have just released a new version; try again later.",
            0x8A150014 => "Winget couldn't find this app. It may have been removed from the catalog.",
            0x8A150016 => "Winget found more than one match for this app.",
            0x8A150017 => "Winget found no version of this app that it can use.",
            0x8A15001B or 0x8A15003A => "Group Policy on this PC blocks this.",
            0x8A15002B => "No update that applies to this PC was found.",
            0x8A15002D => "A security check stopped the installer from running.",
            0x8A15002E => "The download was the wrong size. Try again later.",
            0x8A150030 => "The app's uninstaller failed.",
            0x8A150045 => "Winget couldn't open its app list. Check your internet connection, or use Repair winget in System repair.",
            0x8A150041 or 0x8A150046 => "The agreements weren't accepted.",
            0x8A150049 => "The Windows Installer package failed.",
            0x8A150050 => "Winget can't tell which version is installed, so it won't update this app by itself.",
            0x8A150061 => "It's already installed.",
            0x8A150068 => "A winget pin is stopping this update.",
            0x8A15006D => "A service winget needs is busy. Try again later.",
            0x8A15007D => "This app was installed for a standard user. Run Tman without administrator rights and try again.",
            0x8A150086 => "The download came back empty. Check your internet connection and try again.",
            0x8A15008E => "The update uses a different kind of installer. Uninstall this app, then install it again.",
            0x8A150101 or 0x8A150103 or 0x8A150111 => "The app or one of its files is in use. Close it and try again.",
            0x8A150102 => "Another installation is running. Try again when it finishes.",
            0x8A150104 or 0x8A150110 => "Something this app depends on is missing or wouldn't install.",
            0x8A150105 => "There isn't enough disk space.",
            0x8A150106 => "There isn't enough free memory. Close some apps and try again.",
            0x8A150107 => "This app needs an internet connection to install.",
            0x8A150108 or 0x8A150115 => "The app's installer reported an error.",
            0x8A150109 => "Restart your PC to finish.",
            0x8A15010A => "It failed. Restart your PC, then try again.",
            0x8A15010B => "Your PC will restart to finish.",
            0x8A15010C => "The installation was cancelled.",
            0x8A15010D => "Another version is already installed.",
            0x8A15010E => "A newer version is already installed.",
            0x8A15010F => "Your organization's policies block this.",
            0x8A150112 => "The installer rejected winget's settings.",
            0x8A150113 => "This app doesn't support this version of Windows.",
            0x8A150114 => "This app's installer can't update an existing copy. Uninstall it, then install it again.",
            uint other => $"It failed with error 0x{other:X8}. The log has the details.",
        };
    }

    /// <summary>Exit codes that mean the job did what was asked, or will once the PC restarts.</summary>
    public static bool Succeeded(int code) => (uint)code is 0 or 0x8A150109 or 0x8A15010B;

    public const string MissingText = "Winget isn't available on this PC. To get it back, open System repair and use Repair winget.";
}
