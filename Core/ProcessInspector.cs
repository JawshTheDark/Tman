using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tman.Core;

public sealed record ModuleInfo(string Name, string Path, ulong Base, uint Size);

public sealed record ThreadInfo(int Tid, string Name, string State, int Priority, int BasePriority, long CpuTime, long Created,
    uint ContextSwitches, ulong StartAddress);

/// <summary>Looks inside one process: loaded modules, environment, threads; plus memory dumps and CPU affinity.</summary>
internal static unsafe class ProcessInspector
{
    const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_SET_INFORMATION = 0x0200, PROCESS_DUP_HANDLE = 0x0040;
    const uint THREAD_QUERY_INFORMATION = 0x0040, THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
    const uint TH32CS_SNAPMODULE = 0x8, TH32CS_SNAPMODULE32 = 0x10;
    const int ERROR_BAD_LENGTH = 24;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Module32FirstW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Module32NextW(IntPtr snapshot, ref MODULEENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr process, IntPtr address, void* buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint access, bool inherit, int tid);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationThread(IntPtr thread, int infoClass, void* info, int length, out int returnLength);

    [DllImport("kernel32.dll")]
    static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessAffinityMask(IntPtr process, out nuint processMask, out nuint systemMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessAffinityMask(IntPtr process, nuint mask);

    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(IntPtr process, int pid, SafeFileHandle file, uint type, IntPtr exception, IntPtr userStream, IntPtr callback);

    static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

    static IntPtr Open(int pid, uint access, out string? error)
    {
        var h = Native.OpenProcess(access, false, pid);
        error = h == IntPtr.Zero ? LastError() : null;
        return h;
    }

    // ---- Modules ----

    public static List<ModuleInfo>? Modules(int pid, out string? error)
    {
        IntPtr snap = InvalidHandle;
        for (int attempt = 0; attempt < 5 && snap == InvalidHandle; attempt++)
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
            if (snap == InvalidHandle && Marshal.GetLastWin32Error() != ERROR_BAD_LENGTH) break; // BAD_LENGTH = loader busy, retry
        }
        if (snap == InvalidHandle)
        {
            error = LastError();
            return null;
        }
        try
        {
            var list = new List<ModuleInfo>();
            var e = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            for (bool ok = Module32FirstW(snap, ref e); ok; ok = Module32NextW(snap, ref e))
                list.Add(new ModuleInfo(e.szModule, e.szExePath, (ulong)e.modBaseAddr, e.modBaseSize));
            error = null;
            return list;
        }
        finally
        {
            Native.CloseHandle(snap);
        }
    }

    // ---- Environment (read from the process's PEB) ----

