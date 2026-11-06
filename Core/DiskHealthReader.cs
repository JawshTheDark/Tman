using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tman.Core;

public enum DriveVerdict { Unknown, Healthy, CheckSoon, Failing }

/// <summary>One physical drive. A null field means the drive didn't supply it, or this user can't read it. The serial number is deliberately left out.</summary>
public sealed class DriveHealth
{
    public int Number { get; set; } = -1;
    public string Model { get; set; } = "";
    public ulong Size { get; set; }
    public int BusType { get; set; }
    public int MediaType { get; set; }
    public bool? SeekPenalty { get; set; }
    public bool Opened { get; set; }
    public string PnpId { get; set; } = "";

    /// <summary>MSFT_PhysicalDisk.HealthStatus: 0 healthy, 1 warning, 2 unhealthy, 5 unknown.</summary>
    public int? WindowsHealth { get; set; }
    public bool? PredictFailure { get; set; }
    public byte? NvmeCritical { get; set; }

    public int? TemperatureC { get; set; }
    public int? TemperatureMaxC { get; set; }
    public int? TemperatureWarnC { get; set; }
    public int? WearPct { get; set; }
    public int? SparePct { get; set; }
    public int? SpareThresholdPct { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public double? BytesWritten { get; set; }
    public double? BytesRead { get; set; }
    public long? ReadErrors { get; set; }
    public long? WriteErrors { get; set; }
    /// <summary>Errors the drive couldn't correct: NVMe media errors, or the SMART uncorrectable counts.</summary>
    public long? DataErrors { get; set; }
    public long? ErrorLogEntries { get; set; }
    public long? ReallocatedSectors { get; set; }
    public long? PendingSectors { get; set; }

    public DriveVerdict Verdict { get; set; }
    public string Reason { get; set; } = "";

    public bool IsNvme => BusType == DiskHealthReader.BusNvme || NvmeCritical != null;
    public bool IsAta => BusType is 3 or 11 or 2;
    public bool IsSsd => MediaType is 4 or 5 || (MediaType == 0 && SeekPenalty == false);
    public bool IsHdd => MediaType == 3 || (MediaType == 0 && SeekPenalty == true);

    public string Type =>
        IsNvme ? "NVMe"
        : BusType switch
        {
            7 => "USB",
            8 => "RAID",
            12 => "SD card",
            13 => "eMMC",
            14 or 15 => "Virtual disk",
            16 => "Storage Spaces",
            19 => "UFS",
            _ => IsHdd ? "HDD" : IsSsd ? (IsAta ? "SATA SSD" : "SSD") : "Type not reported",
        };

    /// <summary>Plain reason this kind of drive can't report much, or null when that isn't the case.</summary>
    public string? Gap =>
        !Opened ? "Windows wouldn't let Tman open this drive, so its health data couldn't be read."
        : IsNvme ? null
        : BusType switch
        {
            7 => "USB drives usually don't pass health data through to Windows, so most of this can't be read.",
            8 => "Drives behind a RAID controller usually hide their health data from Windows.",
            14 or 15 or 16 => "This is a virtual disk, so it has no health data of its own.",
            _ => null,
        };
}

/// <summary>
/// Reads drive health. A handle opened with no access rights is enough for IOCTL_STORAGE_QUERY_PROPERTY, so temperature and
/// the whole NVMe health log need no admin. MSFT_StorageReliabilityCounter and the SMART classes in root\wmi do.
/// </summary>
public static class DiskHealthReader
{
    public const int BusNvme = 17;

    const string StorageScope = @"root\Microsoft\Windows\Storage";
    const uint QueryProperty = 0x2D1400, PredictFailure = 0x2D1100;
    const int DeviceProperty = 0, SeekPenaltyProperty = 7, ProtocolSpecificProperty = 50, TemperatureProperty = 51;
    const int ProtocolTypeNvme = 3, NvmeDataTypeLogPage = 2, NvmeLogHealth = 2;
    const int ProtocolDataSize = 40, NvmeLogSize = 512;

