using System.Text;
using Microsoft.Win32;

namespace Tman.Core;

/// <summary>Whether a path a registry entry points at is really gone, present, or can't be judged (network or unavailable drive).</summary>
public enum TargetStatus { Present, Missing, Unknown }

/// <summary>One broken registry entry the cleaner found. Deleting it removes a whole orphaned subkey, or a single value, depending on <see cref="WholeKey"/>.</summary>
public sealed class RegFinding
{
    public required RegistryHive Hive { get; init; }
    /// <summary>Path below the hive root, e.g. <c>SOFTWARE\...\Uninstall\Foo</c>.</summary>
    public required string SubPath { get; init; }
    /// <summary>True: the finding is a whole orphaned subkey. False: one value named <see cref="ValueName"/> under <see cref="SubPath"/>.</summary>
    public required bool WholeKey { get; init; }
    public string ValueName { get; init; } = "";
    public required string Category { get; init; }
    public required string Name { get; init; }
    /// <summary>The file or folder the entry points at.</summary>
    public required string Points { get; init; }
    public required string Reason { get; init; }

    public string HiveLabel => Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
    public string FullKey => $@"{HiveLabel}\{SubPath}";
    public string Location => WholeKey ? FullKey : $@"{FullKey}\{ValueName}";
    public bool NeedsAdmin => Hive == RegistryHive.LocalMachine;
}

/// <summary>
/// Finds registry entries whose target file is gone, in a few well-defined places only: uninstall entries, App Paths,
/// Run keys, Applications file handlers and shared-DLL counters. It judges a target missing only when its drive is a
/// ready, fixed NTFS/ReFS volume and the file still isn't there; network and unavailable drives (including the FUSE
/// mounts that vanish when elevated) are reported Unknown and never flagged. Nothing is removed without a .reg backup.
/// Everything here blocks, so call it off the UI thread.
/// </summary>
public static class RegistryClean
{
    static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    static string HiveName(RegistryHive hive) => hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";

    // ---- Target checks ----

