using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tman.Core;

public enum BatteryCondition { Unknown, Good, Fair, Worn, Replace }

/// <summary>One battery. Null means it wasn't reported. Capacities are in mWh; the rate is in mW, positive while charging and negative while draining.</summary>
public sealed class BatteryReading
{
    public string Key { get; set; } = "";
    public uint? Tag { get; set; }
    public string Name { get; set; } = "";
    public string Maker { get; set; } = "";
    public long? DesignMwh { get; set; }
    public long? FullMwh { get; set; }
    public long? RemainingMwh { get; set; }
    public long? VoltageMv { get; set; }
    public long? RateMw { get; set; }
    public long? Cycles { get; set; }
    public bool? Charging { get; set; }
    public bool? Discharging { get; set; }
    public bool? OnAc { get; set; }
    /// <summary>Windows' own charge figure, used when the capacities aren't available.</summary>
    public int? WindowsPercent { get; set; }
    /// <summary>Windows' own estimate of the time left on battery.</summary>
    public TimeSpan? WindowsRuntime { get; set; }
    /// <summary>A few batteries report capacity as a percentage instead of mWh, which rules out wear and rate sums.</summary>
    public bool Relative { get; set; }

    public bool Complete => DesignMwh != null && FullMwh != null && RemainingMwh != null && VoltageMv != null && RateMw != null && Cycles != null;

    public double? ChargePct =>
        !Relative && RemainingMwh is { } left && FullMwh is { } full && full > 0 ? Math.Min(100, left * 100.0 / full) : WindowsPercent;

    public double? HeldPct =>
        !Relative && DesignMwh is { } design && design > 0 && FullMwh is { } full && full > 0 ? full * 100.0 / design : null;

    /// <summary>How much capacity has been lost since new. A battery holding more than its rating counts as no wear.</summary>
    public double? WearPct => HeldPct is { } held ? Math.Max(0, 100 - held) : null;

    public double? Watts => RateMw is { } rate ? Math.Abs(rate) / 1000.0 : null;

    public string State =>
        Charging == true ? "Charging"
        : Discharging == true ? "On battery"
        : OnAc == true ? (ChargePct >= 99 ? "Fully charged" : "Plugged in, not charging")
        : OnAc == false ? "On battery"
        : "Not reported";

    /// <summary>Time until empty while draining, or until full while charging; null when neither applies or the numbers aren't there.</summary>
    public TimeSpan? TimeLeft
    {
        get
        {
            if (Discharging == true || (Charging != true && OnAc == false))
            {
                if (WindowsRuntime is { } estimate) return estimate;
                return !Relative && RemainingMwh is { } left && RateMw is { } rate && rate <= -100 ? TimeSpan.FromHours(left / (double)-rate) : null;
            }
            if (Charging == true && !Relative && RemainingMwh is { } now && FullMwh is { } full && RateMw is { } charge && charge >= 100)
                return TimeSpan.FromHours(Math.Max(0, full - now) / (double)charge);
            return null;
        }
    }

    public BatteryCondition Condition => WearPct switch
    {
        null => BatteryCondition.Unknown,
        < 10 => BatteryCondition.Good,
        < 20 => BatteryCondition.Fair,
        < 40 => BatteryCondition.Worn,
        _ => BatteryCondition.Replace,
    };
}

/// <summary>
/// Reads batteries from the classes in root\wmi and fills any gaps through IOCTL_BATTERY_QUERY_INFORMATION. Win32_Battery and
/// GetSystemPowerStatus are the last resort, so a laptop whose richer sources fail still shows its charge and not "no battery".
/// </summary>
public static class BatteryReader
{
    const string WmiScope = @"root\wmi";
    static readonly Guid BatteryInterface = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    const uint QueryTag = 0x294040, QueryInformation = 0x294044, QueryStatus = 0x29404C;
    const uint CapacityRelative = 0x40000000, UnknownValue = 0xFFFFFFFF;
    const uint PowerOnLine = 1, PowerDischarging = 2, PowerCharging = 4;
    const int InformationSize = 36, StatusSize = 16;

    public static List<BatteryReading> ReadAll()
    {
        var list = ReadWmi();
        if (list.Count == 0 || list.Any(b => !b.Complete))
        {
            try { Merge(list, ReadIoctl()); }
            catch { }
        }
        ApplyWin32Battery(list);
        if (list.Count == 0 && PowerPlans.Source() is { HasBattery: true } source)
            list.Add(new BatteryReading { WindowsPercent = source.Percent, OnAc = source.OnAc, WindowsRuntime = source.Remaining });
        return list;
    }

