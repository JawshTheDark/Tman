using System.Text;

namespace Tman.Core;

/// <summary>
/// Finds which loaded drivers contain a pool tag. Drivers carry their tags as plain bytes, so searching their files is the
/// usual way to match a tag to a driver. A short tag can match by chance, so a hit means "worth a look", not proof.
/// </summary>
public static class PoolTagFinder
{
    const long LargestFile = 256 * 1024 * 1024;

    /// <summary>File names of loaded drivers containing the tag. Blocks for a few seconds.</summary>
    public static List<string> Find(string tag, CancellationToken cancel = default)
    {
        var needle = Encoding.ASCII.GetBytes(tag.PadRight(4));
        var found = new List<string>();
        foreach (var path in LoadedDrivers())
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(path).Length > LargestFile) continue;
                if (File.ReadAllBytes(path).AsSpan().IndexOf(needle) >= 0) found.Add(Path.GetFileName(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Some drivers can't be read. They just can't be matched.
            }
        }
        return found;
    }

    static IEnumerable<string> LoadedDrivers()
    {
        var paths = Wmi.Query("SELECT PathName FROM Win32_SystemDriver WHERE State='Running'", @"root\cimv2")
            .Select(o => Normalize(o.Str("PathName"))).Where(p => p.Length > 0 && File.Exists(p));
        var kernel = Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe");
        return paths.Append(kernel).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Windows keeps driver paths in several forms, such as \??\C:\... and \SystemRoot\...</summary>
    public static string Normalize(string? path)
    {
        var text = (path ?? "").Trim().Trim('"');
        if (text.StartsWith(@"\??\")) text = text[4..];
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) text = Path.Combine(windows, text[@"\SystemRoot\".Length..]);
        else if (text.StartsWith("system32", StringComparison.OrdinalIgnoreCase)) text = Path.Combine(windows, text);
        return text;
    }
}
