using Tman.Core;

namespace Tman.Monitoring;

/// <summary>Enumerates hardware GPUs via DXGI to get names, memory sizes and LUIDs (which PDH counters are keyed by).</summary>
internal static unsafe class Dxgi
{
    public sealed record Adapter(string Luid, string Name, ulong Dedicated, ulong Shared, uint VendorId);

    const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    public static List<Adapter> Enumerate()
    {
        var list = new List<Adapter>();
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (Native.CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero) return list;
            try
            {
                var fv = *(void***)factory;
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr adapter;
                    // IDXGIFactory1::EnumAdapters1 is vtable slot 12
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fv[12])(factory, i, &adapter);
                    if (hr < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        var av = *(void***)adapter;
                        Native.DXGI_ADAPTER_DESC1 d;
                        // IDXGIAdapter1::GetDesc1 is vtable slot 10
                        if (((delegate* unmanaged[Stdcall]<IntPtr, Native.DXGI_ADAPTER_DESC1*, int>)av[10])(adapter, &d) >= 0
                            && (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0)
                        {
                            var name = new string(d.Description).Trim();
                            var luid = $"0x{(uint)d.LuidHigh:x8}_0x{d.LuidLow:x8}";
                            if (!list.Exists(a => a.Luid == luid))
                                list.Add(new Adapter(luid, name, d.DedicatedVideoMemory, d.SharedSystemMemory, d.VendorId));
                        }
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
            }
            finally
            {
                Release(factory);
            }
        }
        catch { }
        return list;
    }

    static void Release(IntPtr unknown) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)unknown)[2])(unknown);
}
