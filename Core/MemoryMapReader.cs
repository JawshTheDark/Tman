using System.Runtime.InteropServices;

namespace Tman.Core;

/// <summary>One pool tag: kernel memory that a driver or Windows component labeled with four letters.</summary>
public sealed record PoolTagUse(string Tag, ulong Paged, ulong NonPaged, long Blocks)
{
    public ulong Total => Paged + NonPaged;
    public string TotalText => Fmt.Bytes(Total);
    public string PagedText => Fmt.Bytes(Paged);
    public string NonPagedText => Fmt.Bytes(NonPaged);
    public string BlocksText => Fmt.Num(Blocks);
    public string Meaning => MemoryMapReader.DescribeTag(Tag);
}

/// <summary>A snapshot of what physical memory is doing, in bytes.</summary>
public sealed record MemoryMapData
{
    public ulong Installed { get; init; }
    public ulong HardwareReserved { get; init; }
    public ulong InUse { get; init; }
    public ulong Modified { get; init; }
    public ulong Standby { get; init; }
    /// <summary>Free pages, or free and zero together when <see cref="ZeroKnown"/> is false.</summary>
    public ulong Free { get; init; }
    public ulong Zero { get; init; }
    public bool ZeroKnown { get; init; }
    /// <summary>Standby size per level, in the order Windows gives up on them.</summary>
    public List<(string Label, ulong Bytes)> StandbyLevels { get; init; } = new();
    public ulong Commit { get; init; }
    public ulong CommitLimit { get; init; }
    public ulong PagedPool { get; init; }
    public ulong NonPagedPool { get; init; }
    public ulong FileCache { get; init; }
    public List<PoolTagUse>? PoolTags { get; init; }
    /// <summary>Neither source of page lists worked, so the split of RAM is unknown.</summary>
    public bool ListsMissing { get; init; }
}

/// <summary>Reads the page lists, commit and pools. Windows gives all of this to standard users; emptying the standby list is the one part that needs admin.</summary>
internal sealed unsafe class MemoryMapReader
{
    const int SystemPoolTagInformation = 22, SystemMemoryListInformation = 80;
    const int MemoryPurgeStandbyList = 4;
    const int TopTags = 12;
    static readonly int PageSize = Environment.SystemPageSize;

    /// <summary>SYSTEM_MEMORY_LIST_INFORMATION, all page counts.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryLists
    {
        public nuint Zero, Free, Modified, ModifiedNoWrite, Bad;
        public fixed ulong ByPriority[8];
        public fixed ulong RepurposedByPriority[8];
        public nuint ModifiedPageFile;
    }

    /// <summary>SYSTEM_POOLTAG. A 4-byte count, padded to 8, comes ahead of the array.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct PoolTagEntry
    {
        public uint Tag, PagedAllocs, PagedFrees;
        public nuint PagedUsed;
        public uint NonPagedAllocs, NonPagedFrees;
        public nuint NonPagedUsed;
    }

    [DllImport("ntdll.dll")]
    static extern int NtSetSystemInformation(int infoClass, void* info, int length);

    readonly PdhQuery _pdh = new();
    readonly IntPtr _modified, _standbyCore, _standbyNormal, _standbyReserve, _freeZero, _commit, _commitLimit, _paged, _nonPaged, _cache;
    byte[] _tags = new byte[256 * 1024];

    public MemoryMapReader()
    {
        _modified = _pdh.Add(@"\Memory\Modified Page List Bytes");
        _standbyCore = _pdh.Add(@"\Memory\Standby Cache Core Bytes");
        _standbyNormal = _pdh.Add(@"\Memory\Standby Cache Normal Priority Bytes");
        _standbyReserve = _pdh.Add(@"\Memory\Standby Cache Reserve Bytes");
        _freeZero = _pdh.Add(@"\Memory\Free & Zero Page List Bytes");
        _commit = _pdh.Add(@"\Memory\Committed Bytes");
        _commitLimit = _pdh.Add(@"\Memory\Commit Limit");
        _paged = _pdh.Add(@"\Memory\Pool Paged Bytes");
        _nonPaged = _pdh.Add(@"\Memory\Pool Nonpaged Bytes");
        _cache = _pdh.Add(@"\Memory\Cache Bytes");
    }

    ulong? Bytes(IntPtr counter)
    {
        var v = PdhQuery.Value(counter, cap: false);
        return v >= 0 ? (ulong)v.Value : null;
    }

    public MemoryMapData Read()
    {
        _pdh.Collect();
        var status = new Native.MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref status);
        ulong total = status.ullTotalPhys;
        Native.GetPerformanceInfo(out var perf, (uint)sizeof(Native.PERFORMANCE_INFORMATION));
        ulong installed = Native.GetPhysicallyInstalledSystemMemory(out var kb) ? kb * 1024 : total;

        ulong modified, standby, free, zero = 0;
        bool zeroKnown = false, missing = false;
        var levels = new List<(string, ulong)>();
        MemoryLists lists;
        if (Native.NtQuerySystemInformation(SystemMemoryListInformation, &lists, sizeof(MemoryLists), out _) == 0)
        {
            modified = Pages(lists.Modified + lists.ModifiedNoWrite);
            free = Pages(lists.Free);
            zero = Pages(lists.Zero);
            zeroKnown = true;
            standby = 0;
            for (int i = 0; i < 8; i++)
            {
                ulong bytes = Pages((nuint)lists.ByPriority[i]);
                standby += bytes;
                levels.Add(($"Priority {i}", bytes));
            }
        }
        else
        {
            ulong? pdhModified = Bytes(_modified), core = Bytes(_standbyCore), normal = Bytes(_standbyNormal), reserve = Bytes(_standbyReserve), freeZero = Bytes(_freeZero);
            missing = pdhModified == null || core == null || normal == null || reserve == null || freeZero == null;
            modified = pdhModified ?? 0;
            free = freeZero ?? 0;
            standby = (core ?? 0) + (normal ?? 0) + (reserve ?? 0);
            levels.Add(("Reserve", reserve ?? 0));
            levels.Add(("Normal", normal ?? 0));
            levels.Add(("Core", core ?? 0));
        }

