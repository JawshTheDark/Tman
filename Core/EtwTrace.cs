using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tman.Core;

/// <summary>Windows refused or lost a trace session. The message is plain text, ready to show.</summary>
public sealed class EtwFailure : Exception
{
    public EtwFailure(string message) : base(message) { }
}

/// <summary>The fields of EVENT_RECORD that Tman reads, at their offsets in evntcons.h on 64-bit Windows.</summary>
[StructLayout(LayoutKind.Explicit, Size = 112)]
internal unsafe struct EtwEvent
{
    [FieldOffset(12)] public uint ProcessId;
    [FieldOffset(16)] public long TimeStamp;
    [FieldOffset(24)] public Guid ProviderId;
    [FieldOffset(40)] public ushort Id;
    [FieldOffset(42)] public byte Version;
    [FieldOffset(45)] public byte Opcode;
    [FieldOffset(80)] public byte Processor;
    [FieldOffset(86)] public ushort UserDataLength;
    [FieldOffset(96)] public byte* UserData;
    [FieldOffset(104)] public IntPtr UserContext;
}

internal unsafe delegate void EtwHandler(EtwEvent* e);

/// <summary>
/// A real-time ETW session and its consumer thread. Windows keeps a session running after the process that started it
/// dies, and no API ties the two together, so each session carries this process's id in its name and is stopped on every
/// way out: <see cref="Stop"/>, process exit, an unhandled exception, the forced exit in App, and for a hard crash or
/// kill, a watchdog process. <see cref="StopOrphans"/> clears anything that still got through before each new session.
/// </summary>
internal sealed unsafe class EtwTrace
{
    public const uint ProcessTraceRealTime = 0x100, ProcessTraceRawTimestamp = 0x1000, ProcessTraceEventRecord = 0x10000000;
    public const uint WnodeTracedGuid = 0x20000;
    public const int HeaderSize = 120, NameBytes = 2048, PropertiesSize = HeaderSize + 2 * NameBytes;
    const uint RealTimeMode = 0x100, SystemLoggerMode = 0x02000000, ControlStop = 1, ClockQpc = 1;
    const uint EnableProvider = 1;
    const byte LevelInformation = 4;
    const uint ErrorAccessDenied = 5, ErrorAlreadyExists = 183, ErrorMoreData = 234, ErrorNoSystemResources = 1450, ErrorInstanceNotFound = 4201;
    const ulong InvalidConsumer = ulong.MaxValue;
    const string Prefix = "Tman ";

