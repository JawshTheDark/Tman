using System.Runtime.InteropServices;

namespace Tman.Core;

/// <summary>One thread that's waiting on something Windows can trace, and what that wait leads to.</summary>
public sealed record BlockedThread(int Tid, string Name, TimeSpan Waited, bool Deadlock, IReadOnlyList<string> Steps);

/// <param name="Threads">All the process's threads.</param>
/// <param name="Checked">Those that could be blocked; the rest are running, or idle on a work queue or timer.</param>
/// <param name="Denied">Those Windows wouldn't let Tman look at.</param>
public sealed record WaitChainReport(int Threads, int Checked, IReadOnlyList<BlockedThread> Blocked, int Denied)
{
    public bool Deadlocked => Blocked.Any(b => b.Deadlock);
}

/// <summary>
/// Asks Windows' Wait Chain Traversal what each thread of a process is stuck on. A chain alternates threads and the
/// things they wait for, and can cross into other processes, which is how a hang in one app gets traced to another.
/// </summary>
internal static unsafe class WaitChains
{
    const int MaxNodes = 16;
    const uint OutOfProcess = 0x1, OutOfProcessCom = 0x2, OutOfProcessLocks = 0x4, NetworkIo = 0x8;
    const int ErrorAccessDenied = 5;

    enum ObjectType
    {
        CriticalSection = 1, SendMessage, Mutex, Alpc, Com, ThreadWait, ProcessWait, Thread, ComActivation, Unknown, SocketIo, SmbIo,
    }

    enum Status
    {
        NoAccess = 1, Running, Blocked, PidOnly, PidOnlyRpcss, Owned, NotOwned, Abandoned, Unknown, Error,
    }

    /// <summary>WAITCHAIN_NODE_INFO: a lock's name and a thread's ids share the same space.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 280)]
    struct Node
    {
        [FieldOffset(0)] public ObjectType Type;
        [FieldOffset(4)] public Status Status;
        [FieldOffset(8)] public fixed char ObjectName[128];
        [FieldOffset(8)] public int ProcessId;
        [FieldOffset(12)] public int ThreadId;
        /// <summary>Documented only as "the wait time", but it's the kernel tick count when the wait began, not a duration.</summary>
        [FieldOffset(16)] public uint WaitStartTick;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);

    [DllImport("advapi32.dll")]
    static extern void CloseThreadWaitChainSession(IntPtr session);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetThreadWaitChain(IntPtr session, nint context, uint flags, int threadId, ref int nodeCount, Node* nodes, out int isCycle);

    [DllImport("advapi32.dll")]
    static extern void RegisterWaitChainCOMCallback(IntPtr callState, IntPtr activationState);

    [DllImport("kernel32.dll")]
    static extern bool GetSystemTimeAdjustment(out uint adjustment, out uint increment, out bool disabled);

    static readonly SemaphoreSlim OneAtATime = new(1, 1);
    static int _comRegistered;

    /// <summary>How long ago a kernel tick was. Zero when it can't be worked out.</summary>
    static TimeSpan Since(uint tick)
    {
        if (tick == 0 || !GetSystemTimeAdjustment(out _, out uint hundredNsPerTick, out _)) return TimeSpan.Zero;
        double waitedMs = Environment.TickCount64 - tick * (hundredNsPerTick / 10000.0);
        return waitedMs > 0 ? TimeSpan.FromMilliseconds(waitedMs) : TimeSpan.Zero;
    }

    /// <summary>
    /// Each query takes about 30 ms however few details are asked for, which adds up to many seconds in a busy process.
    /// A thread that's running isn't waiting, and one parked on a work queue or a timer is idle by design and is never in
    /// a chain, so these are skipped.
    /// </summary>
    static bool CanBeBlocked(ThreadInfo t) =>
        t.State.StartsWith("Waiting", StringComparison.Ordinal) && t.State is not ("Waiting · Queue" or "Waiting · Sleeping");

    /// <summary>Without this, a chain stops at a COM call instead of following it into the other process.</summary>
    static void RegisterCom()
    {
        if (Interlocked.Exchange(ref _comRegistered, 1) != 0) return;
        if (NativeLibrary.TryLoad("ole32.dll", out var ole)
            && NativeLibrary.TryGetExport(ole, "CoGetCallState", out var call)
            && NativeLibrary.TryGetExport(ole, "CoGetActivationState", out var activation))
            RegisterWaitChainCOMCallback(call, activation);
    }

