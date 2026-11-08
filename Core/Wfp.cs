using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Tman.Core;

/// <summary>What one Windows Filtering Platform filter should match and do. Everything not set means "any".</summary>
public sealed record WfpFilterSpec(Guid Key, string Name, string Description, Guid Layer, bool Block, byte Weight, bool Persistent)
{
    /// <summary>The app, as the NT device path WFP wants (see <see cref="WfpEngine.AppId"/>). Null means every app.</summary>
    public byte[]? AppId { get; init; }
    public IPAddress? RemoteAddress { get; init; }
    /// <summary>Prefix length for <see cref="RemoteAddress"/>; 32 or 128 for one address.</summary>
    public int PrefixLength { get; init; } = -1;
    public (ushort Low, ushort High)? RemotePort { get; init; }
    public (ushort Low, ushort High)? LocalPort { get; init; }
    /// <summary>6 for TCP, 17 for UDP, null for any.</summary>
    public byte? Protocol { get; init; }
}

/// <summary>A filter Tman found in the engine under its own provider.</summary>
public sealed record WfpFilterInfo(Guid Key, ulong Id, string Name, Guid Layer, bool Block, bool Persistent);

/// <summary>One connection the filter engine refused, as reported by its event log.</summary>
public sealed record WfpDrop(DateTime Time, string? AppPath, IPAddress? Remote, int RemotePort, int LocalPort, byte Protocol, ulong FilterId, bool Inbound);

/// <summary>
/// Tman's slice of the Windows Filtering Platform: a provider and a sublayer of its own, filters under them at the four
/// connection-authorisation layers, and the engine's drop events. Chosen over Windows Firewall rules (INetFwPolicy2)
/// because one filter can name an app, a remote address, a port range and a protocol at once; because every filter
/// Tman adds is tagged with its provider, so "remove everything Tman did" is exact; and because the engine reports each
/// connection it refused with the filter that did it, which Windows Firewall rules don't. A block here wins over any
/// Windows Firewall allow (the engine lets a block from any sublayer override a soft permit), and a permit here never
/// overrides a Windows Firewall block. Everything blocks, so call it off the UI thread; it needs administrator rights.
/// </summary>
public sealed unsafe class WfpEngine : IDisposable
{
    public static readonly Guid ProviderKey = new("7a3f0d4c-61b2-4c0e-9a4f-2f0b7d1e5a11");
    public static readonly Guid SubLayerKey = new("7a3f0d4c-61b2-4c0e-9a4f-2f0b7d1e5a12");