    public static List<KeyValuePair<string, string>>? Environment(int pid, out string? currentDirectory, out string? error)
    {
        currentDirectory = null;
        var h = Open(pid, PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, out error);
        if (h == IntPtr.Zero) h = Open(pid, Native.PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, out error);
        if (h == IntPtr.Zero) return null;
        try
        {
            PROCESS_BASIC_INFORMATION pbi;
            if (Native.NtQueryInformationProcess(h, 0, &pbi, sizeof(PROCESS_BASIC_INFORMATION), out _) != 0 || pbi.PebBaseAddress == IntPtr.Zero)
            {
                error = "Couldn't locate the process environment block.";
                return null;
            }
            // x64 layouts: PEB.ProcessParameters at 0x20; in RTL_USER_PROCESS_PARAMETERS, CurrentDirectory at 0x38,
            // Environment at 0x80 and EnvironmentSize at 0x3F0.
            IntPtr parameters;
            if (!Read(h, pbi.PebBaseAddress + 0x20, &parameters, sizeof(IntPtr)) || parameters == IntPtr.Zero)
            {
                error = LastError();
                return null;
            }
            Native.UNICODE_STRING cwd;
            if (Read(h, parameters + 0x38, &cwd, sizeof(Native.UNICODE_STRING)) && cwd.Length > 0 && cwd.Buffer != IntPtr.Zero)
                currentDirectory = ReadString(h, cwd.Buffer, cwd.Length);

            IntPtr env;
            nint envSize = 0;
            if (!Read(h, parameters + 0x80, &env, sizeof(IntPtr)) || env == IntPtr.Zero)
            {
                error = LastError();
                return null;
            }
            Read(h, parameters + 0x3F0, &envSize, sizeof(nint));
            if (envSize <= 0 || envSize > 4 << 20) envSize = 64 * 1024;
            var text = ReadString(h, env, (int)envSize, stopAtDoubleNull: true);
            if (text == null)
            {
                error = LastError();
                return null;
            }
            var vars = new List<KeyValuePair<string, string>>();
            foreach (var entry in text.Split('\0'))
            {
                if (entry.Length == 0) break;
                int eq = entry.IndexOf('=', 1); // names like "=C:" hold per-drive directories; skip those
                if (entry[0] == '=' || eq < 0) continue;
                vars.Add(new(entry[..eq], entry[(eq + 1)..]));
            }
            error = null;
            return vars;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    static bool Read(IntPtr h, IntPtr address, void* buffer, int size) =>
        ReadProcessMemory(h, address, buffer, size, out var read) && read == size;

    static string? ReadString(IntPtr h, IntPtr address, int bytes, bool stopAtDoubleNull = false)
    {
        var buffer = new byte[bytes];
        nint read;
        fixed (byte* p = buffer)
        {
            // Environment blocks can end before the reported size; fall back to smaller reads.
            if (!ReadProcessMemory(h, address, p, bytes, out read) || read == 0)
            {
                if (!stopAtDoubleNull) return null;
                read = 0;
                for (int chunk = 4096; read < bytes && ReadProcessMemory(h, address + (int)read, p + read, Math.Min(chunk, bytes - (int)read), out var got) && got > 0;)
                    read += got;
                if (read == 0) return null;
            }
        }
        var s = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)read & ~1);
        if (stopAtDoubleNull)
        {
            int end = s.IndexOf("\0\0", StringComparison.Ordinal);
            if (end >= 0) s = s[..(end + 1)];
        }
        return s;
    }

    // ---- Threads ----

    static readonly string[] WaitReasons =
    {
        "Executive", "Free page", "Page in", "Pool allocation", "Sleeping", "Suspended", "User request",
        "Executive", "Free page", "Page in", "Pool allocation", "Sleeping", "Suspended", "User request",
        "Event pair", "Queue", "LPC receive", "LPC reply", "Virtual memory", "Page out", "Rendezvous", "Keyed event",
        "Terminated", "Process in swap", "CPU rate control", "Callout stack", "Kernel", "Resource", "Push lock",
        "Mutex", "Quantum end", "Dispatch int", "Preempted", "Yield execution", "Fast mutex", "Guarded mutex",
        "Rundown", "Alert by thread ID", "Deferred preempt",
    };

    /// <summary>Threads of one process instance, straight from the system process list (no handle needed).</summary>
    public static List<ThreadInfo>? Threads(int pid, long createTime)
    {
        var buffer = new byte[2 << 20];
        int status;
        while (true)
        {
            fixed (byte* p = buffer) status = Native.NtQuerySystemInformation(5, p, buffer.Length, out var needed);
            if (status != Native.STATUS_INFO_LENGTH_MISMATCH) break;
            buffer = new byte[buffer.Length * 2];
        }
        if (status != 0) return null;

        var raw = new List<Native.SYSTEM_THREAD_INFORMATION>();
        fixed (byte* basePtr = buffer)
        {
            int offset = 0;
            while (true)
            {
                var spi = (Native.SYSTEM_PROCESS_INFORMATION*)(basePtr + offset);
                if ((int)spi->UniqueProcessId == pid && spi->CreateTime == createTime)
                {
                    var th = (Native.SYSTEM_THREAD_INFORMATION*)(spi + 1);
                    for (int i = 0; i < spi->NumberOfThreads; i++) raw.Add(th[i]);
                    break;
                }
                if (spi->NextEntryOffset == 0) return null;
                offset += (int)spi->NextEntryOffset;
            }
        }

        var list = new List<ThreadInfo>(raw.Count);
        foreach (var t in raw)
        {
            int tid = (int)t.UniqueThread;
            var (name, start) = ThreadExtras(tid);
            list.Add(new ThreadInfo(tid, name, StateText(t.ThreadState, t.WaitReason), t.Priority, t.BasePriority,
                t.KernelTime + t.UserTime, t.CreateTime, t.ContextSwitches, start != 0 ? start : (ulong)t.StartAddress));
        }
        return list;
    }

