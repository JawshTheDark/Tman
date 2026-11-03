using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tman.Core;

/// <summary>Shell icon lookup with a per-path cache. Safe to call from background threads (results are frozen).</summary>
public static class Icons
{
    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static ImageSource? _defaultExe;

    public static ImageSource? DefaultExe => _defaultExe ??= FromShell(".exe", true);

    public static ImageSource? ForFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return DefaultExe;
        return Cache.GetOrAdd(path, p => FromShell(p, false) ?? DefaultExe);
    }

    /// <summary>Resolves an icon spec like <c>"C:\app.exe,0"</c> or <c>"C:\app.ico"</c>.</summary>
    public static ImageSource? ForSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        return Cache.GetOrAdd("spec:" + spec, _ =>
        {
            var s = Environment.ExpandEnvironmentVariables(spec.Trim());
            int index = 0;
            var comma = s.LastIndexOf(',');
            if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out var idx))
            {
                index = idx;
                s = s[..comma];
            }
            s = s.Trim().Trim('"');
            if (!File.Exists(s)) return null;
            if (index != 0)
            {
                try
                {
                    if (Native.ExtractIconExW(s, index, out var large, IntPtr.Zero, 1) > 0 && large != IntPtr.Zero)
                        return FromHIcon(large);
                }
                catch { }
            }
            return FromShell(s, false);
        });
    }

    static ImageSource? FromShell(string path, bool useAttributes)
    {
        try
        {
            var info = new Native.SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_LARGEICON | (useAttributes ? SHGFI_USEFILEATTRIBUTES : 0);
            var r = Native.SHGetFileInfoW(path, useAttributes ? FILE_ATTRIBUTE_NORMAL : 0, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            return FromHIcon(info.hIcon);
        }
        catch
        {
            return null;
        }
    }

    static ImageSource? FromHIcon(IntPtr hIcon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var copy = new WriteableBitmap(src);
            copy.Freeze();
            return copy;
        }
        catch
        {
            return null;
        }
        finally
        {
            Native.DestroyIcon(hIcon);
        }
    }
}