    /// <summary>
    /// Checks every thread and reports how far it's got. Blocks, so call it off the UI thread. Returns null when the
    /// process has exited.
    /// </summary>
    public static WaitChainReport? Analyze(int pid, long createTime, Func<int, string> processName, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        // Wait chain sessions in use at the same time crash inside advapi32 with an access violation, which nothing can
        // catch. So: one session, one thread, one analysis at a time across all Details windows.
        OneAtATime.Wait(cancel);
        try
        {
            var threads = ProcessInspector.Threads(pid, createTime);
            if (threads == null) return null;

            TokenPrivileges.Enable(TokenPrivileges.Debug);
            RegisterCom();
            var session = OpenThreadWaitChainSession(0, IntPtr.Zero);
            if (session == IntPtr.Zero) throw new InvalidOperationException("Windows couldn't start a wait chain session.");

            var blocked = new List<BlockedThread>();
            int denied = 0, done = 0;
            var candidates = threads.Where(CanBeBlocked).ToList();
            try
            {
                foreach (var thread in candidates)
                {
                    cancel.ThrowIfCancellationRequested();
                    progress?.Report((done++, candidates.Count));
                    if (Check(session, thread, processName, out bool accessDenied) is { } found) blocked.Add(found);
                    if (accessDenied) denied++;
                }
            }
            finally
            {
                CloseThreadWaitChainSession(session);
            }
            return new WaitChainReport(threads.Count, candidates.Count, blocked.OrderByDescending(b => b.Deadlock).ThenByDescending(b => b.Waited).ToList(), denied);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    static BlockedThread? Check(IntPtr session, ThreadInfo thread, Func<int, string> processName, out bool accessDenied)
    {
        accessDenied = false;
        Node* nodes = stackalloc Node[MaxNodes];
        int count = MaxNodes;
        if (!GetThreadWaitChain(session, 0, OutOfProcess | OutOfProcessCom | OutOfProcessLocks | NetworkIo, thread.Tid, ref count, nodes, out int cycle))
        {
            accessDenied = Marshal.GetLastPInvokeError() == ErrorAccessDenied;
            return null;
        }
        // A blocked thread with nothing after it is idle: waiting for work, on something Windows doesn't trace.
        if (count < 2 || nodes[0].Status != Status.Blocked) return null;
        return new BlockedThread(thread.Tid, thread.Name, Since(nodes[0].WaitStartTick), cycle != 0,
            Steps(nodes, Math.Min(count, MaxNodes), thread.Tid, processName));
    }

    static List<string> Steps(Node* nodes, int count, int startTid, Func<int, string> processName)
    {
        var steps = new List<string>(count);
        for (int i = 1; i < count; i++)
        {
            if (nodes[i].Type != ObjectType.Thread) steps.Add(DescribeObject(ref nodes[i]));
            else if (nodes[i].ThreadId == startTid) steps.Add($"Back to thread {startTid}, the one that started this chain, so the loop can never end");
            else steps.Add(DescribeThread(in nodes[i], processName));
        }
        return steps;
    }

    static string DescribeObject(ref Node n)
    {
        string name;
        fixed (char* text = n.ObjectName) name = new string(text);
        string what = n.Type switch
        {
            ObjectType.CriticalSection => "a critical section (a lock inside the program)",
            ObjectType.SendMessage => "a reply to a window message",
            ObjectType.Mutex => "a mutex",
            ObjectType.Alpc => "a reply from another process (ALPC)",
            ObjectType.Com => "a COM call to finish",
            ObjectType.ThreadWait => "another thread to finish",
            ObjectType.ProcessWait => "another process to finish",
            ObjectType.ComActivation => "a COM object to start",
            ObjectType.SocketIo => "network traffic",
            ObjectType.SmbIo => "a network file share",
            _ => "something Windows can't identify",
        };
        bool isLock = n.Type is ObjectType.CriticalSection or ObjectType.Mutex;
        string state = n.Status switch
        {
            Status.Owned when isLock => ", held by:",
            Status.NotOwned when isLock => ", which nothing holds",
            Status.Abandoned => ", abandoned by a thread that ended",
            Status.NoAccess => " (no permission to look further)",
            _ => "",
        };
        return "Waiting for " + what + (name.Length > 0 ? $" \"{name}\"" : "") + state;
    }

    static string DescribeThread(in Node n, Func<int, string> processName)
    {
        string where = n.ProcessId > 0 ? $"{processName(n.ProcessId)} (PID {n.ProcessId})" : "an unknown process";
        return n.Status switch
        {
            Status.PidOnly => $"A thread in {where}",
            Status.PidOnlyRpcss => $"A thread in {where}, the Windows RPC service",
            Status.NoAccess => $"Thread {n.ThreadId} in {where} (no permission to look further)",
            Status.Running => $"Thread {n.ThreadId} in {where}, which is running",
            Status.Blocked => $"Thread {n.ThreadId} in {where}, which is blocked too",
            _ => $"Thread {n.ThreadId} in {where}",
        };
    }
}
