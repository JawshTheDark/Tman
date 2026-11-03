using System.Runtime.InteropServices;
using System.Text;

namespace Tman.Core;

/// <summary>Power plans, the Windows 11 power mode and the power source (powrprof / kernel32).</summary>
internal static class PowerPlans
{
    public sealed record Plan(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record ProcessorPolicy(uint? MinState, uint? MaxState, string? Boost, uint? MinUnparked);

    public sealed record PowerSource(bool OnAc, bool HasBattery, int Percent, TimeSpan? Remaining, bool Saver);

    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>Windows 11 "Power mode" overlays. Windows only applies them on the Balanced plan.</summary>
    public static readonly (Guid Id, string Name)[] Modes =
    {
        (new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), "Best power efficiency"),
        (Guid.Empty, "Balanced"),
        (new Guid("ded574b5-45a0-4f42-8737-46345c09c238"), "Best performance"),
    };

    static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    static readonly Guid ProcMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    static readonly Guid ProcMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    static readonly Guid MinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");

    static readonly string[] BoostNames =
    {
        "Disabled", "Enabled", "Aggressive", "Efficient enabled", "Efficient aggressive",
        "Aggressive at guaranteed", "Efficient aggressive at guaranteed",
    };

    const uint ACCESS_SCHEME = 16;

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [DllImport("powrprof.dll")]
    static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup, uint access, uint index, byte[]? buffer, ref uint size);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, byte[]? buffer, ref uint size);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    // Undocumented but stable since Windows 10 1709; this is what the Settings app uses for Power mode.
    [DllImport("powrprof.dll")]
    static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static Guid? Active()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public static List<Plan> All()
    {
        var list = new List<Plan>();
        var buffer = new byte[16];
        for (uint i = 0; i < 64; i++)
        {
            uint size = 16;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, buffer, ref size) != 0) break;
            var id = new Guid(buffer);
            list.Add(new Plan(id, FriendlyName(id)));
        }
        return list;
    }

    static string FriendlyName(Guid id)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return id.ToString();
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return id.ToString();
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static bool SetActive(Guid id) => PowerSetActiveScheme(IntPtr.Zero, ref id) == 0;

    public static Guid? Mode()
    {
        try { return PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static bool SetMode(Guid id)
    {
        try { return PowerSetActiveOverlayScheme(id) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static ProcessorPolicy Policy(Guid scheme, bool ac)
    {
        uint? Read(Guid setting)
        {
            var sub = SubProcessor;
            uint r = ac
                ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v)
                : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
            return r == 0 ? v : null;
        }
        var boost = Read(BoostMode);
        return new ProcessorPolicy(Read(ProcMin), Read(ProcMax),
            boost is { } b ? b < BoostNames.Length ? BoostNames[b] : $"Mode {b}" : null, Read(MinCores));
    }

    public static PowerSource Source()
    {
        if (!GetSystemPowerStatus(out var s)) return new PowerSource(true, false, 0, null, false);
        bool hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        return new PowerSource(s.ACLineStatus != 0, hasBattery, s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 0,
            s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null, (s.SystemStatusFlag & 1) != 0);
    }
}