    public static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    public static readonly Guid LayerAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    public static readonly Guid LayerAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    static readonly Guid CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    static readonly Guid CondRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    static readonly Guid CondRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    static readonly Guid CondLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    static readonly Guid CondProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    // FWP_DATA_TYPE
    const uint TypeEmpty = 0, TypeUint8 = 1, TypeUint16 = 2, TypeUint32 = 3, TypeByteBlob = 12, TypeV4AddrMask = 0x100, TypeV6AddrMask = 0x101, TypeRange = 0x102;
    const uint MatchEqual = 0, MatchRange = 7;
    const uint ActionBlock = 0x1001, ActionPermit = 0x1002;
    const uint FilterFlagPersistent = 1, ProviderFlagPersistent = 1, SubLayerFlagPersistent = 1, SessionFlagDynamic = 1;
    const uint AuthnDefault = 0xFFFFFFFF;
    const uint FwpEAlreadyExists = 0x80320009, FwpENotFound = 0x80320008, FwpEFilterNotFound = 0x80320003, FwpEProviderNotFound = 0x80320005, FwpESubLayerNotFound = 0x80320007, FwpEInUse = 0x8032000A;
    const uint ErrorAccessDenied = 0x80070005;
    const uint OptionCollectNetEvents = 0;
    const uint EventClassifyDrop = 3;
    const uint FlagIpProtocolSet = 0x1, FlagLocalAddrSet = 0x2, FlagRemoteAddrSet = 0x4, FlagLocalPortSet = 0x8, FlagRemotePortSet = 0x10, FlagAppIdSet = 0x20, FlagIpVersionSet = 0x100;
    const ushort SubLayerWeight = 0xFFF0;

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    struct Filter
    {
        [FieldOffset(0)] public Guid FilterKey;
        [FieldOffset(16)] public IntPtr Name;
        [FieldOffset(24)] public IntPtr Description;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public Guid* ProviderKey;
        [FieldOffset(48)] public uint ProviderDataSize;
        [FieldOffset(56)] public IntPtr ProviderData;
        [FieldOffset(64)] public Guid LayerKey;
        [FieldOffset(80)] public Guid SubLayerKey;
        [FieldOffset(96)] public uint WeightType;
        [FieldOffset(104)] public byte WeightUint8;
        [FieldOffset(112)] public uint NumConditions;
        [FieldOffset(120)] public Condition* Conditions;
        [FieldOffset(128)] public uint ActionType;
        [FieldOffset(176)] public ulong FilterId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct Condition
    {
        [FieldOffset(0)] public Guid FieldKey;
        [FieldOffset(16)] public uint MatchType;
        [FieldOffset(24)] public uint ValueType;
        [FieldOffset(32)] public byte Uint8;
        [FieldOffset(32)] public ushort Uint16;
        [FieldOffset(32)] public uint Uint32;
        [FieldOffset(32)] public void* Pointer;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    struct ByteBlob
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public byte* Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct Range
    {
        [FieldOffset(0)] public uint LowType;
        [FieldOffset(8)] public ushort Low;
        [FieldOffset(16)] public uint HighType;
        [FieldOffset(24)] public ushort High;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct Provider
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public IntPtr Name;
        [FieldOffset(24)] public IntPtr Description;
        [FieldOffset(32)] public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    struct SubLayer
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public IntPtr Name;
        [FieldOffset(24)] public IntPtr Description;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public Guid* ProviderKey;
        [FieldOffset(64)] public ushort Weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    struct Session
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public IntPtr Name;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(36)] public uint TxnWaitTimeoutMs;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct Subscription
    {
        [FieldOffset(0)] public EnumTemplate* Template;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public Guid SessionKey;
    }

    /// <summary>FWPM_NET_EVENT_ENUM_TEMPLATE0: a time window and conditions; empty means every event from now on.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct EnumTemplate
    {
        [FieldOffset(0)] public long StartTime;
        [FieldOffset(8)] public long EndTime;
        [FieldOffset(16)] public uint NumConditions;
        [FieldOffset(24)] public void* Conditions;
    }

    /// <summary>FWPM_NET_EVENT2 with FWPM_NET_EVENT_HEADER2, as FwpmNetEventSubscribe1 delivers it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    struct NetEvent
    {
        [FieldOffset(0)] public long TimeStamp;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public uint IpVersion;
        [FieldOffset(16)] public byte IpProtocol;
        [FieldOffset(20)] public uint LocalV4;
        [FieldOffset(20)] public fixed byte LocalV6[16];
        [FieldOffset(36)] public uint RemoteV4;
        [FieldOffset(36)] public fixed byte RemoteV6[16];
        [FieldOffset(52)] public ushort LocalPort;
        [FieldOffset(54)] public ushort RemotePort;
        [FieldOffset(64)] public uint AppIdSize;
        [FieldOffset(72)] public byte* AppIdData;
        [FieldOffset(104)] public uint Type;
        [FieldOffset(112)] public ClassifyDrop* Drop;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    struct ClassifyDrop
    {
        [FieldOffset(0)] public ulong FilterId;
        [FieldOffset(8)] public ushort LayerId;
        [FieldOffset(24)] public uint Direction;
    }

    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineOpen0(char* serverName, uint authnService, void* authIdentity, Session* session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmProviderAdd0(IntPtr engine, Provider* provider, void* sd);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmProviderDeleteByKey0(IntPtr engine, in Guid key);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerAdd0(IntPtr engine, SubLayer* subLayer, void* sd);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, in Guid key);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterAdd0(IntPtr engine, Filter* filter, void* sd, out ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterDeleteByKey0(IntPtr engine, in Guid key);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterDeleteById0(IntPtr engine, ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterCreateEnumHandle0(IntPtr engine, void* template, out IntPtr enumHandle);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterEnum0(IntPtr engine, IntPtr enumHandle, uint requested, out Filter** entries, out uint returned);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engine, IntPtr enumHandle);
    [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(void** p);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmGetAppIdFromFileName0(string fileName, out ByteBlob* appId);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmNetEventSubscribe1(IntPtr engine, Subscription* subscription, delegate* unmanaged<void*, NetEvent*, void> callback, void* context, out IntPtr events);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmNetEventUnsubscribe0(IntPtr engine, IntPtr events);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmNetEventSubscribe0(IntPtr engine, Subscription* subscription, delegate* unmanaged<void*, NetEvent*, void> callback, void* context, out IntPtr events);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmNetEventCreateEnumHandle0(IntPtr engine, EnumTemplate* template, out IntPtr enumHandle);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmNetEventEnum2(IntPtr engine, IntPtr enumHandle, uint requested, out NetEvent** entries, out uint returned);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmNetEventDestroyEnumHandle0(IntPtr engine, IntPtr enumHandle);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineGetOption0(IntPtr engine, uint option, out void* value);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineSetOption0(IntPtr engine, uint option, void* value);

    IntPtr _engine;
    IntPtr _events;
    GCHandle _self;
    Action<WfpDrop>? _onDrop;
    readonly Guid _sessionKey;
    Timer? _poll;
    long _lastDrop;
    readonly object _pollGate = new();

    /// <summary>How refused connections reach Tman: "subscription", "polling" or null before <see cref="SubscribeDrops"/>.</summary>
    public string? DropSource { get; private set; }
    /// <summary>Why the subscription was refused, when it was and polling took over.</summary>
    public string? SubscribeError { get; private set; }

    WfpEngine(IntPtr engine, Guid sessionKey)
    {
        _engine = engine;
        _sessionKey = sessionKey;
    }

    /// <summary>
    /// Opens the engine. A dynamic session's filters vanish when it closes (or Tman dies), which is right for filters
    /// made from DNS answers; rules the user wrote go through a normal session with persistent filters, so they hold
    /// while Tman is closed.
    /// </summary>
    public static WfpEngine Open(bool dynamic)
    {
        var name = Marshal.StringToHGlobalUni(dynamic ? "Tman app firewall (live)" : "Tman app firewall");
        try
        {
            var key = Guid.NewGuid();
            var session = new Session { Key = key, Name = name, Flags = dynamic ? SessionFlagDynamic : 0, TxnWaitTimeoutMs = 5000 };
            uint status = FwpmEngineOpen0(null, AuthnDefault, null, &session, out var engine);
            if (status != 0) throw new WfpFailure(status, "open the Windows Filtering Platform engine");
            return new WfpEngine(engine, key);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>Registers Tman's provider and sublayer if they aren't there. Persistent, so persistent filters can hang off them.</summary>
    public void EnsureProviderAndSubLayer()
    {
        var name = Marshal.StringToHGlobalUni("Tman");
        var desc = Marshal.StringToHGlobalUni("App firewall rules made on Tman's App firewall page. Remove all Tman filters there takes them all out.");
        var subName = Marshal.StringToHGlobalUni("Tman app firewall");
        try
        {
            var provider = new Provider { Key = ProviderKey, Name = name, Description = desc, Flags = ProviderFlagPersistent };
            uint status = FwpmProviderAdd0(_engine, &provider, null);
            if (status != 0 && status != FwpEAlreadyExists) throw new WfpFailure(status, "register Tman with the filter engine");
            var key = ProviderKey;
            var sub = new SubLayer { Key = SubLayerKey, Name = subName, Description = desc, Flags = SubLayerFlagPersistent, ProviderKey = &key, Weight = SubLayerWeight };
            status = FwpmSubLayerAdd0(_engine, &sub, null);
            if (status != 0 && status != FwpEAlreadyExists) throw new WfpFailure(status, "add Tman's sublayer to the filter engine");
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(desc);
            Marshal.FreeHGlobal(subName);
        }
    }

    /// <summary>The app identifier WFP matches on: the NT device path of the file, as UTF-16 with its terminator.</summary>
    public static byte[] AppId(string path)
    {
        uint status = FwpmGetAppIdFromFileName0(path, out var blob);
        if (status != 0) throw new WfpFailure(status, $"work out the filter engine's name for {path}");
        try
        {
            return new ReadOnlySpan<byte>(blob->Data, (int)blob->Size).ToArray();
        }
        finally
        {
            void* p = blob;
            FwpmFreeMemory0(&p);
        }
    }

    /// <summary>Adds one filter. Returns its runtime id, which drop events quote. An identical key already present counts as added.</summary>
    public ulong AddFilter(WfpFilterSpec spec)
    {
        var conditions = new List<Condition>();
        var allocated = new List<IntPtr>();
        IntPtr Alloc(int size)
        {
            var p = (IntPtr)NativeMemory.AllocZeroed((nuint)size);
            allocated.Add(p);
            return p;
        }
        try
        {
            if (spec.AppId is { } appId)
            {
                var blob = (ByteBlob*)Alloc(sizeof(ByteBlob));
                blob->Size = (uint)appId.Length;
                blob->Data = (byte*)Alloc(appId.Length);
                appId.CopyTo(new Span<byte>(blob->Data, appId.Length));
                conditions.Add(new Condition { FieldKey = CondAppId, MatchType = MatchEqual, ValueType = TypeByteBlob, Pointer = blob });
            }
            if (spec.RemoteAddress is { } remote)
            {
                if (remote.AddressFamily == AddressFamily.InterNetwork)
                {
                    int prefix = spec.PrefixLength < 0 ? 32 : spec.PrefixLength;
                    var mask = (uint*)Alloc(8);
                    mask[0] = BinaryPrimitives.ReadUInt32BigEndian(remote.GetAddressBytes());
                    mask[1] = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
                    conditions.Add(new Condition { FieldKey = CondRemoteAddress, MatchType = MatchEqual, ValueType = TypeV4AddrMask, Pointer = mask });
                }
                else
                {
                    var mask = (byte*)Alloc(17);
                    remote.GetAddressBytes().CopyTo(new Span<byte>(mask, 16));
                    mask[16] = (byte)(spec.PrefixLength < 0 ? 128 : spec.PrefixLength);
                    conditions.Add(new Condition { FieldKey = CondRemoteAddress, MatchType = MatchEqual, ValueType = TypeV6AddrMask, Pointer = mask });
                }
            }
            if (spec.RemotePort is { } rp) conditions.Add(PortCondition(CondRemotePort, rp, Alloc));
            if (spec.LocalPort is { } lp) conditions.Add(PortCondition(CondLocalPort, lp, Alloc));
            if (spec.Protocol is { } proto) conditions.Add(new Condition { FieldKey = CondProtocol, MatchType = MatchEqual, ValueType = TypeUint8, Uint8 = proto });

            var array = (Condition*)Alloc(Math.Max(1, conditions.Count) * sizeof(Condition));
            for (int i = 0; i < conditions.Count; i++) array[i] = conditions[i];
            var name = Marshal.StringToHGlobalUni(spec.Name);
            var desc = Marshal.StringToHGlobalUni(spec.Description);
            allocated.Add(name);
            allocated.Add(desc);
            var provider = ProviderKey;
            var filter = new Filter
            {
                FilterKey = spec.Key,
                Name = name,
                Description = desc,
                Flags = spec.Persistent ? FilterFlagPersistent : 0,
                ProviderKey = &provider,
                LayerKey = spec.Layer,
                SubLayerKey = SubLayerKey,
                WeightType = TypeUint8,
                WeightUint8 = Math.Min(spec.Weight, (byte)15),
                NumConditions = (uint)conditions.Count,
                Conditions = array,
                ActionType = spec.Block ? ActionBlock : ActionPermit,
            };
            uint status = FwpmFilterAdd0(_engine, &filter, null, out ulong id);
            if (status == FwpEAlreadyExists) return Find(spec.Key)?.Id ?? 0;
            if (status != 0) throw new WfpFailure(status, $"add the filter \"{spec.Name}\"");
            return id;
        }
        finally
        {
            foreach (var p in allocated) NativeMemory.Free((void*)p);
        }
    }

    static Condition PortCondition(Guid field, (ushort Low, ushort High) port, Func<int, IntPtr> alloc)
    {
        if (port.Low == port.High) return new Condition { FieldKey = field, MatchType = MatchEqual, ValueType = TypeUint16, Uint16 = port.Low };
        var range = (Range*)alloc(sizeof(Range));
        range->LowType = TypeUint16;
        range->Low = port.Low;
        range->HighType = TypeUint16;
        range->High = port.High;
        return new Condition { FieldKey = field, MatchType = MatchRange, ValueType = TypeRange, Pointer = range };
    }

    /// <summary>Removes a filter by key. A filter that's already gone counts as removed.</summary>
    public void DeleteFilter(Guid key)
    {
        uint status = FwpmFilterDeleteByKey0(_engine, key);
        if (status != 0 && status != FwpEFilterNotFound && status != FwpENotFound) throw new WfpFailure(status, "remove a filter");
    }

    public void DeleteFilter(ulong id)
    {
        uint status = FwpmFilterDeleteById0(_engine, id);
        if (status != 0 && status != FwpEFilterNotFound && status != FwpENotFound) throw new WfpFailure(status, "remove a filter");
    }

    WfpFilterInfo? Find(Guid key) => ListTmanFilters().FirstOrDefault(f => f.Key == key);

    /// <summary>Every filter in the engine that carries Tman's provider, in any layer.</summary>
    public List<WfpFilterInfo> ListTmanFilters()
    {
        var list = new List<WfpFilterInfo>();
        uint status = FwpmFilterCreateEnumHandle0(_engine, null, out var handle);
        if (status != 0) throw new WfpFailure(status, "list the filters");
        try
        {
            while (true)
            {
                status = FwpmFilterEnum0(_engine, handle, 256, out var entries, out uint count);
                if (status != 0) throw new WfpFailure(status, "list the filters");
                if (count == 0) break;
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        var f = entries[i];
                        if (f->ProviderKey == null || *f->ProviderKey != ProviderKey) continue;
                        list.Add(new WfpFilterInfo(f->FilterKey, f->FilterId, f->Name == IntPtr.Zero ? "" : Marshal.PtrToStringUni(f->Name) ?? "", f->LayerKey,
                            f->ActionType == ActionBlock, (f->Flags & FilterFlagPersistent) != 0));
                    }
                }
                finally
                {
                    void* p = entries;
                    FwpmFreeMemory0(&p);
                }
                if (count < 256) break;
            }
        }
        finally
        {
            FwpmFilterDestroyEnumHandle0(_engine, handle);
        }
        return list;
    }

    /// <summary>Deletes every Tman filter, then the sublayer and provider. Returns how many filters went.</summary>
    public int RemoveEverything()
    {
        var filters = ListTmanFilters();
        uint status = FwpmTransactionBegin0(_engine, 0);
        if (status != 0) throw new WfpFailure(status, "start removing the filters");
        try
        {
            foreach (var f in filters) DeleteFilter(f.Id);
            DeleteProviderAndSubLayer();
            status = FwpmTransactionCommit0(_engine);
            if (status != 0) throw new WfpFailure(status, "finish removing the filters");
        }
        catch
        {
            FwpmTransactionAbort0(_engine);
            throw;
        }
        return filters.Count;
    }

    /// <summary>Takes Tman's sublayer and provider out, so nothing of Tman's shows in the engine while there are no rules. Filters must be gone first.</summary>
    public void DeleteProviderAndSubLayer()
    {
        uint status = FwpmSubLayerDeleteByKey0(_engine, SubLayerKey);
        if (status != 0 && status != FwpESubLayerNotFound && status != FwpENotFound && status != FwpEInUse) throw new WfpFailure(status, "remove Tman's sublayer");
        status = FwpmProviderDeleteByKey0(_engine, ProviderKey);
        if (status != 0 && status != FwpEProviderNotFound && status != FwpENotFound && status != FwpEInUse) throw new WfpFailure(status, "remove Tman's provider");
    }

    /// <summary>Turns on the engine's record of refused connections if it's off. Returns true when this call changed it.</summary>
    public bool EnsureDropEvents()
    {
        uint status = FwpmEngineGetOption0(_engine, OptionCollectNetEvents, out var value);
        if (status == 0)
        {
            // FWP_VALUE0: type at 0, uint32 at 8.
            bool on = *(uint*)value == TypeUint32 && *((uint*)value + 2) != 0;
            FwpmFreeMemory0(&value);
            if (on) return false;
        }
        var set = stackalloc ulong[2];
        ((uint*)set)[0] = TypeUint32;
        ((uint*)set)[2] = 1;
        status = FwpmEngineSetOption0(_engine, OptionCollectNetEvents, set);
        if (status != 0) throw new WfpFailure(status, "turn on the filter engine's record of refused connections");
        return true;
    }

    /// <summary>
    /// Asks for each connection the engine refuses from now on. The engine calls back on a thread of its own; when it
    /// refuses the subscription, Tman reads its event log once a second instead, which is a second late but just as true.
    /// </summary>
    public void SubscribeDrops(Action<WfpDrop> onDrop)
    {
        if (_events != IntPtr.Zero || _poll != null) throw new InvalidOperationException("Already subscribed.");
        _onDrop = onDrop;
        _self = GCHandle.Alloc(this);
        var template = new EnumTemplate { StartTime = 0, EndTime = long.MaxValue };
        var subscription = new Subscription { Template = &template, SessionKey = _sessionKey };
        uint status = FwpmNetEventSubscribe1(_engine, &subscription, &OnNetEvent, (void*)GCHandle.ToIntPtr(_self), out _events);
        if (status != 0)
        {
            subscription = new Subscription();
            status = FwpmNetEventSubscribe0(_engine, &subscription, &OnNetEvent, (void*)GCHandle.ToIntPtr(_self), out _events);
        }
        if (status == 0)
        {
            DropSource = "subscription";
            return;
        }
        SubscribeError = Describe(status);
        _events = IntPtr.Zero;
        _lastDrop = DateTime.UtcNow.ToFileTimeUtc();
        _poll = new Timer(_ => Poll(), null, 1000, 1000);
        DropSource = "polling";
    }

    void Poll()
    {
        if (!Monitor.TryEnter(_pollGate)) return;
        try
        {
            List<WfpDrop> drops;
            try { drops = DropsSince(_lastDrop); }
            catch { return; }
            foreach (var d in drops)
            {
                if (d.Time.ToFileTimeUtc() > _lastDrop) _lastDrop = d.Time.ToFileTimeUtc();
                _onDrop?.Invoke(d);
            }
        }
        finally
        {
            Monitor.Exit(_pollGate);
        }
    }

    /// <summary>Refused connections the engine has recorded after a moment in time (a UTC file time). Public for tests.</summary>
    public List<WfpDrop> DropsSince(long fileTimeUtc)
    {
        var list = new List<WfpDrop>();
        var template = new EnumTemplate { StartTime = fileTimeUtc + 1, EndTime = long.MaxValue };
        uint status = FwpmNetEventCreateEnumHandle0(_engine, &template, out var handle);
        if (status != 0) throw new WfpFailure(status, "read the engine's refused connections");
        try
        {
            while (true)
            {
                status = FwpmNetEventEnum2(_engine, handle, 256, out var entries, out uint count);
                if (status != 0) throw new WfpFailure(status, "read the engine's refused connections");
                if (count == 0) break;
                try
                {
                    for (uint i = 0; i < count; i++)
                        if (Read(entries[i]) is { } drop) list.Add(drop);
                }
                finally
                {
                    void* p = entries;
                    FwpmFreeMemory0(&p);
                }
                if (count < 256) break;
            }
        }
        finally
        {
            FwpmNetEventDestroyEnumHandle0(_engine, handle);
        }
        return list;
    }

    /// <summary>One FWPM_NET_EVENT2 as a drop, or null when it's another kind of event.</summary>
    static WfpDrop? Read(NetEvent* e)
    {
        if (e->Type != EventClassifyDrop || e->Drop == null) return null;
        bool v6 = (e->Flags & FlagIpVersionSet) != 0 && e->IpVersion == 1;
        IPAddress? remote = null;
        if ((e->Flags & FlagRemoteAddrSet) != 0)
            remote = v6 ? new IPAddress(new ReadOnlySpan<byte>(e->RemoteV6, 16)) : new IPAddress(BinaryPrimitives.ReverseEndianness(e->RemoteV4));
        string? app = null;
        if ((e->Flags & FlagAppIdSet) != 0 && e->AppIdData != null && e->AppIdSize >= 2)
            app = DosPath(new string((char*)e->AppIdData, 0, (int)e->AppIdSize / 2).TrimEnd('\0'));
        return new WfpDrop(DateTime.FromFileTimeUtc(e->TimeStamp).ToLocalTime(), app, remote,
            (e->Flags & FlagRemotePortSet) != 0 ? e->RemotePort : 0,
            (e->Flags & FlagLocalPortSet) != 0 ? e->LocalPort : 0,
            (e->Flags & FlagIpProtocolSet) != 0 ? e->IpProtocol : (byte)0,
            e->Drop->FilterId, e->Drop->Direction == 1);
    }

    [UnmanagedCallersOnly]
    static void OnNetEvent(void* context, NetEvent* e)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)context).Target is WfpEngine engine && Read(e) is { } drop) engine._onDrop?.Invoke(drop);
        }
        catch
        {
            // Nothing may escape into the engine's thread.
        }
    }

