using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Tman.Core;

public enum MenuEntryKind { Command, Extension, Modern }

/// <summary>What a command entry does, as the user typed it.</summary>
public sealed record MenuCommand(string Title, string Command, string? Icon, bool ShiftOnly);

/// <summary>One line of a right-click menu.</summary>
public sealed class MenuEntry
{
    public required MenuEntryKind Kind { get; init; }

    /// <summary>Stable across reloads, for keeping the selection.</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> Places { get; init; }
    public required string From { get; init; }
    public required string Detail { get; init; }
    public bool WindowsOwned { get; init; }
    public bool Hidden { get; init; }
    public bool ShiftOnly { get; init; }

    /// <summary>Added on the Context menus page, so it can be changed or deleted there.</summary>
    public bool Custom { get; init; }

    /// <summary>Why this entry can't be hidden, or null when it can.</summary>
    public string? LockReason { get; init; }

    public string? ClassName { get; init; }
    public string? Verb { get; init; }
    public string? Clsid { get; init; }
    public string? RawCommand { get; init; }
    public string? RawIcon { get; init; }

    public string PlacesText => string.Join(", ", Places);
    public string KindText => Kind switch { MenuEntryKind.Command => "Command", MenuEntryKind.Extension => "Shell extension", _ => "Windows 11 app" };
    public string StateText => Hidden ? "Hidden" : ShiftOnly ? "Shift + right-click" : "Shown";

    /// <summary>Hiding a shell extension or Windows 11 entry only takes effect once Explorer reloads it.</summary>
    public bool NeedsExplorerRestart => Kind != MenuEntryKind.Command;
}

/// <summary>
/// Reads and changes what appears in Explorer's right-click menus. Nothing here deletes an entry that Tman didn't add: hiding is
/// a marker that can be taken off again. Commands are hidden per location, through a per-user key that needs no admin rights.
/// Shell extensions and Windows 11 entries go on Windows' block list, which is per user but applies wherever the handler is used.
/// </summary>
public static class ContextMenus
{
    /// <summary>The places that have their own menu, in the order Tman lists them.</summary>
    public static readonly (string Label, string ClassName)[] Locations =
    {
        ("All files", "*"),
        ("Files and folders", "AllFilesystemObjects"),
        ("Folders", "Directory"),
        ("Folders and drives", "Folder"),
        ("Folder background", @"Directory\Background"),
        ("Desktop", "DesktopBackground"),
        ("Drives", "Drive"),
        ("Programs", "exefile"),
        ("Shortcuts", "lnkfile"),
        ("Pictures", @"SystemFileAssociations\image"),
        ("Videos", @"SystemFileAssociations\video"),
        ("Audio", @"SystemFileAssociations\audio"),
        ("Recycle Bin", @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}"),
    };

    const string BlockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    const string PackagesPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    const string HiddenByTman = "TmanHidden";
    const string AddedByTman = "TmanEntry";

    // Redirected by tests, so they can run against a throwaway key.
    internal static string UserRoot { get; set; } = @"Software\Classes";
    internal static string MachineRoot { get; set; } = @"SOFTWARE\Classes";
    internal static RegistryKey MachineHive { get; set; } = Registry.LocalMachine;
    internal static string UserBlocked { get; set; } = BlockedPath;
    internal static string MachineBlocked { get; set; } = BlockedPath;
    internal static bool ReadPackages { get; set; } = true;

