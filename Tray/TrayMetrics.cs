using System.Windows;
using System.Windows.Media;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Tray;

/// <summary>What one tray icon shows right now: a 0..1 level for the meter, a short number, and a tooltip.</summary>
public sealed record TrayReading(double Level, string Text, string Tip);

/// <summary>A metric that can live in the tray. <see cref="Read"/> returns null when the PC can't provide it.</summary>
public sealed record TrayMetric(string Key, string Name, string ColorKey, Func<Snapshot, TrayReading?> Read)
{
    public Color Color => Application.Current.TryFindResource(ColorKey) is Color c ? c : Color.FromRgb(0x3F, 0xC8, 0xFF);
}

public static class TrayMetrics
{
    static SystemMonitor Monitor => SystemMonitor.Instance;

    public static readonly TrayMetric[] All =
    {
        new("cpu", "CPU usage", "CpuColor", s =>
        {
            double ghz = s.Cpu.AvgMhz > 0 ? s.Cpu.AvgMhz / 1000 : s.Cpu.SpeedGhz;
            return new(s.Cpu.Total / 100, $"{s.Cpu.Total:0}", $"CPU {s.Cpu.Total:0}%" + (ghz > 0 ? $" at {ghz:0.00} GHz" : ""));
        }),
        new("mem", "Memory usage", "MemColor", s =>
            new(s.Mem.UsedPct / 100, $"{s.Mem.UsedPct:0}", $"Memory {s.Mem.UsedPct:0}%\n{Fmt.Bytes(s.Mem.Used)} of {Fmt.Bytes(s.Mem.Total)} in use")),
        new("gpu", "GPU usage", "GpuColor", s => Gpu(s) is { } g
            ? new(g.Usage / 100, $"{g.Usage:0}", $"GPU {g.Usage:0}%" + (g.Sensors?.TempC is { } t ? $" · {t:0} °C" : "") + $"\n{g.Name}")
            : null),
        new("gputemp", "GPU temperature", "GpuColor", s => Gpu(s) is { Sensors.TempC: { } t } g
            ? new(t / 100, $"{t:0}", $"GPU {t:0} °C\n{g.Name}")
            : null),
        new("disk", "Disk activity (system drive)", "DiskColor", s => SystemDisk(s) is { } d
            ? new(d.Active / 100, $"{d.Active:0}", $"Disk {d.Letters} {d.Active:0}% active\nRead {Fmt.Rate(d.ReadBps)} · Write {Fmt.Rate(d.WriteBps)}")
            : null),
        new("netdown", "Network download", "NetColor", s =>
        {
            double down = s.Nets.Sum(n => n.RecvBps), up = s.Nets.Sum(n => n.SendBps);
            return new(down / Peak("net.r"), Compact(down), $"Network\nDown {Fmt.Rate(down)} · Up {Fmt.Rate(up)}");
        }),
        new("netup", "Network upload", "NetColor", s =>
        {
            double down = s.Nets.Sum(n => n.RecvBps), up = s.Nets.Sum(n => n.SendBps);
            return new(up / Peak("net.s"), Compact(up), $"Network\nUp {Fmt.Rate(up)} · Down {Fmt.Rate(down)}");
        }),
        new("cpupower", "CPU power", "PowerColor", s => s.Power.Package is { } w
            ? new(w / Math.Max(50, Monitor.Series("power.pkg").Max() * 1.25), $"{w:0}", $"CPU package {Fmt.Watts(w)}" + (s.Power.Cores is { } c ? $"\nCores {Fmt.Watts(c)}" : ""))
            : null),
        new("cpuclock", "CPU clock", "CpuColor", s =>
        {
            double mhz = s.Cpu.AvgMhz > 0 ? s.Cpu.AvgMhz : s.Cpu.SpeedGhz * 1000;
            if (mhz <= 0) return null;
            double top = Math.Max(mhz, Monitor.Series("cpu.mhz.max").Max());
            return new(mhz / top, $"{mhz / 1000:0.0}", $"CPU clock {Fmt.Mhz(mhz)} average\nFastest core {Fmt.Mhz(s.Cpu.CoreMhz.DefaultIfEmpty(mhz).Max())}");
        }),
    };

    public static TrayMetric? Find(string key) => All.FirstOrDefault(m => m.Key == key);

    static GpuStat? Gpu(Snapshot s) => s.Gpus.OrderByDescending(g => g.DedTotal).FirstOrDefault();

    static DiskStat? SystemDisk(Snapshot s)
    {
        var sysDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows)[..2];
        return s.Disks.FirstOrDefault(d => d.Letters.Contains(sysDrive, StringComparison.OrdinalIgnoreCase)) ?? s.Disks.FirstOrDefault();
    }

    /// <summary>Rates have no natural 100%, so meters scale to the busiest moment of the last minute.</summary>
    static double Peak(string series) => Math.Max(125_000, Monitor.Series(series).Max());

    /// <summary>Byte rate squeezed into a few characters: "0", "48K", "1.2M", "35M", "1.1G".</summary>
    public static string Compact(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024, mb = kb / 1024;
        if (kb < 1) return "0";
        if (kb < 1000) return $"{kb:0}K";
        if (mb < 10) return $"{mb:0.0}M";
        if (mb < 1000) return $"{mb:0}M";
        return $"{mb / 1024:0.0}G";
    }
}