    static long? Num(ManagementBaseObject o, string property)
    {
        try { return o[property] is { } v ? Convert.ToInt64(v) : null; }
        catch { return null; }
    }

    /// <summary>Firmware says "don't know" with zero or all ones.</summary>
    internal static long? Reported(long? value) => value is > 0 and < UnknownValue ? value : null;

    static List<BatteryReading> ReadWmi()
    {
        var found = new Dictionary<string, BatteryReading>(StringComparer.OrdinalIgnoreCase);
        BatteryReading Entry(ManagementBaseObject o)
        {
            var key = o.Str("InstanceName");
            if (!found.TryGetValue(key, out var b)) found[key] = b = new BatteryReading { Key = key };
            if (Reported(Num(o, "Tag")) is { } tag) b.Tag = (uint)tag;
            return b;
        }

        foreach (var o in Wmi.Query("SELECT * FROM BatteryStaticData", WmiScope))
        {
            var b = Entry(o);
            b.Name = o.Str("DeviceName");
            b.Maker = o.Str("ManufactureName");
            b.DesignMwh = Reported(Num(o, "DesignedCapacity"));
            b.Relative = ((Num(o, "Capabilities") ?? 0) & CapacityRelative) != 0;
        }
        foreach (var o in Wmi.Query("SELECT * FROM BatteryFullChargedCapacity", WmiScope))
            Entry(o).FullMwh = Reported(Num(o, "FullChargedCapacity"));
        foreach (var o in Wmi.Query("SELECT * FROM BatteryCycleCount", WmiScope))
            Entry(o).Cycles = Reported(Num(o, "CycleCount"));
        foreach (var o in Wmi.Query("SELECT * FROM BatteryStatus", WmiScope))
        {
            var b = Entry(o);
            b.RemainingMwh = Reported(Num(o, "RemainingCapacity"));
            b.VoltageMv = Reported(Num(o, "Voltage"));
            b.Charging = o.Bool("Charging");
            b.Discharging = o.Bool("Discharging");
            b.OnAc = o.Bool("PowerOnline");
            b.RateMw = RateFrom(b.Charging, b.Discharging, Num(o, "ChargeRate"), Num(o, "DischargeRate"));
        }
        foreach (var o in Wmi.Query("SELECT * FROM BatteryRuntime", WmiScope))
            if (Reported(Num(o, "EstimatedRuntime")) is { } seconds and < 7 * 86400)
                Entry(o).WindowsRuntime = TimeSpan.FromSeconds(seconds);
        return found.Values.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>WMI keeps charge and discharge rates apart, and some firmware signs them oddly. Anything over 2 kW is a "don't know" value.</summary>
    internal static long? RateFrom(bool? charging, bool? discharging, long? chargeRate, long? dischargeRate)
    {
        static long? Size(long? v) => v is { } x && Math.Abs(x) < 2_000_000 ? Math.Abs(x) : null;
        return charging == true ? Size(chargeRate)
            : discharging == true ? -Size(dischargeRate)
            : charging == null && discharging == null ? null
            : 0;
    }

    static void Merge(List<BatteryReading> list, List<BatteryReading> extra)
    {
        if (list.Count == 0)
        {
            list.AddRange(extra);
            return;
        }
        foreach (var b in list)
        {
            var other = extra.FirstOrDefault(e => e.Tag != null && e.Tag == b.Tag) ?? (list.Count == 1 && extra.Count == 1 ? extra[0] : null);
            if (other == null) continue;
            b.DesignMwh ??= other.DesignMwh;
            b.FullMwh ??= other.FullMwh;
            b.RemainingMwh ??= other.RemainingMwh;
            b.VoltageMv ??= other.VoltageMv;
            b.RateMw ??= other.RateMw;
            b.Cycles ??= other.Cycles;
            b.Charging ??= other.Charging;
            b.Discharging ??= other.Discharging;
            b.OnAc ??= other.OnAc;
            b.Relative |= other.Relative;
        }
    }

    static void ApplyWin32Battery(List<BatteryReading> list)
    {
        var rows = Wmi.Query("SELECT Name, EstimatedChargeRemaining, EstimatedRunTime FROM Win32_Battery");
        if (list.Count == 0)
            list.AddRange(rows.Select(_ => new BatteryReading()));
        if (rows.Count != list.Count) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var b = list[i];
            if (b.Name.Length == 0) b.Name = rows[i].Str("Name");
            if (Num(rows[i], "EstimatedChargeRemaining") is { } percent and <= 100) b.WindowsPercent = (int)percent;
            // 71582788 minutes is how Win32_Battery says "no estimate".
            if (b.WindowsRuntime == null && Num(rows[i], "EstimatedRunTime") is { } minutes and > 0 and < 71582788)
                b.WindowsRuntime = TimeSpan.FromMinutes(minutes);
        }
    }