    public static List<DriveHealth> ReadAll()
    {
        var pnp = Wmi.Query("SELECT Index, Model, Size, PNPDeviceID FROM Win32_DiskDrive");
        var drives = Wmi.Query("SELECT * FROM MSFT_PhysicalDisk", StorageScope).Select(FromPhysicalDisk).ToList();
        if (drives.Count == 0)
            drives = pnp.Select(o => new DriveHealth { Number = (int)o.U64("Index"), Model = o.Str("Model"), Size = o.U64("Size") }).ToList();
        foreach (var d in drives)
        {
            var row = pnp.FirstOrDefault(o => (int)o.U64("Index") == d.Number);
            if (row != null) d.PnpId = row.Str("PNPDeviceID");
        }

        var predict = Wmi.Query("SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus", @"root\wmi");
        var smart = Wmi.Query("SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData", @"root\wmi");
        foreach (var d in drives)
        {
            try
            {
                if (d.Number >= 0) ReadDevice(d);
                if (d.PnpId.Length > 0)
                {
                    if (ForDrive(predict, d)?.Bool("PredictFailure") is { } failing) d.PredictFailure = failing;
                    if (ForDrive(smart, d)?["VendorSpecific"] is byte[] table) ApplySmart(d, table);
                }
            }
            catch (ManagementException) { }
            Judge(d);
        }
        return drives.OrderBy(d => d.Number < 0 ? int.MaxValue : d.Number).ToList();
    }

    static ManagementBaseObject? ForDrive(List<ManagementBaseObject> rows, DriveHealth d) =>
        rows.FirstOrDefault(o => o.Str("InstanceName").StartsWith(d.PnpId + "_", StringComparison.OrdinalIgnoreCase));

    static long? Num(ManagementBaseObject o, string property)
    {
        try { return o[property] is { } v ? Convert.ToInt64(v) : null; }
        catch { return null; }
    }

    static DriveHealth FromPhysicalDisk(ManagementBaseObject o)
    {
        var name = o.Str("FriendlyName");
        var d = new DriveHealth
        {
            Model = name.Length > 0 ? name : o.Str("Model"),
            Size = o.U64("Size"),
            BusType = (int)o.U64("BusType"),
            MediaType = (int)o.U64("MediaType"),
            WindowsHealth = (int?)Num(o, "HealthStatus"),
        };
        if (int.TryParse(o.Str("DeviceId"), out var number)) d.Number = number;
        ReadReliability(o as ManagementObject, d);
        return d;
    }

    /// <summary>Admin only: a standard user gets no rows back.</summary>
    static void ReadReliability(ManagementObject? disk, DriveHealth d)
    {
        if (disk == null) return;
        try
        {
            foreach (ManagementBaseObject r in disk.GetRelated("MSFT_StorageReliabilityCounter"))
            {
                d.TemperatureC ??= ValidTemperature(Num(r, "Temperature"));
                d.TemperatureMaxC = ValidTemperature(Num(r, "TemperatureMax"));
                if (Num(r, "Wear") is { } wear && wear > 0) d.WearPct ??= (int)wear;
                if (Num(r, "PowerOnHours") is { } hours && hours > 0) d.PowerOnHours ??= hours;
                d.ReadErrors = Num(r, "ReadErrorsTotal");
                d.WriteErrors = Num(r, "WriteErrorsTotal");
            }
        }
        catch { }
    }

