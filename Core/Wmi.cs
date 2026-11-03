using System.Management;

namespace Tman.Core;

public static class Wmi
{
    public static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch
        {
            return new List<ManagementBaseObject>();
        }
    }

    public static string Str(this ManagementBaseObject o, string property)
    {
        try { return o[property]?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    public static ulong U64(this ManagementBaseObject o, string property)
    {
        try { return Convert.ToUInt64(o[property] ?? 0UL); }
        catch { return 0; }
    }

    public static bool? Bool(this ManagementBaseObject o, string property)
    {
        try { return o[property] is bool b ? b : null; }
        catch { return null; }
    }

    public static DateTime? Date(this ManagementBaseObject o, string property)
    {
        var s = o.Str(property);
        if (s.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