    static List<BatteryReading> ReadIoctl()
    {
        var list = new List<BatteryReading>();
        foreach (var path in DevicePaths(BatteryInterface))
        {
            using var h = CreateFileW(path, 0x80000000 /* GENERIC_READ */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;

            // A zero wait asks for the tag of whatever is in the slot now; tag 0 means the slot is empty.
            var tag = new byte[4];
            if (!DeviceIoControl(h, QueryTag, new byte[4], 4, tag, 4, out _, IntPtr.Zero) || BitConverter.ToUInt32(tag) == 0) continue;
            var b = new BatteryReading { Key = path, Tag = BitConverter.ToUInt32(tag) };

            // BATTERY_QUERY_INFORMATION: tag, level (0 is BatteryInformation), rate.
            var query = new byte[12];
            tag.CopyTo(query, 0);
            var information = new byte[InformationSize];
            if (DeviceIoControl(h, QueryInformation, query, query.Length, information, InformationSize, out var size, IntPtr.Zero) && size >= InformationSize)
                ApplyInformation(b, information);

            // BATTERY_WAIT_STATUS: all zero after the tag means "the status now", without waiting for a change.
            var wait = new byte[20];
            tag.CopyTo(wait, 0);
            var status = new byte[StatusSize];
            if (DeviceIoControl(h, QueryStatus, wait, wait.Length, status, StatusSize, out size, IntPtr.Zero) && size >= StatusSize)
                ApplyStatus(b, status);
            list.Add(b);
        }
        return list;
    }

    /// <summary>BATTERY_INFORMATION: Capabilities at 0, DesignedCapacity at 12, FullChargedCapacity at 16, CycleCount at 32.</summary>
    internal static void ApplyInformation(BatteryReading b, byte[] data)
    {
        b.Relative = (BitConverter.ToUInt32(data, 0) & CapacityRelative) != 0;
        b.DesignMwh = Reported(BitConverter.ToUInt32(data, 12));
        b.FullMwh = Reported(BitConverter.ToUInt32(data, 16));
        b.Cycles = Reported(BitConverter.ToUInt32(data, 32));
    }

    /// <summary>BATTERY_STATUS: PowerState at 0, Capacity at 4, Voltage at 8, signed Rate at 12.</summary>
    internal static void ApplyStatus(BatteryReading b, byte[] data)
    {
        uint state = BitConverter.ToUInt32(data, 0);
        b.OnAc = (state & PowerOnLine) != 0;
        b.Discharging = (state & PowerDischarging) != 0;
        b.Charging = (state & PowerCharging) != 0;
        b.RemainingMwh = Reported(BitConverter.ToUInt32(data, 4));
        b.VoltageMv = Reported(BitConverter.ToUInt32(data, 8));
        int rate = BitConverter.ToInt32(data, 12);
        b.RateMw = rate == int.MinValue ? null : rate;
    }

    /// <summary>The device paths of every present device exposing an interface.</summary>
    internal static List<string> DevicePaths(Guid deviceInterface)
    {
        const int Present = 2, DeviceInterface = 16;
        var paths = new List<string>();
        var set = SetupDiGetClassDevsW(ref deviceInterface, IntPtr.Zero, IntPtr.Zero, Present | DeviceInterface);
        if (set == new IntPtr(-1)) return paths;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { Size = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref deviceInterface, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                if (needed <= 0) continue;
                var detail = Marshal.AllocHGlobal(needed);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA: cbSize is the fixed part (8 on 64-bit, 6 on 32-bit), and the path starts at offset 4.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int Size;
        public Guid InterfaceClass;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int required, IntPtr deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
