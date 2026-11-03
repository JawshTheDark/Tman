using System.Runtime.InteropServices;

namespace Tman.Core;

/// <summary>Thin wrapper over the Performance Data Helper API, used for counters with no cheaper source.</summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FmtValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, byte* buffer);

    const uint PDH_FMT_DOUBLE = 0x200;
    const uint PDH_FMT_NOCAP100 = 0x8000;
    const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    struct FmtValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FmtItem
    {
        public char* Name;
        public FmtValue Value;
    }

    IntPtr _query;
    byte[] _buffer = new byte[64 * 1024];

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    public IntPtr Add(string path) =>
        _query != IntPtr.Zero && PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    public static double? Value(IntPtr counter, bool cap = true)
    {
        if (counter == IntPtr.Zero) return null;
        var format = PDH_FMT_DOUBLE | (cap ? 0 : PDH_FMT_NOCAP100);
        if (PdhGetFormattedCounterValue(counter, format, out _, out var v) != 0 || v.Status > 1) return null;
        return v.Value;
    }

    /// <summary>Reads every instance of a wildcard counter, keyed by instance name.</summary>
    public Dictionary<string, double> Array(IntPtr counter, bool cap = true)
    {
        var result = new Dictionary<string, double>();
        if (counter == IntPtr.Zero) return result;
        var format = PDH_FMT_DOUBLE | (cap ? 0 : PDH_FMT_NOCAP100);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint size = (uint)_buffer.Length;
            uint count;
            uint status;
            fixed (byte* p = _buffer)
            {
                status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, p);
                if (status == 0)
                {
                    var items = (FmtItem*)p;
                    for (int i = 0; i < count; i++)
                    {
                        if (items[i].Value.Status > 1 || items[i].Name == null) continue;
                        result[new string(items[i].Name)] = items[i].Value.Value;
                    }
                    return result;
                }
            }
            if (status != PDH_MORE_DATA) return result;
            _buffer = new byte[size + 16 * 1024];
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