        ulong accounted = modified + standby + free + zero;
        return new MemoryMapData
        {
            Installed = Math.Max(installed, total),
            HardwareReserved = installed > total ? installed - total : 0,
            InUse = total > accounted ? total - accounted : 0,
            Modified = modified,
            Standby = standby,
            Free = free,
            Zero = zero,
            ZeroKnown = zeroKnown,
            StandbyLevels = levels,
            Commit = Bytes(_commit) ?? perf.CommitTotal * perf.PageSize,
            CommitLimit = Bytes(_commitLimit) ?? perf.CommitLimit * perf.PageSize,
            PagedPool = Bytes(_paged) ?? perf.KernelPaged * perf.PageSize,
            NonPagedPool = Bytes(_nonPaged) ?? perf.KernelNonpaged * perf.PageSize,
            FileCache = Bytes(_cache) ?? 0,
            PoolTags = ReadAllPoolTags(ref _tags)?.OrderByDescending(t => t.Total).Take(TopTags).ToList(),
            ListsMissing = missing,
        };
    }

    static ulong Pages(nuint pages) => (ulong)pages * (ulong)PageSize;

    /// <summary>Every pool tag, or null when Windows won't say. The buffer grows as needed and is kept for next time.</summary>
    public static List<PoolTagUse>? ReadAllPoolTags(ref byte[] buffer)
    {
        int status = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int needed;
            fixed (byte* p = buffer) status = Native.NtQuerySystemInformation(SystemPoolTagInformation, p, buffer.Length, out needed);
            if (status != Native.STATUS_INFO_LENGTH_MISMATCH) break;
            buffer = new byte[Math.Max(needed + 64 * 1024, buffer.Length * 2)];
        }
        if (status != 0) return null;

        var all = new List<PoolTagUse>();
        fixed (byte* p = buffer)
        {
            uint count = *(uint*)p;
            var entries = (PoolTagEntry*)(p + 8);
            for (uint i = 0; i < count; i++)
            {
                var e = entries[i];
                long blocks = (long)(e.PagedAllocs - e.PagedFrees) + (e.NonPagedAllocs - e.NonPagedFrees);
                all.Add(new PoolTagUse(TagText(e.Tag), e.PagedUsed, e.NonPagedUsed, blocks));
            }
        }
        return all;
    }

    static string TagText(uint tag)
    {
        Span<char> text = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((tag >> (i * 8)) & 0xFF);
            text[i] = c is >= ' ' and < (char)127 ? c : '.';
        }
        return new string(text).TrimEnd();
    }

    /// <summary>Drops every standby page. Returns null on success, or what went wrong in plain words.</summary>
    public static string? EmptyStandbyList()
    {
        if (!TokenPrivileges.Enable(TokenPrivileges.ProfileSingleProcess))
            return "Windows didn't give Tman the right to do this. It needs administrator rights.";
        int command = MemoryPurgeStandbyList;
        int status = NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int));
        return status == 0 ? null : $"Windows refused (status 0x{status:X8}).";
    }

    static readonly Dictionary<string, string> KnownTags = new(StringComparer.Ordinal)
    {
        ["MmSt"] = "Tracking for mapped files",
        ["MmCa"] = "Control data for mapped files",
        ["File"] = "Open file objects",
        ["Thre"] = "Thread objects",
        ["Proc"] = "Process objects",
        ["Even"] = "Event objects",
        ["Toke"] = "Security tokens",
        ["Key"] = "Registry key objects",
        ["Sect"] = "Section objects",
        ["Driv"] = "Driver objects",
        ["Devi"] = "Device objects",
        ["Irp"] = "I/O requests",
        ["Cont"] = "Contiguous memory for device drivers",
        ["ALPC"] = "Messages between processes",
        ["Pool"] = "Pool bookkeeping",
    };

    static readonly (string Prefix, string Meaning)[] TagFamilies =
    {
        ("Ntf", "NTFS file system"), ("NtF", "NTFS file system"), ("CM", "Registry"), ("Mm", "Memory manager"), ("Vi", "Graphics memory manager"),
        ("Dx", "DirectX"), ("ND", "NDIS networking"), ("TCP", "TCP/IP networking"), ("Tcp", "TCP/IP networking"), ("FM", "File system filter manager"),
        ("Cc", "File cache manager"), ("Ob", "Object manager"), ("Se", "Security"), ("Io", "I/O manager"), ("Ps", "Process manager"),
        ("Wdf", "Windows driver framework"), ("Etw", "Event tracing"), ("sm", "Memory compression store"), ("USB", "USB"), ("Usb", "USB"),
        ("NV", "NVIDIA driver"), ("Nv", "NVIDIA driver"),
    };

    public static string DescribeTag(string tag)
    {
        if (KnownTags.TryGetValue(tag, out var known)) return known;
        foreach (var (prefix, meaning) in TagFamilies)
            if (tag.StartsWith(prefix, StringComparison.Ordinal)) return meaning;
        return "";
    }
}
