using System.Runtime.InteropServices;
using System.Text;

namespace Tman.Monitoring;

/// <summary>Temperature, fan, power and clocks for one GPU; any value can be missing.</summary>
public sealed record GpuReading(double? TempC, double? FanPct, double? FanRpm, double? PowerW, double? PowerLimitW, double? CoreMhz, double? MemMhz)
{
    /// <summary>Board power as a percentage of its limit, from the WDDM driver data (when watts aren't available).</summary>
    public double? PowerPct { get; init; }
}

/// <summary>
/// GPU sensors. NVIDIA cards are read through NVML (nvml.dll ships with the NVIDIA driver), which gives watts,
/// fan % and clocks. Other GPUs fall back to the WDDM adapter performance data that Task Manager uses for its
/// GPU temperature. Lives on the sampler thread.
/// </summary>
internal sealed unsafe class GpuSensors
{
    readonly Dictionary<string, IntPtr> _nvmlDevices = new();
    readonly Dictionary<string, uint> _kmtAdapters = new();

    delegate* unmanaged<IntPtr, uint*, int> _getFanSpeed, _getPowerUsage, _getPowerLimit;
    delegate* unmanaged<IntPtr, int, uint*, int> _getTemp, _getClock;

    public GpuSensors(IReadOnlyList<Dxgi.Adapter> adapters)
    {
        try { InitNvml(adapters); } catch { _nvmlDevices.Clear(); }
        foreach (var a in adapters)
        {
            if (!ParseLuid(a.Luid, out var low, out var high)) continue;
            var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = low, LuidHigh = high };
            try
            {
                if (D3DKMTOpenAdapterFromLuid(ref open) == 0) _kmtAdapters[a.Luid] = open.hAdapter;
            }
            catch { }
        }
    }

    public GpuReading? Read(string luid)
    {
        var nvml = _nvmlDevices.TryGetValue(luid, out var dev) ? ReadNvml(dev) : null;
        var kmt = _kmtAdapters.TryGetValue(luid, out var adapter) ? ReadKmt(adapter) : null;
        if (nvml == null) return kmt;
        if (kmt == null) return nvml;
        // NVML is richer; the WDDM data fills its gaps.
        return nvml with
        {
            TempC = nvml.TempC ?? kmt.TempC,
            FanRpm = kmt.FanRpm,
            MemMhz = nvml.MemMhz ?? kmt.MemMhz,
            PowerPct = kmt.PowerPct,
        };
    }

    // ---- NVML ----

    void InitNvml(IReadOnlyList<Dxgi.Adapter> adapters)
    {
        var nvidia = adapters.Where(a => a.VendorId == 0x10DE).ToList();
        if (nvidia.Count == 0) return;
        if (!NativeLibrary.TryLoad("nvml.dll", out var lib) &&
            !NativeLibrary.TryLoad(@"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll", out lib)) return;

        var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlInit_v2");
        if (init() != 0) return;
        var getCount = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetCount_v2");
        var getHandle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2");
        var getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetName");
        _getTemp = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetTemperature");
        _getFanSpeed = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetFanSpeed");
        _getPowerUsage = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetPowerUsage");
        _getPowerLimit = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetEnforcedPowerLimit");
        _getClock = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetClockInfo");

        uint count;
        if (getCount(&count) != 0) return;
        // NVML doesn't report the Windows adapter LUID, so pair devices with DXGI adapters by name (then by order).
        var unmatched = new List<Dxgi.Adapter>(nvidia);
        var nameBuf = stackalloc byte[96];
        for (uint i = 0; i < count; i++)
        {
            IntPtr dev;
            if (getHandle(i, &dev) != 0) continue;
            string name = "";
            if (getName(dev, nameBuf, 96) == 0)
            {
                var span = new ReadOnlySpan<byte>(nameBuf, 96);
                int end = span.IndexOf((byte)0);
                name = Encoding.UTF8.GetString(end >= 0 ? span[..end] : span);
            }
            var match = unmatched.FirstOrDefault(a => a.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? unmatched.FirstOrDefault();
            if (match == null) break;
            unmatched.Remove(match);
            _nvmlDevices[match.Luid] = dev;
        }
    }

    GpuReading ReadNvml(IntPtr dev)
    {
        uint v;
        double? temp = _getTemp(dev, 0 /* NVML_TEMPERATURE_GPU */, &v) == 0 ? v : null;
        double? fan = _getFanSpeed(dev, &v) == 0 ? v : null;
        double? power = _getPowerUsage(dev, &v) == 0 ? v / 1000.0 : null;
        double? limit = _getPowerLimit(dev, &v) == 0 ? v / 1000.0 : null;
        double? core = _getClock(dev, 0 /* graphics */, &v) == 0 ? v : null;
        double? mem = _getClock(dev, 2 /* memory */, &v) == 0 ? v : null;
        return new GpuReading(temp, fan, null, power, limit, core, mem);
    }

    // ---- WDDM adapter perf data (D3DKMT) ----

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_OPENADAPTERFROMLUID
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public void* pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    [DllImport("gdi32.dll")]
    static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);

    [DllImport("gdi32.dll")]
    static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);

    static GpuReading? ReadKmt(uint adapter)
    {
        var perf = new D3DKMT_ADAPTER_PERFDATA();
        var q = new D3DKMT_QUERYADAPTERINFO
        {
            hAdapter = adapter,
            Type = KMTQAITYPE_ADAPTERPERFDATA,
            pPrivateDriverData = &perf,
            PrivateDriverDataSize = (uint)sizeof(D3DKMT_ADAPTER_PERFDATA),
        };
        if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
        double? temp = perf.Temperature > 0 ? perf.Temperature / 10.0 : null; // deci-degrees Celsius
        double? rpm = perf.FanRPM > 0 ? perf.FanRPM : null;
        // Drivers disagree on units here (Hz per the docs, MHz in practice for some), so accept either.
        double? mem = perf.MemoryFrequency == 0 ? null : perf.MemoryFrequency > 1_000_000 ? perf.MemoryFrequency / 1e6 : perf.MemoryFrequency;
        double? powerPct = perf.Power > 0 ? perf.Power / 10.0 : null; // tenths of a percent
        if (temp == null && rpm == null && mem == null && powerPct == null) return null;
        return new GpuReading(temp, null, rpm, null, null, null, mem) { PowerPct = powerPct };
    }

    /// <summary>Our LUID strings look like 0x{high:x8}_0x{low:x8}.</summary>
    static bool ParseLuid(string luid, out uint low, out int high)
    {
        low = 0;
        high = 0;
        var parts = luid.Split('_');
        return parts.Length == 2
            && int.TryParse(parts[0].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out high)
            && uint.TryParse(parts[1].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out low);
    }
}
