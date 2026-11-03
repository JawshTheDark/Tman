namespace Tman.Core;

/// <summary>Display formatting for sizes, rates and durations.</summary>
public static class Fmt
{
    static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB" };
    static readonly string[] BitUnits = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };

    public static string Bytes(double v)
    {
        if (v < 0 || double.IsNaN(v)) v = 0;
        int i = 0;
        while (v >= 1000 && i < ByteUnits.Length - 1) { v /= 1024; i++; }
        if (i == 0) return $"{v:0} B";
        return v >= 100 ? $"{v:0} {ByteUnits[i]}" : $"{v:0.#} {ByteUnits[i]}";
    }

    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";

    public static string Bits(double bytesPerSec)
    {
        var b = Math.Max(0, bytesPerSec * 8);
        int i = 0;
        while (b >= 1000 && i < BitUnits.Length - 1) { b /= 1000; i++; }
        return b >= 100 || i == 0 ? $"{b:0} {BitUnits[i]}" : $"{b:0.#} {BitUnits[i]}";
    }

    public static string Mb(double bytes) => (bytes / 1048576.0).ToString("#,0.0") + " MB";

    public static string MbRate(double bytesPerSec)
    {
        var mb = bytesPerSec / 1048576.0;
        return mb < 0.05 ? "0 MB/s" : mb.ToString("0.0") + " MB/s";
    }

    public static string Pct(double v) => v < 0.05 ? "0%" : v >= 99.95 ? "100%" : v.ToString("0.0") + "%";

    public static string Uptime(TimeSpan t) => $"{(int)t.TotalDays}:{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>CPU-time style "h:mm:ss", as Task Manager shows it.</summary>
    public static string Duration(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Short human span: "42s", "5m 12s", "3h 4m", "2d 1h".</summary>
    public static string Span(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, t.TotalSeconds):0}s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
        : t.TotalHours < 24 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{(int)t.TotalDays}d {t.Hours}h";

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "Just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : t.ToString("d");
    }

    public static string Watts(double w) => w >= 100 ? $"{w:0} W" : $"{w:0.0} W";

    public static string Num(long n) => n.ToString("#,0");

    public static string Mhz(double mhz) => mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:0} MHz";
}