    static string StateText(uint state, uint reason) => state switch
    {
        0 => "Initialized",
        1 => "Ready",
        2 => "Running",
        3 => "Standby",
        4 => "Terminated",
        5 => reason == 5 || reason == 12 ? "Suspended" : $"Waiting · {(reason < WaitReasons.Length ? WaitReasons[reason] : reason.ToString())}",
        6 => "Transition",
        7 => "Deferred ready",
        _ => $"State {state}",
    };

    /// <summary>The thread's name (SetThreadDescription) and the address its code started at, when we're allowed to look.</summary>
    static (string Name, ulong Start) ThreadExtras(int tid)
    {
        var h = OpenThread(THREAD_QUERY_INFORMATION, false, tid);
        if (h == IntPtr.Zero) h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, tid);
        if (h == IntPtr.Zero) return ("", 0);
        try
        {
            string name = "";
            if (GetThreadDescription(h, out var desc) >= 0 && desc != IntPtr.Zero)
            {
                name = Marshal.PtrToStringUni(desc) ?? "";
                LocalFree(desc);
            }
            ulong start = 0;
            NtQueryInformationThread(h, 9 /* ThreadQuerySetWin32StartAddress */, &start, sizeof(ulong), out _);
            return (name, start);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>"module.dll+0x1a2b" for an address inside one of the given modules.</summary>
    public static string Symbolize(ulong address, IReadOnlyList<ModuleInfo>? modules)
    {
        if (address == 0) return "";
        if (modules != null)
            foreach (var m in modules)
                if (address >= m.Base && address < m.Base + m.Size)
                    return $"{m.Name}+0x{address - m.Base:x}";
        return $"0x{address:x}";
    }

    // ---- Dump ----

    /// <summary>Writes a full memory dump (what Task Manager's "Create memory dump file" makes). Blocks; run off the UI thread.</summary>
    public static bool WriteDump(int pid, string path, out string? error)
    {
        var h = Open(pid, PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_DUP_HANDLE, out error);
        if (h == IntPtr.Zero) h = Open(pid, PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, out error);
        if (h == IntPtr.Zero) return false;
        try
        {
            using var file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
            // Full memory + handle data + unloaded modules + memory info + thread info.
            const uint type = 0x2 | 0x4 | 0x20 | 0x800 | 0x1000;
            if (MiniDumpWriteDump(h, pid, file, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)) return true;
            error = LastError();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            Native.CloseHandle(h);
        }
        try { File.Delete(path); } catch { }
        return false;
    }

    // ---- Affinity ----

    public static bool GetAffinity(int pid, out ulong mask, out ulong system, out string? error)
    {
        mask = system = 0;
        var h = Open(pid, Native.PROCESS_QUERY_LIMITED_INFORMATION, out error);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!GetProcessAffinityMask(h, out var m, out var s))
            {
                error = LastError();
                return false;
            }
            mask = m;
            system = s;
            return true;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static bool SetAffinity(int pid, ulong mask, out string? error)
    {
        var h = Open(pid, PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, out error);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (SetProcessAffinityMask(h, (nuint)mask)) return true;
            error = LastError();
            return false;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }
}
