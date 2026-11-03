using Tman.Core;

namespace Tman.Monitoring;

/// <summary>
/// Enumerates every process in one NtQuerySystemInformation call. This works for protected and
/// other-user processes without opening handles, which is what lets CPU numbers cover everything.
/// </summary>
internal sealed unsafe class ProcessCollector
{
    const int SystemProcessInformation = 5;
    const uint StateWaiting = 5, WaitReasonSuspended = 5;

    byte[] _buffer = new byte[1 << 20];
    Dictionary<(int, long), (long Cpu, long Io)> _prev = new();
    Dictionary<(int, long), (long Cpu, long Io)> _next = new();

    public int ThreadCount { get; private set; }
    public int HandleCount { get; private set; }

    public List<ProcSample> Collect(double elapsed100ns)
    {
        int status;
        while (true)
        {
            int needed;
            fixed (byte* p = _buffer) status = Native.NtQuerySystemInformation(SystemProcessInformation, p, _buffer.Length, out needed);
            if (status != Native.STATUS_INFO_LENGTH_MISMATCH) break;
            _buffer = new byte[Math.Max(needed + 128 * 1024, _buffer.Length * 2)];
        }

        var list = new List<ProcSample>(512);
        if (status != 0) return list;

        int cpus = Environment.ProcessorCount;
        double seconds = elapsed100ns / 1e7;
        int threads = 0, handles = 0;
        _next.Clear();

        fixed (byte* basePtr = _buffer)
        {
            int offset = 0;
            while (true)
            {
                var spi = (Native.SYSTEM_PROCESS_INFORMATION*)(basePtr + offset);
                int pid = (int)spi->UniqueProcessId;
                string name = pid == 0
                    ? "System Idle Process"
                    : spi->ImageName.Buffer == IntPtr.Zero
                        ? "System"
                        : new string((char*)spi->ImageName.Buffer, 0, spi->ImageName.Length / 2);

                long cpu = spi->UserTime + spi->KernelTime;
                long io = spi->ReadTransferCount + spi->WriteTransferCount;
                var key = (pid, spi->CreateTime);
                double cpuPct = 0, ioBps = 0;
                if (elapsed100ns > 0 && _prev.TryGetValue(key, out var prev))
                {
                    cpuPct = Math.Clamp((cpu - prev.Cpu) / (elapsed100ns * cpus) * 100.0, 0, 100);
                    ioBps = Math.Max(0, (io - prev.Io) / seconds);
                }
                _next[key] = (cpu, io);

                bool suspended = pid != 0 && spi->NumberOfThreads > 0;
                var th = (Native.SYSTEM_THREAD_INFORMATION*)(spi + 1);
                for (int i = 0; i < spi->NumberOfThreads && suspended; i++)
                    if (th[i].ThreadState != StateWaiting || th[i].WaitReason != WaitReasonSuspended) suspended = false;

                list.Add(new ProcSample
                {
                    Pid = pid,
                    ParentPid = (int)spi->InheritedFromUniqueProcessId,
                    CreateTime = spi->CreateTime,
                    Name = name,
                    Threads = (int)spi->NumberOfThreads,
                    Handles = (int)spi->HandleCount,
                    SessionId = (int)spi->SessionId,
                    BasePriority = spi->BasePriority,
                    Cpu = cpuPct,
                    PrivateWs = (ulong)Math.Max(0, spi->WorkingSetPrivateSize),
                    WorkingSet = spi->WorkingSetSize,
                    Commit = spi->PagefileUsage,
                    IoBps = ioBps,
                    Suspended = suspended,
                    CpuTime = cpu,
                    IoBytes = io,
                });

                threads += (int)spi->NumberOfThreads;
                handles += (int)spi->HandleCount;
                if (spi->NextEntryOffset == 0) break;
                offset += (int)spi->NextEntryOffset;
            }
        }

        ThreadCount = threads;
        HandleCount = handles;
        (_prev, _next) = (_next, _prev);
        return list;
    }
}
