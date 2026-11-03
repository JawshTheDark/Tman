using System.Runtime.InteropServices;

namespace Tman.Core;

public enum OpenFileKind { File, Folder, Module }

public sealed record OpenFile(int Pid, OpenFileKind Kind, string Path);

public sealed record HandleScan(List<OpenFile> Results, int Handles, int Processes, int Denied, TimeSpan Elapsed);

/// <summary>
/// Finds which processes have a file or folder open: walks the system handle table, copies each file handle
/// into Tman and asks Windows for its path. Optionally also matches loaded DLLs. Without admin rights only
/// processes running as you can be inspected.
/// </summary>
internal static unsafe class Handles
{
    const int SystemExtendedHandleInformation = 64;
    const uint PROCESS_DUP_HANDLE = 0x0040;
    const uint DUPLICATE_SAME_ACCESS = 0x2;
    const uint FILE_TYPE_DISK = 1;
    const int LookupTimeoutMs = 150;

    [StructLayout(LayoutKind.Sequential)]
    struct HandleEntry
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern uint GetFileType(IntPtr file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(IntPtr file, char* path, uint size, uint flags);

    /// <summary>
    /// Scans every process (or just <paramref name="onlyPid"/>) for open files whose path satisfies <paramref name="match"/>.
    /// Blocking; call from a background thread.
    /// </summary>
    public static HandleScan Scan(Func<string, bool> match, bool includeModules, int? onlyPid, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<OpenFile>();
        int handles = 0, denied = 0;
        var processes = new HashSet<int>();

        // A handle of our own to a known file tells us which object-type index means "File" on this boot.
        using var probe = File.OpenHandle(System.Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var entries = Query();
        int self = System.Environment.ProcessId;
        var probeValue = probe.DangerousGetHandle();
        int fileType = -1;
        foreach (var e in entries)
            if ((int)e.UniqueProcessId == self && e.HandleValue == probeValue) { fileType = e.ObjectTypeIndex; break; }

        using var resolver = new NameResolver();
        if (fileType >= 0)
        {
            foreach (var group in entries.Where(e => e.ObjectTypeIndex == fileType && (int)e.UniqueProcessId != self
                         && (onlyPid == null || (int)e.UniqueProcessId == onlyPid)).GroupBy(e => (int)e.UniqueProcessId))
            {
                ct.ThrowIfCancellationRequested();
                int pid = group.Key;
                processes.Add(pid);
                var proc = Native.OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                if (proc == IntPtr.Zero)
                {
                    denied++;
                    continue;
                }
                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in group)
                    {
                        if (!DuplicateHandle(proc, e.HandleValue, GetCurrentProcess(), out var dup, 0, false, DUPLICATE_SAME_ACCESS)) continue;
                        handles++;
                        var path = resolver.Resolve(dup, LookupTimeoutMs);
                        if (path == null || !seen.Add(path) || !match(path)) continue;
                        results.Add(new OpenFile(pid, Directory.Exists(path) ? OpenFileKind.Folder : OpenFileKind.File, path));
                    }
                }
                finally
                {
                    Native.CloseHandle(proc);
                }
            }
        }

        if (includeModules)
        {
            var pids = onlyPid is { } one ? new List<int> { one } : entries.Select(e => (int)e.UniqueProcessId).Distinct().ToList();
            foreach (var pid in pids)
            {
                ct.ThrowIfCancellationRequested();
                if (pid is 0 or 4 || pid == self) continue;
                var modules = ProcessInspector.Modules(pid, out _);
                if (modules == null) continue;
                processes.Add(pid);
                foreach (var m in modules.Skip(1)) // the first module is the program itself
                    if (match(m.Path)) results.Add(new OpenFile(pid, OpenFileKind.Module, m.Path));
            }
        }
        return new HandleScan(results, handles, processes.Count, denied, sw.Elapsed);
    }

    static List<HandleEntry> Query()
    {
        int size = 8 << 20;
        while (true)
        {
            var buffer = NativeMemory.Alloc((nuint)size);
            try
            {
                int status = Native.NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out var needed);
                if (status == Native.STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + (1 << 20));
                    continue;
                }
                if (status != 0) return new List<HandleEntry>();
                long count = (long)*(nuint*)buffer;
                var first = (HandleEntry*)((byte*)buffer + 2 * sizeof(nuint)); // NumberOfHandles, Reserved, then the entries
                var list = new List<HandleEntry>((int)count);
                for (long i = 0; i < count; i++) list.Add(first[i]);
                return list;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
    }

    /// <summary>
    /// Path lookups run on a helper thread with a timeout, because querying a synchronous pipe that has a read
    /// pending blocks until that read finishes (possibly forever). A stuck helper is abandoned and replaced.
    /// </summary>
    sealed class NameResolver : IDisposable
    {
        sealed class Worker
        {
            public readonly AutoResetEvent Go = new(false), Done = new(false);
            public IntPtr Handle;
            public string? Result;
            public volatile bool Quit;
        }

        Worker? _worker;

        public string? Resolve(IntPtr handle, int timeoutMs)
        {
            var w = _worker ??= Start();
            w.Handle = handle;
            w.Result = null;
            w.Go.Set();
            if (w.Done.WaitOne(timeoutMs))
            {
                Native.CloseHandle(handle);
                return w.Result;
            }
            // Leave the stuck thread (and its handle) behind; it exits on its own if the lookup ever returns.
            w.Quit = true;
            _worker = null;
            return null;
        }

        static Worker Start()
        {
            var w = new Worker();
            new Thread(() =>
            {
                while (w.Go.WaitOne() && !w.Quit)
                {
                    w.Result = PathOf(w.Handle);
                    if (w.Quit) return;
                    w.Done.Set();
                }
            }) { IsBackground = true, Name = "Tman handle names" }.Start();
            return w;
        }

        public void Dispose()
        {
            if (_worker == null) return;
            _worker.Quit = true;
            _worker.Go.Set();
        }
    }

    static string? PathOf(IntPtr h)
    {
        if (GetFileType(h) != FILE_TYPE_DISK) return null;
        var buf = stackalloc char[1024];
        uint n = GetFinalPathNameByHandleW(h, buf, 1024, 0);
        if (n == 0 || n >= 1024) return null;
        var s = new string(buf, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) return s[4..];
        return s;
    }
}
