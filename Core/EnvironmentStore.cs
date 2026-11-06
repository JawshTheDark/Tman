using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Tman.Core;

public enum EnvScope { User, System }

public sealed record EnvVariable(EnvScope Scope, string Name, string Value, RegistryValueKind Kind)
{
    public string ScopeText => Scope.ToString();
    public bool IsString => Kind is RegistryValueKind.String or RegistryValueKind.ExpandString;
    public bool IsPath => Name.Equals(EnvironmentStore.PathName, StringComparison.OrdinalIgnoreCase);
    public string KindText => Kind switch
    {
        RegistryValueKind.ExpandString => "Expandable",
        RegistryValueKind.String => "Text",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// User and system environment variables, read raw from the registry so %NAME% references and REG_EXPAND_SZ survive an edit.
/// Every write saves a dated text backup first and tells running apps afterwards. Values are never logged.
/// </summary>
public static class EnvironmentStore
{
    public const string PathName = "Path";

    const string UserKey = "Environment";
    const string SystemKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public static string BackupFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "backups");

    static RegistryKey Root(EnvScope scope) => scope == EnvScope.User ? Registry.CurrentUser : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    static string KeyPath(EnvScope scope) => scope == EnvScope.User ? UserKey : SystemKey;

    static string KeyLabel(EnvScope scope) => scope == EnvScope.User ? $@"HKCU\{UserKey}" : $@"HKLM\{SystemKey}";

    public static List<EnvVariable> Read(EnvScope scope)
    {
        var list = new List<EnvVariable>();
        using var key = Root(scope).OpenSubKey(KeyPath(scope));
        if (key == null) return list;
        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0) continue;
            list.Add(ReadOne(key, scope, name));
        }
        return list;
    }

    public static EnvVariable? Find(EnvScope scope, string name)
    {
        using var key = Root(scope).OpenSubKey(KeyPath(scope));
        return key?.GetValueNames().FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } actual ? ReadOne(key, scope, actual) : null;
    }

    static EnvVariable ReadOne(RegistryKey key, EnvScope scope, string name)
    {
        var kind = key.GetValueKind(name);
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        // Anything that isn't text still needs a readable form, because backups are made from this value.
        string text = raw switch
        {
            string s => s,
            byte[] bytes => Convert.ToHexString(bytes),
            string[] lines => string.Join(Environment.NewLine, lines),
            _ => raw?.ToString() ?? "",
        };
        return new EnvVariable(scope, name, text, kind);
    }

    /// <summary>Backs up the old value, then writes the new one. Throws if either fails, and a failed backup stops the write.</summary>
    public static void Save(EnvScope scope, string name, string value, bool expandable)
    {
        Backup(scope, name);
        using var key = Root(scope).OpenSubKey(KeyPath(scope), writable: true) ?? Root(scope).CreateSubKey(KeyPath(scope), writable: true);
        key.SetValue(name, value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    /// <summary>Backs up the old value, then deletes the variable. Throws if either fails.</summary>
    public static void Delete(EnvScope scope, string name)
    {
        Backup(scope, name);
        using var key = Root(scope).OpenSubKey(KeyPath(scope), writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    /// <summary>Writes the current state of one variable to a dated text file and returns its path.</summary>
    static string Backup(EnvScope scope, string name)
    {
        var old = Find(scope, name);
        Directory.CreateDirectory(BackupFolder);
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var stem = $"environment-{DateTime.Now:yyyy-MM-dd-HHmmss}-{scope.ToString().ToLowerInvariant()}-{safeName}";
        var file = Path.Combine(BackupFolder, stem + ".txt");
        for (int i = 2; File.Exists(file); i++) file = Path.Combine(BackupFolder, $"{stem}-{i}.txt");

        var text = new StringBuilder();
        text.AppendLine("Tman backup of an environment variable, saved before changing it.");
        text.AppendLine($"Saved: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Where: {KeyLabel(scope)}");
        text.AppendLine($"Name: {name}");
        if (old == null)
        {
            text.AppendLine("This variable didn't exist yet, so there was no old value.");
        }
        else
        {
            text.AppendLine($"Type: {(old.Kind == RegistryValueKind.ExpandString ? "REG_EXPAND_SZ" : old.Kind == RegistryValueKind.String ? "REG_SZ" : old.Kind.ToString())}");
            text.AppendLine("Value:");
            text.AppendLine(old.Value);
        }
        File.WriteAllText(file, text.ToString(), Encoding.UTF8);
        return file;
    }

    /// <summary>Tells running apps, Explorer above all, that the environment changed. Can take seconds if an app is hung, so call it off the UI thread.</summary>
    public static void Broadcast() =>
        SendMessageTimeout(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "Environment", 0x2 /* SMTO_ABORTIFHUNG */, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>Splits a PATH value into its entries, dropping empty ones. Other than that, entries stay exactly as stored.</summary>
    public static List<string> SplitPath(string value) =>
        value.Split(';').Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

    public static string JoinPath(IEnumerable<string> entries) =>
        string.Join(";", entries.Select(e => e.Trim()).Where(e => e.Length > 0));

    /// <summary>The form two PATH entries are compared in: expanded, no quotes, no trailing slash, any case.</summary>
    public static string NormalizePathEntry(string entry) =>
        Environment.ExpandEnvironmentVariables(entry).Trim().Trim('"').TrimEnd('\\', '/').ToUpperInvariant();
}