    /// <summary>EVENT_TRACE_PROPERTIES; the session and log file names follow it in the same buffer.</summary>
    [StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
    public struct Properties
    {
        [FieldOffset(0)] public uint WnodeBufferSize;
        [FieldOffset(24)] public Guid WnodeGuid;
        [FieldOffset(40)] public uint ClientContext;
        [FieldOffset(44)] public uint WnodeFlags;
        [FieldOffset(48)] public uint BufferSize;
        [FieldOffset(52)] public uint MinimumBuffers;
        [FieldOffset(56)] public uint MaximumBuffers;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimer;
        [FieldOffset(72)] public uint EnableFlags;
        [FieldOffset(88)] public uint EventsLost;
        [FieldOffset(100)] public uint RealTimeBuffersLost;
        [FieldOffset(112)] public uint LogFileNameOffset;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    public struct LogFile
    {
        [FieldOffset(0)] public char* LogFileName;
        [FieldOffset(8)] public char* LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public delegate* unmanaged<EtwEvent*, void> EventRecordCallback;
        [FieldOffset(440)] public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint StartTraceW(out ulong handle, string name, void* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ControlTraceW(ulong handle, string? name, void* properties, uint code);

    [DllImport("advapi32.dll")]
    public static extern uint EnableTraceEx2(ulong handle, in Guid provider, uint code, byte level, ulong matchAny, ulong matchAll, uint timeout, void* parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern ulong OpenTraceW(LogFile* logFile);

    [DllImport("advapi32.dll")]
    public static extern uint ProcessTrace(ulong* handles, uint count, void* start, void* end);

    [DllImport("advapi32.dll")]
    public static extern uint CloseTrace(ulong handle);

    [DllImport("advapi32.dll")]
    static extern uint QueryAllTracesW(IntPtr* properties, uint count, out uint found);

    static readonly List<EtwTrace> Live = new();
    static int _exitHooked;

    readonly string _name;
    readonly EtwHandler _onEvent;
    GCHandle _context;
    LogFile* _logFile;
    IntPtr _nameText;
    ulong _consumer = InvalidConsumer;
    ulong _session;
    Thread? _thread;
    volatile bool _sessionOpen;
    int _stopRequested;
    uint _pumpStatus, _lost;

    EtwTrace(string kind, EtwHandler onEvent)
    {
        _name = $"{Prefix}{kind} {Environment.ProcessId}";
        _onEvent = onEvent;
    }

    /// <summary>False once the session has ended, whether we stopped it or something else did.</summary>
    public bool Running => _thread is { IsAlive: true };

    /// <summary>True when the consumer thread has returned, so its handler can no longer run.</summary>
    public bool Finished => _thread is not { IsAlive: true };

    /// <summary>Why the consumer stopped, when it stopped because of an error.</summary>
    public string? PumpError => _pumpStatus == 0 ? null : Describe(_pumpStatus);

    /// <summary>A session of the SystemTraceProvider with its own name, so it can't clash with the NT Kernel Logger.</summary>
    public static EtwTrace StartKernel(string kind, uint enableFlags, EtwHandler onEvent)
    {
        int buffers = Math.Max(16, Environment.ProcessorCount * 2);
        return Start(kind, onEvent, p =>
        {
            p->LogFileMode = RealTimeMode | SystemLoggerMode;
            p->EnableFlags = enableFlags;
            p->BufferSize = 64;
            p->MinimumBuffers = (uint)buffers;
            p->MaximumBuffers = (uint)buffers * 8;
        }, null, 0);
    }

    /// <summary>A session listening to one manifest provider. Kernel providers can't log to paged buffers, so this leaves that mode off.</summary>
    public static EtwTrace StartProvider(string kind, Guid provider, ulong keywords, EtwHandler onEvent) =>
        Start(kind, onEvent, p =>
        {
            p->LogFileMode = RealTimeMode;
            p->BufferSize = 16;
            p->MinimumBuffers = 4;
            p->MaximumBuffers = 64;
        }, provider, keywords);

    /// <summary>Adds a manifest provider to a running session, so one consumer thread can hear several providers.</summary>
    public void Enable(Guid provider, ulong keywords)
    {
        if (!_sessionOpen) throw new EtwFailure("The trace session isn't open.");
        uint status = EnableTraceEx2(_session, provider, EnableProvider, LevelInformation, keywords, 0, 0, null);
        if (status != 0) throw new EtwFailure(Describe(status));
    }

    delegate void Configure(Properties* p);

    static EtwTrace Start(string kind, EtwHandler onEvent, Configure configure, Guid? provider, ulong keywords)
    {
        HookExit();
        StopOrphans();
        EtwWatchdog.Ensure();
        var trace = new EtwTrace(kind, onEvent);
        // Listed before the session exists, so no exit path can miss it.
        lock (Live) Live.Add(trace);
        try
        {
            trace.Open(configure, provider, keywords);
            return trace;
        }
        catch
        {
            trace.Stop();
            throw;
        }
    }

    void Open(Configure configure, Guid? provider, ulong keywords)
    {
        byte* buffer = stackalloc byte[PropertiesSize];
        uint status = Begin(buffer, configure, out ulong session);
        if (status == ErrorAlreadyExists)
        {
            // The name holds this process's id, so this is our own leftover and it's safe to stop.
            StopByName(_name, out _);
            status = Begin(buffer, configure, out session);
        }
        if (status != 0) throw new EtwFailure(Describe(status));
        _sessionOpen = true;
        _session = session;

        if (provider is { } guid) Enable(guid, keywords);

        _context = GCHandle.Alloc(this);
        _nameText = Marshal.StringToHGlobalUni(_name);
        _logFile = (LogFile*)NativeMemory.AllocZeroed((nuint)sizeof(LogFile));
        _logFile->LoggerName = (char*)_nameText;
        _logFile->ProcessTraceMode = ProcessTraceRealTime | ProcessTraceEventRecord | ProcessTraceRawTimestamp;
        _logFile->EventRecordCallback = &OnRecord;
        _logFile->Context = GCHandle.ToIntPtr(_context);
        _consumer = OpenTraceW(_logFile);
        if (_consumer == InvalidConsumer) throw new EtwFailure(Describe((uint)Marshal.GetLastPInvokeError()));

        _thread = new Thread(Pump) { IsBackground = true, Name = _name };
        _thread.Start();
    }

    uint Begin(byte* buffer, Configure configure, out ulong session)
    {
        new Span<byte>(buffer, PropertiesSize).Clear();
        var p = (Properties*)buffer;
        p->WnodeBufferSize = PropertiesSize;
        p->WnodeFlags = WnodeTracedGuid;
        p->WnodeGuid = Guid.NewGuid();
        p->ClientContext = ClockQpc;
        p->FlushTimer = 1;
        p->LoggerNameOffset = HeaderSize;
        configure(p);
        return StartTraceW(out session, _name, buffer);
    }

    void Pump()
    {
        ulong handle = _consumer;
        _pumpStatus = ProcessTrace(&handle, 1, null, null);
    }

    [UnmanagedCallersOnly]
    static void OnRecord(EtwEvent* e)
    {
        // An exception can't cross back into ProcessTrace; it would end the process.
        try { ((EtwTrace)GCHandle.FromIntPtr(e->UserContext).Target!)._onEvent(e); }
        catch { }
    }

    /// <summary>Asks Windows to end the session. Safe from any thread, any number of times, and quick enough for exit paths.</summary>
    void RequestStop()
    {
        if (!_sessionOpen || Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        uint status = StopByName(_name, out _lost);
        if (status is 0 or ErrorInstanceNotFound) return;
        // Stopping is the one thing here that mustn't quietly fail, so a hiccup gets a second try.
        Thread.Sleep(50);
        StopByName(_name, out _lost);
    }

    /// <summary>Stops the session and waits for its last events. Returns how many events or buffers Windows reported lost.</summary>
    public uint Stop()
    {
        RequestStop();
        if (_consumer != InvalidConsumer)
        {
            // ProcessTrace drains what's buffered, then returns by itself. CloseTrace is the fallback when it doesn't.
            if (_thread is { } t && !t.Join(4000))
            {
                CloseTrace(_consumer);
                t.Join(2000);
            }
            else CloseTrace(_consumer);
            _consumer = InvalidConsumer;
        }
        if (Finished)
        {
            if (_context.IsAllocated) _context.Free();
            if (_logFile != null) NativeMemory.Free(_logFile);
            if (_nameText != IntPtr.Zero) Marshal.FreeHGlobal(_nameText);
            _logFile = null;
            _nameText = IntPtr.Zero;
        }
        lock (Live) Live.Remove(this);
        return _lost;
    }

    /// <summary>Stops every session this process owns, without waiting for consumers. For exit paths.</summary>
    public static void StopAllNow()
    {
        EtwTrace[] all;
        lock (Live) all = Live.ToArray();
        foreach (var trace in all)
        {
            try { trace.RequestStop(); }
            catch { }
        }
    }

    static void HookExit()
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAllNow();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => StopAllNow();
        App.Terminating += StopAllNow;
    }

    static uint StopByName(string name, out uint lost)
    {
        byte* buffer = stackalloc byte[PropertiesSize];
        new Span<byte>(buffer, PropertiesSize).Clear();
        var p = (Properties*)buffer;
        p->WnodeBufferSize = PropertiesSize;
        p->LoggerNameOffset = HeaderSize;
        p->LogFileNameOffset = HeaderSize + NameBytes;
        uint status = ControlTraceW(0, name, buffer, ControlStop);
        lost = status == 0 ? p->EventsLost + p->RealTimeBuffersLost : 0;
        return status;
    }

    /// <summary>Stops sessions left by a Tman that's gone. Sessions of a Tman that's still running are left alone.</summary>
    public static void StopOrphans()
    {
        foreach (var name in SessionNames())
            if (OwnerPid(name) is { } pid && !IsTman(pid))
                StopByName(name, out _);
    }

    /// <summary>The process that named a session, for names in the form "Tman Latency 1234".</summary>
    public static int? OwnerPid(string session)
    {
        if (!session.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return int.TryParse(session[(session.LastIndexOf(' ') + 1)..], out var pid) ? pid : null;
    }

    static bool IsTman(int pid)
    {
        try
        {
            using var other = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            return string.Equals(other.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static List<string> SessionNames()
    {
        const int max = 64;
        var names = new List<string>();
        var storage = new byte[max * PropertiesSize];
        IntPtr* pointers = stackalloc IntPtr[max];
        fixed (byte* start = storage)
        {
            for (int i = 0; i < max; i++)
            {
                var p = (Properties*)(start + i * PropertiesSize);
                p->WnodeBufferSize = PropertiesSize;
                p->LoggerNameOffset = HeaderSize;
                p->LogFileNameOffset = HeaderSize + NameBytes;
                pointers[i] = (IntPtr)p;
            }
            uint status = QueryAllTracesW(pointers, max, out uint found);
            if (status != 0 && status != ErrorMoreData) return names;
            for (int i = 0; i < found && i < max; i++)
            {
                var p = (Properties*)pointers[i];
                if (p->LoggerNameOffset != 0) names.Add(new string((char*)((byte*)p + p->LoggerNameOffset)));
            }
        }
        return names;
    }

    public static string Describe(uint status) => status switch
    {
        ErrorAccessDenied => "Windows only lets administrators run this kind of trace.",
        ErrorNoSystemResources => "Windows has no free trace sessions. Close other tracing tools, such as LatencyMon or Windows Performance Recorder, and try again.",
        _ => $"The trace session failed: {new Win32Exception((int)status).Message}",
    };
}

/// <summary>
/// The last line of defense against a leaked session. A second copy of Tman waits for the first to die, then stops
/// whatever it left behind. It runs from a module initializer, ahead of WPF, so it costs only a small hidden process
/// and only once Tman has started a trace. It ends by itself when the first copy exits.
/// </summary>
internal static class EtwWatchdog
{
    const string Flag = "--etw-watchdog";
    const uint Synchronize = 0x00100000, Infinite = 0xFFFFFFFF;
    static readonly object Gate = new();
    static Process? _child;

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_child is { HasExited: false }) return;
            _child?.Dispose();
            _child = null;
            try
            {
                _child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{Flag} {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch
            {
                // StopOrphans still clears a leak before the next trace.
            }
        }
    }

#pragma warning disable CA2255 // Deliberate: this has to run before Main so the watchdog never starts WPF.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RunIfWatchdog()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length != 3 || args[1] != Flag || !int.TryParse(args[2], out var pid)) return;
        try
        {
            var owner = Native.OpenProcess(Synchronize, false, pid);
            if (owner != IntPtr.Zero)
            {
                WaitForSingleObject(owner, Infinite);
                Native.CloseHandle(owner);
            }
            EtwTrace.StopOrphans();
        }
        catch { }
        Environment.Exit(0);
    }
}