    /// <summary>Windows opens items through these, so hiding one can stop programs or folders opening at all.</summary>
    static readonly HashSet<string> EssentialVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "explore", "runas", "opennewwindow", "opennewtab", "opennewprocess",
    };

    static readonly Dictionary<string, string> VerbNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = "Open", ["runas"] = "Run as administrator", ["runasuser"] = "Run as a different user", ["find"] = "Search",
        ["print"] = "Print", ["edit"] = "Edit", ["explore"] = "Explore", ["pintohome"] = "Pin to Quick access",
        ["cmd"] = "Open command window here", ["Powershell"] = "Open PowerShell window here", ["setdesktopwallpaper"] = "Set as desktop background",
    };

    /// <summary>
    /// Windows' own menu handlers that are safe to block because they only add menu items. Any other Windows handler stays
    /// locked: the block applies to the handler everywhere, and some also do other work (the one for shortcuts, for example).
    /// </summary>
    static readonly Dictionary<string, string> SafeWindowsHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{f81e9010-6ea4-11ce-a7ff-00aa003ca9f6}"] = "Give access to",
        ["{3dad6c5d-2167-4cae-9914-f99e41c12cfa}"] = "Include in library",
        ["{596AB062-B4D2-4215-9F74-E9109B0A8153}"] = "Restore previous versions",
        ["{e2bf9676-5f8f-435c-97eb-11607a5bedf7}"] = "Share",
        ["{1d27f844-3a1f-4410-85ac-14651078412d}"] = "Troubleshoot compatibility",
        ["{7AD84985-87B4-4a16-BE58-8B72A5B390F7}"] = "Cast to Device",
        ["{09A47860-11B0-4DA5-AFA5-26D86198A780}"] = "Scan with Microsoft Defender",
        ["{E61BF828-5E63-4287-BEF1-60B1A4FDE0E3}"] = "Work Folders",
        ["{474C98EE-CF3D-41f5-80E3-4AAB0AB04301}"] = "Offline Files",
        ["{A470F8CF-A1E8-4f65-8335-227475AA5C46}"] = "Encrypt",
        ["{470C0EBD-5D73-4d58-9CED-E91E22E23282}"] = "Pin to Start",
        ["{f3d06e7c-1e45-4a26-847e-f9fcdee59be0}"] = "Copy as path",
        ["{0bf754aa-c967-445c-ab3d-d8fda9bae7ef}"] = "Next desktop background",
    };

    /// <summary>Names for entries whose internal ID doesn't read as what the menu shows.</summary>
    static readonly Dictionary<string, string> ModernNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{BFE0E2A4-C70C-4AD7-AC3D-10D1ECEBB5B4}"] = "Edit with Photos",
        ["{8AB635F8-9A67-4698-AB99-784AD929F3B4}"] = "Edit with Clipchamp",
        ["{DB6BACB1-FC31-4B28-9C53-7240A4F98404}"] = "Ask Copilot",
        ["{2430F218-B743-4FD6-97BF-5C76541B4AE9}"] = "Edit with Paint",
        ["{CA6CC9F1-867A-481E-951E-A28C5E4F01EA}"] = "Edit in Notepad",
        ["{E6950302-61F0-4FEB-97DB-855E30D4A991}"] = "Edit with Notepad++",
        ["{1C6DF0C0-192A-4451-BE36-6A59A86A692E}"] = "Open with Code",
        ["{9F156763-7844-4DC4-B2B1-901F640F5155}"] = "Open in Terminal",
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);

    // ---- Reading ----

    sealed record VerbData(string? Title, string? MuiVerb, string? Icon, string? Command, bool Delegate, string? ComHandler,
        bool Hidden, bool ShadowOnly, bool ShiftOnly, bool Programmatic, bool Custom);

    /// <summary>Every entry Tman can see. Blocks, so call it off the UI thread.</summary>
    public static List<MenuEntry> Read()
    {
        var blocked = ReadBlocked();
        var sources = new Dictionary<string, (string From, bool Windows)>(StringComparer.OrdinalIgnoreCase);
        var list = new List<MenuEntry>();
        var extensions = new Dictionary<string, (string KeyName, List<string> Places)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, className) in Locations)
        {
            var user = ReadVerbs(Registry.CurrentUser, UserRoot, className);
            var machine = ReadVerbs(MachineHive, MachineRoot, className);
            foreach (var verb in user.Keys.Concat(machine.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                user.TryGetValue(verb, out var u);
                machine.TryGetValue(verb, out var m);
                if (Command(label, className, verb, u, m, sources) is { } entry) list.Add(entry);
            }
            ReadHandlers(Registry.CurrentUser, UserRoot, className, label, extensions);
            ReadHandlers(MachineHive, MachineRoot, className, label, extensions);
        }

        foreach (var (clsid, (keyName, places)) in extensions)
            list.Add(Extension(clsid, keyName, places, blocked, sources));

        if (ReadPackages) list.AddRange(ReadModern(blocked));
        return list;
    }

    static Dictionary<string, VerbData> ReadVerbs(RegistryKey hive, string root, string className)
    {
        var verbs = new Dictionary<string, VerbData>(StringComparer.OrdinalIgnoreCase);
        using var shell = Open(hive, $@"{root}\{className}\shell");
        if (shell == null) return verbs;
        foreach (var name in shell.GetSubKeyNames())
        {
            try
            {
                using var k = shell.OpenSubKey(name);
                if (k == null) continue;
                using var command = k.OpenSubKey("command");
                var values = k.GetValueNames();
                bool Has(string value) => values.Contains(value, StringComparer.OrdinalIgnoreCase);
                verbs[name] = new VerbData(
                    k.GetValue("") as string, (k.GetValue("MUIVerb") ?? k.GetValue("MuiVerb")) as string,
                    k.GetValue("Icon", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    command?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    command?.GetValue("DelegateExecute") != null, k.GetValue("ExplorerCommandHandler") as string,
                    Has("LegacyDisable"), Has(HiddenByTman), Has("Extended"), Has("ProgrammaticAccessOnly"), Has(AddedByTman));
            }
            catch { }
        }
        return verbs;
    }

    static MenuEntry? Command(string label, string className, string verb, VerbData? user, VerbData? machine,
        Dictionary<string, (string, bool)> sources)
    {
        // A user key that only holds Tman's marker says nothing about the entry itself.
        var own = user is { ShadowOnly: false } ? user : null;
        var data = own ?? machine ?? user!;
        if (data.Programmatic) return null;

        string? server = data.Command != null ? ExeOf(data.Command) : data.ComHandler != null ? ServerOf(data.ComHandler) : null;
        bool builtIn = server == null && (data.Delegate || data.ComHandler != null);
        var (from, windows) = data.Custom ? ("Tman", false) : builtIn ? ("Windows", true) : Source(server, sources);
        return new MenuEntry
        {
            Kind = MenuEntryKind.Command,
            Key = $@"command|{className}|{verb}",
            Name = Title(data, verb),
            Places = new[] { label },
            From = from,
            WindowsOwned = windows,
            Detail = data.Command ?? (builtIn ? "Built into Windows" : data.ComHandler ?? "Opens a submenu"),
            Hidden = (user?.Hidden ?? false) || (machine?.Hidden ?? false),
            ShiftOnly = data.ShiftOnly,
            Custom = data.Custom,
            LockReason = EssentialVerbs.Contains(verb) ? "Windows uses this to open things, so Tman won't hide it." : null,
            ClassName = className,
            Verb = verb,
            RawCommand = data.Command,
            RawIcon = data.Icon,
        };
    }

    static string Title(VerbData data, string verb)
    {
        foreach (var candidate in new[] { data.Title, data.MuiVerb })
            if (!string.IsNullOrWhiteSpace(candidate) && Resolve(candidate) is { Length: > 0 } text) return text;
        if (VerbNames.TryGetValue(verb, out var known)) return known;
        return Words(verb.TrimStart('.'));
    }

    /// <summary>Turns a resource reference into its text, and removes the access-key markers.</summary>
    static string Resolve(string text)
    {
        if (text.StartsWith('@'))
        {
            var buffer = new StringBuilder(512);
            text = SHLoadIndirectString(Environment.ExpandEnvironmentVariables(text), buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : "";
        }
        return text.Replace("&&", "\0").Replace("&", "").Replace("\0", "&").Trim();
    }

    /// <summary>Spaces out an internal name such as EditInPaint.</summary>
    static string Words(string name) => Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ');

    static void ReadHandlers(RegistryKey hive, string root, string className, string label, Dictionary<string, (string KeyName, List<string> Places)> found)
    {
        using var handlers = Open(hive, $@"{root}\{className}\shellex\ContextMenuHandlers");
        if (handlers == null) return;
        foreach (var name in handlers.GetSubKeyNames())
        {
            try
            {
                using var k = handlers.OpenSubKey(name);
                // The handler's ID is the key's value, or the key's own name when the value is a comment.
                var clsid = new[] { k?.GetValue("") as string, name }.FirstOrDefault(IsGuid);
                if (clsid == null) continue;
                if (!found.TryGetValue(clsid, out var item)) found[clsid] = item = (IsGuid(name) ? "" : name, new List<string>());
                if (!item.Places.Contains(label)) item.Places.Add(label);
            }
            catch { }
        }
    }

    static bool IsGuid(string? text) => text != null && Guid.TryParse(text, out _) && text.StartsWith('{');

    static MenuEntry Extension(string clsid, string keyName, List<string> places, HashSet<string> blocked, Dictionary<string, (string, bool)> sources)
    {
        var server = ServerOf(clsid);
        var (from, windows) = server == null ? ("Not installed", false) : Source(server, sources);
        SafeWindowsHandlers.TryGetValue(clsid, out var safeName);
        var name = safeName ?? (keyName.Length > 0 ? Words(keyName) : ComName(clsid) ?? clsid);
        return new MenuEntry
        {
            Kind = MenuEntryKind.Extension,
            Key = $"block|{clsid.ToLowerInvariant()}",
            Name = name,
            Places = places,
            From = from,
            WindowsOwned = windows,
            Detail = server ?? clsid,
            Hidden = blocked.Contains(clsid),
            LockReason = windows && safeName == null ? "Part of Windows, and it may do more than add a menu item, so Tman won't block it." : null,
            Clsid = clsid,
        };
    }

    static string? ServerOf(string clsid)
    {
        foreach (var root in new[] { "CLSID", @"WOW6432Node\CLSID" })
        {
            try
            {
                using var k = Registry.ClassesRoot.OpenSubKey($@"{root}\{clsid}\InprocServer32");
                if (k?.GetValue("") is string path && path.Length > 0) return ExeOf(path) ?? path;
            }
            catch { }
        }
        return null;
    }

    static string? ComName(string clsid)
    {
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
            return k?.GetValue("") is string { Length: > 0 } name ? name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The program a command line starts, with variables expanded.</summary>
    static string? ExeOf(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command).Trim();
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        int space = text.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        return space > 0 ? text[..(space + 4)] : text;
    }

    /// <summary>Who a program or library comes from, going by where it is and what its version information says.</summary>
    static (string From, bool Windows) Source(string? path, Dictionary<string, (string, bool)> cache)
    {
        if (string.IsNullOrEmpty(path)) return ("Unknown", false);
        if (cache.TryGetValue(path, out var known)) return known;
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // A bare name such as cmd.exe or shell32.dll comes from the system folder.
        var full = Path.IsPathRooted(path) ? path : Path.Combine(Environment.SystemDirectory, path);
        string from = "", company = "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(full);
            company = (info.CompanyName ?? "").Trim();
            from = (info.ProductName ?? "").Trim();
            if (from.Length == 0) from = company;
        }
        catch { }
        // Drivers put their menu handlers in the system folder too, so the folder alone doesn't make something part of Windows.
        bool inWindows = full.StartsWith(windowsDir + "\\", StringComparison.OrdinalIgnoreCase) && (company.Length == 0 || company.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase));
        var result = inWindows || full.Contains(@"\Windows Defender\", StringComparison.OrdinalIgnoreCase)
            ? ("Windows", true)
            : (from.Length > 0 ? from : Path.GetFileNameWithoutExtension(full), false);
        cache[path] = result;
        return result;
    }

    static HashSet<string> ReadBlocked()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[] { (Registry.CurrentUser, UserBlocked), (MachineHive, MachineBlocked) })
        {
            using var k = Open(hive, path);
            if (k != null) blocked.UnionWith(k.GetValueNames());
        }
        return blocked;
    }

    /// <summary>Entries Windows 11 apps declare in their manifests. Windows asks each one for its title, so the name is worked out from its ID.</summary>
    static IEnumerable<MenuEntry> ReadModern(HashSet<string> blocked)
    {
        var verbs = new Dictionary<string, (string Name, string From, string Root, HashSet<string> Types)>(StringComparer.OrdinalIgnoreCase);
        using var packages = Open(Registry.CurrentUser, PackagesPath);
        if (packages == null) yield break;
        foreach (var fullName in packages.GetSubKeyNames())
        {
            try
            {
                using var k = packages.OpenSubKey(fullName);
                if (k?.GetValue("PackageRootFolder") is not string root) continue;
                // Apps installed outside the Store keep their manifest apart from their files.
                var manifest = new[] { root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", fullName) }
                    .Select(folder => Path.Combine(folder, "AppxManifest.xml")).FirstOrDefault(File.Exists);
                if (manifest == null) continue;
                var text = File.ReadAllText(manifest);
                if (!text.Contains("FileExplorerContextMenus", StringComparison.Ordinal)) continue;

                var from = PackageName(fullName, k.GetValue("DisplayName") as string);
                foreach (var item in XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "FileExplorerContextMenus").SelectMany(e => e.Elements()))
                {
                    if (item.Name.LocalName != "ItemType") continue;
                    var type = (string?)item.Attribute("Type") ?? "";
                    foreach (var verb in item.Elements().Where(e => e.Name.LocalName == "Verb"))
                    {
                        if ((string?)verb.Attribute("Clsid") is not { Length: > 0 } id) continue;
                        var clsid = "{" + id.Trim('{', '}').ToUpperInvariant() + "}";
                        if (!verbs.TryGetValue(clsid, out var entry))
                            verbs[clsid] = entry = (Words((string?)verb.Attribute("Id") ?? id), from, root, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        entry.Types.Add(type);
                    }
                }
            }
            catch { }
        }

        foreach (var (clsid, (name, from, root, types)) in verbs)
        {
            yield return new MenuEntry
            {
                Kind = MenuEntryKind.Modern,
                Key = $"block|{clsid.ToLowerInvariant()}",
                Name = ModernNames.GetValueOrDefault(clsid, name),
                Places = Places(types),
                From = from,
                Detail = root,
                Hidden = blocked.Contains(clsid),
                Clsid = clsid,
            };
        }
    }

    static string PackageName(string fullName, string? displayName)
    {
        if (!string.IsNullOrEmpty(displayName) && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return displayName;
        var name = fullName.Split('_')[0];
        if (displayName != null)
        {
            var key = displayName["ms-resource:".Length..];
            var buffer = new StringBuilder(256);
            foreach (var path in new[] { $"ms-resource://{name}/Resources/{key}", $"ms-resource://{name}/{key}" })
                if (SHLoadIndirectString($"@{{{fullName}?{path}}}", buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0)
                    return buffer.ToString();
        }
        return Words(name[(name.LastIndexOf('.') + 1)..]);
    }

    static List<string> Places(HashSet<string> types)
    {
        var places = new List<string>();
        if (types.Contains("*")) places.Add("All files");
        if (types.Contains("Directory")) places.Add("Folders");
        if (types.Contains(@"Directory\Background")) places.Add("Folder background");
        int files = types.Count(t => t.StartsWith('.'));
        if (files > 0) places.Add(files == 1 ? types.First(t => t.StartsWith('.')) : $"{files} file types");
        return places;
    }

    static RegistryKey? Open(RegistryKey hive, string path, bool writable = false)
    {
        try
        {
            return hive.OpenSubKey(path, writable);
        }
        catch when (!writable)
        {
            return null;
        }
    }

    // ---- Changing ----

    /// <summary>Hides or restores an entry. Returns an error message, or null on success.</summary>
    public static string? SetHidden(MenuEntry entry, bool hide)
    {
        if (hide && entry.LockReason != null) return entry.LockReason;
        return Try(() =>
        {
            if (entry.Kind == MenuEntryKind.Command) SetCommandHidden(entry, hide);
            else SetBlocked(entry.Clsid!, hide);
        });
    }

    static void SetCommandHidden(MenuEntry entry, bool hide)
    {
        var user = $@"{UserRoot}\{entry.ClassName}\shell\{entry.Verb}";
        var machine = $@"{MachineRoot}\{entry.ClassName}\shell\{entry.Verb}";
        using var existing = Open(Registry.CurrentUser, user, writable: true);
        bool mine = existing?.GetValue(HiddenByTman) != null;
        if (hide)
        {
            using var k = Registry.CurrentUser.CreateSubKey(user, writable: true);
            k.SetValue("LegacyDisable", "", RegistryValueKind.String);
            // Marks a key that exists only to hide an entry from the machine's list, so showing it again can remove the key.
            if (existing == null) k.SetValue(HiddenByTman, "", RegistryValueKind.String);
            return;
        }

        if (mine)
        {
            existing!.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(user, throwOnMissingSubKey: false);
        }
        else
        {
            existing?.DeleteValue("LegacyDisable", throwOnMissingValue: false);
        }
        // Windows and some apps ship entries already hidden this way.
        using var read = Open(MachineHive, machine);
        if (read?.GetValue("LegacyDisable") == null) return;
        using var write = MachineHive.OpenSubKey(machine, writable: true);
        write?.DeleteValue("LegacyDisable", throwOnMissingValue: false);
    }

    static void SetBlocked(string clsid, bool block)
    {
        if (block)
        {
            using var k = Registry.CurrentUser.CreateSubKey(UserBlocked, writable: true);
            k.SetValue(clsid, "", RegistryValueKind.String);
            return;
        }
        foreach (var (hive, path) in new[] { (Registry.CurrentUser, UserBlocked), (MachineHive, MachineBlocked) })
        {
            using var read = Open(hive, path);
            var name = read?.GetValueNames().FirstOrDefault(n => n.Equals(clsid, StringComparison.OrdinalIgnoreCase));
            if (name == null) continue;
            using var write = hive.OpenSubKey(path, writable: true);
            write?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>Shows a command only when Shift is held. Returns an error message, or null on success.</summary>
    public static string? SetShiftOnly(MenuEntry entry, bool shiftOnly) => Try(() =>
    {
        var user = $@"{UserRoot}\{entry.ClassName}\shell\{entry.Verb}";
        using var inUser = Open(Registry.CurrentUser, user, writable: true);
        // A key holding only Tman's marker isn't where the entry lives.
        using var key = inUser != null && inUser.GetValue(HiddenByTman) == null
            ? null
            : MachineHive.OpenSubKey($@"{MachineRoot}\{entry.ClassName}\shell\{entry.Verb}", writable: true);
        var target = key ?? inUser ?? throw new InvalidOperationException("This entry no longer exists.");
        if (shiftOnly) target.SetValue("Extended", "", RegistryValueKind.String);
        else target.DeleteValue("Extended", throwOnMissingValue: false);
    });

    /// <summary>Adds a command to each location, for this user. Returns an error message, or null on success.</summary>
    public static string? Add(MenuCommand command, IEnumerable<string> classNames) => Try(() =>
    {
        var verb = "Tman." + Guid.NewGuid().ToString("N")[..8];
        foreach (var className in classNames) Write($@"{UserRoot}\{className}\shell\{verb}", command);
    });

    /// <summary>Changes a command added here. Returns an error message, or null on success.</summary>
    public static string? Update(MenuEntry entry, MenuCommand command) =>
        !entry.Custom ? "Only entries added here can be edited." : Try(() => Write($@"{UserRoot}\{entry.ClassName}\shell\{entry.Verb}", command));

    /// <summary>Deletes a command added here. Returns an error message, or null on success.</summary>
    public static string? Delete(MenuEntry entry) =>
        !entry.Custom ? "Only entries added here can be deleted." : Try(() => Registry.CurrentUser.DeleteSubKeyTree($@"{UserRoot}\{entry.ClassName}\shell\{entry.Verb}", throwOnMissingSubKey: false));

    static void Write(string path, MenuCommand command)
    {
        using var k = Registry.CurrentUser.CreateSubKey(path, writable: true);
        k.SetValue("", command.Title, RegistryValueKind.String);
        k.SetValue(AddedByTman, "", RegistryValueKind.String);
        if (string.IsNullOrWhiteSpace(command.Icon)) k.DeleteValue("Icon", throwOnMissingValue: false);
        else k.SetValue("Icon", command.Icon, RegistryValueKind.String);
        if (command.ShiftOnly) k.SetValue("Extended", "", RegistryValueKind.String);
        else k.DeleteValue("Extended", throwOnMissingValue: false);
        using var c = k.CreateSubKey("command", writable: true);
        c.SetValue("", command.Command, RegistryValueKind.String);
    }

    static string? Try(Action act)
    {
        try
        {
            act();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "Windows refused the change. This entry may need administrator rights, or Windows may be protecting it.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