    static Dictionary<string, string>? _devices;

    /// <summary>Turns "\device\harddiskvolume3\x\y.exe" into "C:\x\y.exe". Unknown devices come back as they are.</summary>
    public static string DosPath(string nt)
    {
        if (nt.Length == 0 || nt[0] != '\\') return nt;
        _devices ??= BuildDeviceMap();
        foreach (var (device, drive) in _devices)
            if (nt.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive + nt[device.Length..];
        if (nt.StartsWith(@"\systemroot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), nt[12..]);
        return nt;
    }

    static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        char* buffer = stackalloc char[1024];
        for (char c = 'A'; c <= 'Z'; c++)
        {
            int n = Native.QueryDosDeviceW($"{c}:", buffer, 1024);
            if (n > 0) map[new string(buffer).TrimEnd('\0')] = $"{c}:";
        }
        return map;
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        if (_events != IntPtr.Zero)
        {
            FwpmNetEventUnsubscribe0(_engine, _events);
            _events = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
        if (_engine != IntPtr.Zero)
        {
            FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
    }

    public static string Describe(uint status) => status switch
    {
        ErrorAccessDenied or 5 => "Windows refused: this needs administrator rights.",
        0x80320001 => "The filter engine's callout isn't there.",
        FwpENotFound => "The filter engine didn't find that item.",
        0x80320013 => "The filter engine isn't recording refused connections.",
        0x80320025 => "The filter engine rejected the weight.",
        0x80320027 => "A condition had the wrong data type.",
        0x8032001F => "The address range isn't valid.",
        0x80320033 => "The filter could never match anything.",
        FwpEInUse => "The item is still in use.",
        0x8032000D => "The filter engine is in a transaction that can't be nested.",
        0x80320020 or 0x80320021 => "The Base Filtering Engine service isn't running. Start it in Services and try again.",
        _ => $"error 0x{status:X8}" + (Marshal.GetPInvokeErrorMessage((int)(status & 0xFFFF)) is { Length: > 0 } text && (status & 0xFFFF0000) == 0x80070000 ? $": {text}" : ""),
    };
}

/// <summary>The filter engine said no. The message is plain text, ready to show.</summary>
public sealed class WfpFailure : Exception
{
    public uint Status { get; }

    internal WfpFailure(uint status, string action) : base($"Couldn't {action}: {WfpEngine.Describe(status)}") => Status = status;
}