    /// <summary>Where a file or folder stands. Unknown for anything Tman can't safely judge, so those are never offered for removal.</summary>
    public static TargetStatus StatusOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return TargetStatus.Unknown;
        string p;
        try { p = Expand(path.Trim().Trim('"')); }
        catch { return TargetStatus.Unknown; }
        if (p.Length < 3 || p.StartsWith(@"\\")) return TargetStatus.Unknown;   // empty-ish or UNC
        if (!Path.IsPathRooted(p) || p[1] != ':') return TargetStatus.Unknown;  // relative, or found on PATH
        try
        {
            var drive = new DriveInfo(p[..3]);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return TargetStatus.Unknown;
            // FUSE mounts report their own format and FAT drives come and go, so only trust Windows' own disk formats.
            if (drive.DriveFormat is not ("NTFS" or "ReFS")) return TargetStatus.Unknown;
            // GetAttributes tells "not there" apart from "can't read it": a protected folder like WindowsApps denies
            // access, and a running Store app must never look broken just because a standard user can't stat its file.
            _ = File.GetAttributes(p);
            return TargetStatus.Present;
        }
        catch (FileNotFoundException) { return TargetStatus.Missing; }
        catch (DirectoryNotFoundException) { return TargetStatus.Missing; }
        catch { return TargetStatus.Unknown; }
    }

    /// <summary>The file a command line depends on, or null when it can't be pinned down. Handles quotes, rundll32/regsvr32 targets and icon indexes.</summary>
    public static string? TargetFile(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var cmd = command.Trim();
        string exe, rest;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) return null;
            exe = cmd[1..end];
            rest = cmd[(end + 1)..].Trim();
        }
        else
        {
            int sp = cmd.IndexOf(' ');
            exe = sp < 0 ? cmd : cmd[..sp];
            rest = sp < 0 ? "" : cmd[(sp + 1)..].Trim();
            // An unquoted path with arguments is ambiguous ("C:\Program Files\...") unless the first token already names a file.
            if (sp >= 0 && Path.GetExtension(exe).Length == 0) return null;
        }
        var name = Path.GetFileName(exe).ToLowerInvariant();
        if (name is "rundll32.exe" or "regsvr32.exe" && rest.Length > 0)
        {
            var arg = rest.Trim('"');
            int comma = arg.IndexOf(',');
            if (comma >= 0) arg = arg[..comma];
            arg = arg.Trim().Trim('"');
            return arg.Length > 0 ? arg : exe;
        }
        return exe;
    }

    /// <summary>A DisplayIcon or shortcut target, with any trailing ",0" icon index removed.</summary>
    static string? IconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        int comma = s.LastIndexOf(',');
        if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        return s.Trim().Trim('"');
    }

    // ---- Scanners ----

    public static List<RegFinding> Scan()
    {
        const string cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
        const string wow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion";
        const string cvUser = @"Software\Microsoft\Windows\CurrentVersion";
        var list = new List<RegFinding>();

        Collect(list, () => ScanUninstall(RegistryHive.LocalMachine, $@"{cv}\Uninstall"));
        Collect(list, () => ScanUninstall(RegistryHive.LocalMachine, $@"{wow}\Uninstall"));
        Collect(list, () => ScanUninstall(RegistryHive.CurrentUser, $@"{cvUser}\Uninstall"));
        Collect(list, () => ScanAppPaths(RegistryHive.LocalMachine, $@"{cv}\App Paths"));
        Collect(list, () => ScanAppPaths(RegistryHive.LocalMachine, $@"{wow}\App Paths"));
        Collect(list, () => ScanAppPaths(RegistryHive.CurrentUser, $@"{cvUser}\App Paths"));
        foreach (var run in new[] { "Run", "RunOnce" })
        {
            Collect(list, () => ScanRun(RegistryHive.LocalMachine, $@"{cv}\{run}"));
            Collect(list, () => ScanRun(RegistryHive.LocalMachine, $@"{wow}\{run}"));
            Collect(list, () => ScanRun(RegistryHive.CurrentUser, $@"{cvUser}\{run}"));
        }
        Collect(list, () => ScanApplications(RegistryHive.LocalMachine, @"SOFTWARE\Classes\Applications"));
        Collect(list, () => ScanApplications(RegistryHive.CurrentUser, @"Software\Classes\Applications"));
        Collect(list, () => ScanSharedDlls(RegistryHive.LocalMachine, $@"{cv}\SharedDLLs"));
        return list;
    }

    static void Collect(List<RegFinding> into, Func<List<RegFinding>> scan)
    {
        try { into.AddRange(scan()); } catch { }
    }

    internal static List<RegFinding> ScanUninstall(RegistryHive hive, string subPath)
    {
        var found = new List<RegFinding>();
        using var baseKey = Base(hive).OpenSubKey(subPath);
        if (baseKey == null) return found;
        foreach (var name in baseKey.GetSubKeyNames())
        {
            try
            {
                using var k = baseKey.OpenSubKey(name);
                if (k == null || k.GetValue("SystemComponent") is 1) continue;   // hidden Windows component
                var uninstall = (k.GetValue("UninstallString") as string ?? k.GetValue("QuietUninstallString") as string ?? "").Trim();
                bool msi = k.GetValue("WindowsInstaller") is 1 || uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase);

                var checks = new List<(string Path, TargetStatus St)>();
                void Add(string? p) { if (!string.IsNullOrWhiteSpace(p)) checks.Add((p!, StatusOf(p))); }
                Add(k.GetValue("InstallLocation") as string);
                Add(IconPath(k.GetValue("DisplayIcon") as string));
                if (!msi) Add(TargetFile(uninstall));

                if (checks.Count == 0 || checks.Any(c => c.St == TargetStatus.Present) || checks.All(c => c.St == TargetStatus.Unknown)) continue;
                var missing = checks.First(c => c.St == TargetStatus.Missing);
                found.Add(new RegFinding
                {
                    Hive = hive, SubPath = $@"{subPath}\{name}", WholeKey = true,
                    Category = "Uninstall entries", Name = k.GetValue("DisplayName") as string ?? name,
                    Points = missing.Path,
                    Reason = $"The program's files are gone ({missing.Path}), but its uninstall entry is still listed.",
                });
            }
            catch { }
        }
        return found;
    }

    internal static List<RegFinding> ScanAppPaths(RegistryHive hive, string subPath)
    {
        var found = new List<RegFinding>();
        using var baseKey = Base(hive).OpenSubKey(subPath);
        if (baseKey == null) return found;
        foreach (var name in baseKey.GetSubKeyNames())
        {
            try
            {
                using var k = baseKey.OpenSubKey(name);
                var target = TargetFile(k?.GetValue(null) as string);
                if (StatusOf(target) != TargetStatus.Missing) continue;
                found.Add(new RegFinding
                {
                    Hive = hive, SubPath = $@"{subPath}\{name}", WholeKey = true,
                    Category = "App Paths", Name = name,
                    Points = target!, Reason = $"This App Paths shortcut points to a program that's gone ({target}).",
                });
            }
            catch { }
        }
        return found;
    }

    internal static List<RegFinding> ScanRun(RegistryHive hive, string subPath)
    {
        var found = new List<RegFinding>();
        using var k = Base(hive).OpenSubKey(subPath);
        if (k == null) return found;
        foreach (var name in k.GetValueNames())
        {
            if (name.Length == 0) continue;
            try
            {
                var target = TargetFile(k.GetValue(name) as string);
                if (StatusOf(target) != TargetStatus.Missing) continue;
                found.Add(new RegFinding
                {
                    Hive = hive, SubPath = subPath, WholeKey = false, ValueName = name,
                    Category = "Startup (Run)", Name = name,
                    Points = target!, Reason = $"This startup entry runs a program that's gone ({target}).",
                });
            }
            catch { }
        }
        return found;
    }

    internal static List<RegFinding> ScanApplications(RegistryHive hive, string subPath)
    {
        var found = new List<RegFinding>();
        using var baseKey = Base(hive).OpenSubKey(subPath);
        if (baseKey == null) return found;
        foreach (var app in baseKey.GetSubKeyNames())
        {
            try
            {
                using var cmdKey = baseKey.OpenSubKey($@"{app}\shell\open\command");
                var target = TargetFile(cmdKey?.GetValue(null) as string);
                if (StatusOf(target) != TargetStatus.Missing) continue;
                found.Add(new RegFinding
                {
                    Hive = hive, SubPath = $@"{subPath}\{app}", WholeKey = true,
                    Category = "File associations", Name = app,
                    Points = target!, Reason = $"This file-type handler runs a program that's gone ({target}).",
                });
            }
            catch { }
        }
        return found;
    }

    internal static List<RegFinding> ScanSharedDlls(RegistryHive hive, string subPath)
    {
        var found = new List<RegFinding>();
        using var k = Base(hive).OpenSubKey(subPath);
        if (k == null) return found;
        foreach (var name in k.GetValueNames())
        {
            if (name.Length == 0) continue;
            try
            {
                if (StatusOf(name) != TargetStatus.Missing) continue;   // the value name is itself the file path
                found.Add(new RegFinding
                {
                    Hive = hive, SubPath = subPath, WholeKey = false, ValueName = name,
                    Category = "Shared DLLs", Name = Path.GetFileName(name),
                    Points = name, Reason = $"A shared-file counter points to a file that's gone ({name}).",
                });
            }
            catch { }
        }
        return found;
    }

    // ---- Backup, remove, restore ----

    /// <summary>Writes a .reg backup of every finding, so a one-click restore can put it back. Throws if the file can't be written.</summary>
    public static void Backup(IEnumerable<RegFinding> findings, string file)
    {
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var f in findings)
        {
            if (f.WholeKey) ExportKey(f.Hive, f.SubPath, sb);
            else ExportValue(f.Hive, f.SubPath, f.ValueName, sb);
        }
        File.WriteAllText(file, sb.ToString(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
    }

    static void ExportKey(RegistryHive hive, string subPath, StringBuilder sb)
    {
        using var k = Base(hive).OpenSubKey(subPath);
        if (k == null) return;
        sb.Append("\r\n[").Append(HiveName(hive)).Append('\\').Append(subPath).Append("]\r\n");
        foreach (var name in k.GetValueNames())
            AppendValue(sb, name, k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(name));
        foreach (var child in k.GetSubKeyNames())
            ExportKey(hive, $@"{subPath}\{child}", sb);
    }

    static void ExportValue(RegistryHive hive, string subPath, string valueName, StringBuilder sb)
    {
        using var k = Base(hive).OpenSubKey(subPath);
        var value = k?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (k == null || value == null) return;
        sb.Append("\r\n[").Append(HiveName(hive)).Append('\\').Append(subPath).Append("]\r\n");
        AppendValue(sb, valueName, value, k.GetValueKind(valueName));
    }

    static void AppendValue(StringBuilder sb, string name, object? value, RegistryValueKind kind)
    {
        if (value == null) return;
        string lhs = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
        switch (kind)
        {
            case RegistryValueKind.String:
                sb.Append(lhs).Append("=\"").Append(Escape(value.ToString() ?? "")).Append("\"\r\n");
                break;
            case RegistryValueKind.DWord:
                sb.Append(lhs).Append("=dword:").Append(unchecked((uint)Convert.ToInt32(value)).ToString("x8")).Append("\r\n");
                break;
            case RegistryValueKind.ExpandString:
                sb.Append(lhs).Append("=hex(2):").Append(Hex(Encoding.Unicode.GetBytes((value.ToString() ?? "") + "\0"))).Append("\r\n");
                break;
            case RegistryValueKind.MultiString:
                sb.Append(lhs).Append("=hex(7):").Append(Hex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])value) + "\0\0"))).Append("\r\n");
                break;
            case RegistryValueKind.QWord:
                sb.Append(lhs).Append("=hex(b):").Append(Hex(BitConverter.GetBytes(Convert.ToInt64(value)))).Append("\r\n");
                break;
            case RegistryValueKind.Binary:
                sb.Append(lhs).Append("=hex:").Append(Hex((byte[])value)).Append("\r\n");
                break;
        }
    }

    /// <summary>Replaces %VAR% with its value, leaving an unknown name in place. Most values read from the registry are expanded already; this covers the rest.</summary>
    static string Expand(string s)
    {
        if (!s.Contains('%')) return s;
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf('%', i);
            if (open < 0) { sb.Append(s, i, s.Length - i); break; }
            int close = s.IndexOf('%', open + 1);
            if (close < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, open - i);
            var name = s.Substring(open + 1, close - open - 1);
            sb.Append(Environment.GetEnvironmentVariable(name) ?? s.Substring(open, close - open + 1));
            i = close + 1;
        }
        return sb.ToString();
    }

    static string Hex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2")));
    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Removes the given entries. Returns one message per entry that couldn't be removed, empty on full success.</summary>
    public static List<string> Delete(IEnumerable<RegFinding> findings)
    {
        var errors = new List<string>();
        foreach (var f in findings)
        {
            try
            {
                using var hive = Base(f.Hive);
                if (f.WholeKey)
                    hive.DeleteSubKeyTree(f.SubPath, throwOnMissingSubKey: false);
                else
                {
                    using var k = hive.OpenSubKey(f.SubPath, writable: true);
                    k?.DeleteValue(f.ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{f.Name}: {ex.Message}");
            }
        }
        return errors;
    }

    /// <summary>Puts back everything in a backup written by <see cref="Backup"/>. Returns an error message, or null on success. HKLM entries need admin.</summary>
    public static string? Restore(string file)
    {
        if (!File.Exists(file)) return "The backup file is gone.";
        var result = Cmd.Run(Cmd.System("reg.exe"), $"import \"{file}\"");
        if (result.Ok) return null;
        var text = result.Output.Trim();
        return text.Length > 0 ? text : "Windows couldn't import the backup. Entries under HKLM need administrator rights to restore.";
    }
}