    static void ReadDevice(DriveHealth d)
    {
        using var h = CreateFileW(@"\\.\PhysicalDrive" + d.Number, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return;
        d.Opened = true;

        if (Property(h, DeviceProperty, 1024) is { } descriptor && d.BusType == 0)
            d.BusType = BitConverter.ToInt32(descriptor, 28);
        if (Property(h, SeekPenaltyProperty, 12) is { } seek)
            d.SeekPenalty = seek[8] != 0;
        if (Property(h, TemperatureProperty, 1024) is { } temperature)
            ApplyTemperature(d, temperature);

        // Some RAID drivers (Intel RST on many laptops) still pass the NVMe log through.
        if ((d.BusType == BusNvme || d.BusType == 8) && NvmeHealthLog(h) is { } log)
            ApplyNvmeLog(d, log);
        else if (d.Gap == null && !d.IsNvme)
        {
            var result = new byte[516];
            if (DeviceIoControl(h, PredictFailure, null, 0, result, result.Length, out _, IntPtr.Zero))
                d.PredictFailure = BitConverter.ToInt32(result, 0) != 0;
        }
    }

    static byte[]? Property(SafeFileHandle h, int id, int size)
    {
        var input = new byte[12];
        BitConverter.TryWriteBytes(input, id);
        var output = new byte[size];
        return DeviceIoControl(h, QueryProperty, input, input.Length, output, size, out _, IntPtr.Zero) ? output : null;
    }

    static byte[]? NvmeHealthLog(SafeFileHandle h)
    {
        // STORAGE_PROPERTY_QUERY's first two fields, then STORAGE_PROTOCOL_SPECIFIC_DATA, then room for the log.
        var buffer = new byte[8 + ProtocolDataSize + NvmeLogSize];
        void Put(int offset, int value) => BitConverter.TryWriteBytes(buffer.AsSpan(offset), value);
        Put(0, ProtocolSpecificProperty);
        Put(8, ProtocolTypeNvme);
        Put(12, NvmeDataTypeLogPage);
        Put(16, NvmeLogHealth);
        Put(24, ProtocolDataSize);
        Put(28, NvmeLogSize);
        if (!DeviceIoControl(h, QueryProperty, buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero)) return null;
        return ExtractNvmeLog(buffer, returned);
    }

    /// <summary>Takes the log out of a STORAGE_PROTOCOL_DATA_DESCRIPTOR, whose protocol data follows a two-field header.</summary>
    internal static byte[]? ExtractNvmeLog(byte[] descriptor, int returned)
    {
        if (returned < 8 + ProtocolDataSize) return null;
        int offset = 8 + BitConverter.ToInt32(descriptor, 8 + 16), length = BitConverter.ToInt32(descriptor, 8 + 20);
        if (length < NvmeLogSize || offset < 8 || offset + NvmeLogSize > Math.Min(returned, descriptor.Length)) return null;
        var log = descriptor.AsSpan(offset, NvmeLogSize);
        return log.IndexOfAnyExcept((byte)0) < 0 ? null : log.ToArray();
    }

    internal static void ApplyTemperature(DriveHealth d, byte[] b)
    {
        // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: Critical at 8, Warning at 10, InfoCount at 12, entries from 24. Index 0 is the overall figure.
        short warning = BitConverter.ToInt16(b, 10);
        if (warning > 0) d.TemperatureWarnC = warning;
        if (BitConverter.ToUInt16(b, 12) > 0)
            d.TemperatureC = ValidTemperature(BitConverter.ToInt16(b, 24 + 2)) ?? d.TemperatureC;
    }

    static int? ValidTemperature(long? c) => c is > 0 and < 130 ? (int)c : null;

    /// <summary>Reads the NVMe SMART / Health Information log (log page 2); offsets are from the NVMe spec.</summary>
    internal static void ApplyNvmeLog(DriveHealth d, byte[] log)
    {
        d.NvmeCritical = log[0];
        d.TemperatureC = ValidTemperature(BitConverter.ToUInt16(log, 1) - 273) ?? d.TemperatureC;
        d.SparePct = log[3];
        d.SpareThresholdPct = log[4];
        d.WearPct = log[5];
        // Data units are thousands of 512-byte blocks.
        d.BytesRead = Counter(log, 32) * 512000;
        d.BytesWritten = Counter(log, 48) * 512000;
        d.PowerCycles = (long)Counter(log, 112);
        d.PowerOnHours = (long)Counter(log, 128);
        d.UnsafeShutdowns = (long)Counter(log, 144);
        d.DataErrors = (long)Counter(log, 160);
        d.ErrorLogEntries = (long)Counter(log, 176);
    }

    /// <summary>NVMe counters are 16-byte little-endian numbers.</summary>
    static double Counter(byte[] log, int offset) =>
        BitConverter.ToUInt64(log, offset) + BitConverter.ToUInt64(log, offset + 8) * 18446744073709551616.0;

    /// <summary>
    /// Reads the SMART attribute table from MSStorageDriver_FailurePredictData: a 2-byte revision, then 12-byte
    /// records of id, 2 flag bytes, current, worst and a 6-byte raw value. Only attributes with a settled meaning are used,
    /// which leaves out data written: attribute 241's unit differs by maker.
    /// </summary>
    internal static void ApplySmart(DriveHealth d, byte[] table)
    {
        for (int o = 2; o + 12 <= table.Length; o += 12)
        {
            byte id = table[o];
            if (id == 0) continue;
            long raw = BitConverter.ToUInt32(table, o + 5);
            switch (id)
            {
                case 5: d.ReallocatedSectors = raw; break;
                case 9: d.PowerOnHours ??= raw; break;
                case 12: d.PowerCycles ??= raw; break;
                case 187 or 198: d.DataErrors = Math.Max(d.DataErrors ?? 0, raw); break;
                case 194: d.TemperatureC ??= ValidTemperature(table[o + 5]); break;
                case 197: d.PendingSectors = raw; break;
            }
        }
    }

    /// <summary>Sets the verdict. Healthy needs real evidence from the drive itself; Windows' own "healthy" isn't enough, because it's also what a drive that reports nothing gets.</summary>
    internal static void Judge(DriveHealth d)
    {
        var failing = new List<string>();
        var soon = new List<string>();

        if (d.NvmeCritical is { } c)
        {
            if ((c & 0x01) != 0) failing.Add("Its spare space has fallen below the safe level.");
            if ((c & 0x04) != 0) failing.Add("It reports that its reliability has degraded.");
            if ((c & 0x08) != 0) failing.Add("It has switched itself to read-only to protect your data.");
            if ((c & 0x10) != 0) failing.Add("Its memory backup has failed.");
            if ((c & 0x02) != 0) soon.Add("It's outside its safe temperature range.");
        }
        if (d.PredictFailure == true) failing.Add("Its own self-check predicts it will fail.");
        if (d.WindowsHealth == 2) failing.Add("Windows marks it as unhealthy.");
        if (d.WindowsHealth == 1) soon.Add("Windows reports a warning for it.");

        if (d.DataErrors > 0) soon.Add(d.DataErrors == 1 ? "It has recorded a data error it couldn't correct." : $"It has recorded {Fmt.Num(d.DataErrors.Value)} data errors it couldn't correct.");
        if (d.ReallocatedSectors > 0) soon.Add($"{Fmt.Num(d.ReallocatedSectors.Value)} bad sectors have been swapped for spares.");
        if (d.PendingSectors > 0) soon.Add($"{Fmt.Num(d.PendingSectors.Value)} sectors failed to read and are waiting to be replaced.");
        if (d.WearPct >= 100) soon.Add("It has used all of its rated write life.");
        else if (d.WearPct >= 90) soon.Add($"It has used {d.WearPct}% of its rated write life.");
        if (d.SparePct is { } spare && d.SpareThresholdPct is > 0 and var limit && spare >= limit && spare < limit + 10) soon.Add("Its spare space is getting low.");
        if (d.TemperatureC >= d.TemperatureWarnC && (d.NvmeCritical & 0x02) is null or 0) soon.Add("It's running above its warning temperature.");

        if (failing.Count > 0)
        {
            d.Verdict = DriveVerdict.Failing;
            d.Reason = string.Join(" ", failing.Concat(soon)) + " Back up your files now and plan to replace it.";
        }
        else if (soon.Count > 0)
        {
            d.Verdict = DriveVerdict.CheckSoon;
            d.Reason = string.Join(" ", soon) + " Keep a backup of anything important and check again soon.";
        }
        else if (d.NvmeCritical != null)
        {
            d.Verdict = DriveVerdict.Healthy;
            d.Reason = "The drive's own health log shows no warnings and no uncorrected errors.";
        }
        else if (d.PredictFailure == false)
        {
            d.Verdict = DriveVerdict.Healthy;
            d.Reason = "The drive's own self-check predicts no failure.";
        }
        else
        {
            d.Verdict = DriveVerdict.Unknown;
            d.Reason = d.Gap ?? (d.WindowsHealth == 0
                ? "Windows lists this drive as healthy, but the drive's own health data couldn't be read, so that can't be confirmed."
                : "This drive didn't report enough to judge its health.");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
