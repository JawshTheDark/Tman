using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Monitoring;

/// <summary>Collects one <see cref="Snapshot"/> per call. Lives on the sampler thread only.</summary>
internal sealed class Sampler
{
    readonly PdhQuery _pdh = new();
    readonly IntPtr _cpuTotal, _cpuPerf, _cpuCores;
    readonly IntPtr _cpuFreq, _cpuParked, _cpuLimit, _cpuLimitFlags, _energy;
    readonly IntPtr _gpuEngine, _gpuDedicated, _gpuShared;
    readonly IntPtr _diskIdle, _diskRead, _diskWrite, _diskResponse;
    readonly ProcessCollector _procs = new();
    readonly List<Dxgi.Adapter> _adapters;
    readonly GpuSensors? _gpuSensors;
    readonly Dictionary<string, (long Sent, long Recv)> _netPrev = new();
    readonly int _baseMhz;
    long _lastTimestamp;
    long _lastIdle, _lastKernel, _lastUser;

    public Sampler()
    {
        _cpuTotal = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
        bool utility = _cpuTotal != IntPtr.Zero;
        if (!utility) _cpuTotal = _pdh.Add(@"\Processor Information(_Total)\% Processor Time");
        _cpuCores = _pdh.Add(utility ? @"\Processor Information(*)\% Processor Utility" : @"\Processor Information(*)\% Processor Time");
        _cpuPerf = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _cpuFreq = _pdh.Add(@"\Processor Information(*)\Actual Frequency");
        _cpuParked = _pdh.Add(@"\Processor Information(*)\Parking Status");
        _cpuLimit = _pdh.Add(@"\Processor Information(_Total)\% Performance Limit");
        _cpuLimitFlags = _pdh.Add(@"\Processor Information(_Total)\Performance Limit Flags");
        _energy = _pdh.Add(@"\Energy Meter(*)\Power");

        _gpuEngine = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        _gpuDedicated = _pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _gpuShared = _pdh.Add(@"\GPU Adapter Memory(*)\Shared Usage");

        _diskIdle = _pdh.Add(@"\PhysicalDisk(*)\% Idle Time");
        _diskRead = _pdh.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = _pdh.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _diskResponse = _pdh.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");

        _adapters = Dxgi.Enumerate();
        try { _gpuSensors = new GpuSensors(_adapters); } catch { }
        _baseMhz = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("~MHz") as int? ?? 0;

        // Prime every delta-based source so the first real sample has something to compare against.
        _pdh.Collect();
        _procs.Collect(0);
        Native.GetSystemTimes(out _lastIdle, out _lastKernel, out _lastUser);
        SampleNetworks(0);
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public IReadOnlyList<Dxgi.Adapter> Adapters => _adapters;

    public Snapshot Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed100ns = (now - _lastTimestamp) * 1e7 / Stopwatch.Frequency;
        double seconds = elapsed100ns / 1e7;
        _lastTimestamp = now;
        _pdh.Collect();

        var snap = new Snapshot { Time = DateTime.Now, Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64) };

        // CPU
        double total = PdhQuery.Value(_cpuTotal) ?? FallbackCpu();
        var cores = PerCore(_pdh.Array(_cpuCores)).Select(v => Math.Clamp(v, 0, 100)).ToArray();
        double perf = PdhQuery.Value(_cpuPerf, cap: false) ?? 100;
        var freq = _pdh.Array(_cpuFreq, cap: false);
        var coreMhz = PerCore(freq);
        snap.Cpu = new CpuStat(Math.Clamp(total, 0, 100), cores, _baseMhz * perf / 100.0 / 1000.0)
        {
            CoreMhz = coreMhz,
            AvgMhz = freq.TryGetValue("_Total", out var avg) ? avg : coreMhz.DefaultIfEmpty().Average(),
            Parked = PerCore(_pdh.Array(_cpuParked, cap: false)).Select(v => v >= 0.5).ToArray(),
            PerfLimit = PdhQuery.Value(_cpuLimit, cap: false) ?? 100,
            LimitFlags = (uint)(PdhQuery.Value(_cpuLimitFlags, cap: false) ?? 0),
        };
        snap.Power = ReadPower(_pdh.Array(_energy, cap: false));

        // GPU engines: per-process and per-adapter usage
        var pidGpu = new Dictionary<int, double>();
        var engineSums = new Dictionary<(string Luid, string Engine), (string Type, double Value)>();
        foreach (var (instance, value) in _pdh.Array(_gpuEngine))
        {
            if (!ParseEngine(instance, out int pid, out var luid, out var engine, out var type)) continue;
            pidGpu[pid] = Math.Max(pidGpu.GetValueOrDefault(pid), value);
            var key = (luid, engine);
            var cur = engineSums.GetValueOrDefault(key, (type, 0));
            engineSums[key] = (type, cur.Item2 + value);
        }
        var dedicated = _pdh.Array(_gpuDedicated, cap: false);
        var shared = _pdh.Array(_gpuShared, cap: false);
        foreach (var a in _adapters)
        {
            var engines = new Dictionary<string, double>();
            double usage = 0;
            foreach (var (k, v) in engineSums)
            {
                if (k.Luid != a.Luid) continue;
                var pct = Math.Min(v.Value, 100);
                usage = Math.Max(usage, pct);
                engines[v.Type] = Math.Max(engines.GetValueOrDefault(v.Type), pct);
            }
            snap.Gpus.Add(new GpuStat(a.Luid, a.Name, usage, engines,
                SumForLuid(dedicated, a.Luid), a.Dedicated, SumForLuid(shared, a.Luid), a.Shared)
            {
                Sensors = _gpuSensors?.Read(a.Luid),
            });
        }

        // Processes
        var procs = _procs.Collect(elapsed100ns);
        var alive = new HashSet<(int, long)>();
        foreach (var p in procs)
        {
            p.Gpu = Math.Min(100, pidGpu.GetValueOrDefault(p.Pid));
            p.Details = ProcessInfo.Get(p.Pid, p.CreateTime, p.Name);
            alive.Add((p.Pid, p.CreateTime));
        }
        ProcessInfo.Prune(alive);
        snap.Processes = procs;
        snap.ProcessCount = procs.Count(p => p.Pid != 0);
        snap.ThreadCount = _procs.ThreadCount;
        snap.HandleCount = _procs.HandleCount;

        // Memory
        var ms = new Native.MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref ms);
        Native.GetPerformanceInfo(out var pi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
        ulong page = pi.PageSize;
        snap.Mem = new MemStat(ms.ullTotalPhys, ms.ullAvailPhys, pi.CommitTotal * page, pi.CommitLimit * page,
            pi.SystemCache * page, pi.KernelPaged * page, pi.KernelNonpaged * page);

        // Disks
        var idle = _pdh.Array(_diskIdle, cap: false);
        var read = _pdh.Array(_diskRead, cap: false);
        var write = _pdh.Array(_diskWrite, cap: false);
        var resp = _pdh.Array(_diskResponse, cap: false);
        foreach (var (instance, idlePct) in idle)
        {
            if (instance == "_Total") continue;
            var space = instance.IndexOf(' ');
            var num = space < 0 ? instance : instance[..space];
            if (!int.TryParse(num, out var index)) continue;
            var letters = space < 0 ? "" : instance[(space + 1)..].Trim();
            snap.Disks.Add(new DiskStat(index, letters, Math.Clamp(100 - idlePct, 0, 100),
                read.GetValueOrDefault(instance), write.GetValueOrDefault(instance), resp.GetValueOrDefault(instance) * 1000));
        }
        snap.Disks.Sort((x, y) => x.Index.CompareTo(y.Index));

        snap.Nets = SampleNetworks(seconds);
        return snap;
    }

    List<NetStat> SampleNetworks(double seconds)
    {
        var result = new List<NetStat>();
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var ni in all)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            long sent, recv;
            try
            {
                var st = ni.GetIPStatistics();
                sent = st.BytesSent;
                recv = st.BytesReceived;
            }
            catch { continue; }

            double sendBps = 0, recvBps = 0;
            if (seconds > 0 && _netPrev.TryGetValue(ni.Id, out var prev))
            {
                sendBps = Math.Max(0, (sent - prev.Sent) / seconds);
                recvBps = Math.Max(0, (recv - prev.Recv) / seconds);
            }
            _netPrev[ni.Id] = (sent, recv);

            string v4 = "", v6 = "";
            try
            {
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && v4 == "") v4 = ua.Address.ToString();
                    else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && v6 == "" && !ua.Address.IsIPv6LinkLocal) v6 = ua.Address.ToString();
                }
            }
            catch { }

            var type = ni.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                NetworkInterfaceType.Ppp => "PPP",
                NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
                _ => ni.NetworkInterfaceType.ToString(),
            };
            long speed = 0;
            try { speed = ni.Speed; } catch { }
            result.Add(new NetStat(ni.Id, ni.Name, ni.Description, type, sendBps, recvBps, speed, v4, v6));
        }
        // .NET also reports NDIS filter layers (e.g. "Ethernet-QoS Packet Scheduler-0000") as interfaces; they mirror the real adapter.
        result.RemoveAll(n => result.Any(o => o != n && n.Name.StartsWith(o.Name + "-", StringComparison.OrdinalIgnoreCase)));
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    double FallbackCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long di = idle - _lastIdle, dk = kernel - _lastKernel, du = user - _lastUser;
        _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        long busy = dk + du;
        return busy <= 0 ? 0 : (busy - di) * 100.0 / busy;
    }

    /// <summary>Per-logical-processor values in processor order, without the _Total rows.</summary>
    static double[] PerCore(Dictionary<string, double> values) =>
        values.Where(kv => !kv.Key.Contains('_'))
            .OrderBy(kv => CoreOrder(kv.Key))
            .Select(kv => kv.Value)
            .ToArray();

    /// <summary>
    /// Turns Energy Meter instances (milliwatts) into named channels. On RAPL systems these look like
    /// rapl_package0_pkg, rapl_package0_core3_core (AMD) or rapl_package0_pp0 / _dram / _psys (Intel).
    /// </summary>
    static PowerStat ReadPower(Dictionary<string, double> milliwatts)
    {
        var channels = new List<(int Order, int Index, PowerChannel Channel)>();
        double pkg = 0, cores = 0, pp0 = 0;
        bool hasPkg = false, hasCores = false, hasPp0 = false;
        double? psys = null;
        foreach (var (instance, mw) in milliwatts)
        {
            if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            double w = Math.Max(0, mw / 1000);
            var t = instance.ToLowerInvariant().Split('_');
            if (t.Length >= 3 && t[0] == "rapl" && t[1].StartsWith("package"))
            {
                int.TryParse(t[1]["package".Length..], out var socket);
                switch (t[2])
                {
                    case "pkg":
                        pkg += w; hasPkg = true;
                        channels.Add((0, socket, new PowerChannel(socket == 0 ? "Package" : $"Package {socket}", w)));
                        continue;
                    case var c when c.StartsWith("core") && int.TryParse(c[4..], out var core):
                        cores += w; hasCores = true;
                        channels.Add((5, core, new PowerChannel($"Core {core}", w)));
                        continue;
                    case "pp0":
                        pp0 += w; hasPp0 = true;
                        channels.Add((1, socket, new PowerChannel("Cores", w)));
                        continue;
                    case "pp1":
                        channels.Add((2, socket, new PowerChannel("Graphics", w)));
                        continue;
                    case "dram":
                        channels.Add((3, socket, new PowerChannel("Memory", w)));
                        continue;
                    case "psys":
                        psys = (psys ?? 0) + w;
                        channels.Add((4, socket, new PowerChannel("Platform", w)));
                        continue;
                }
            }
            channels.Add((6, 0, new PowerChannel(instance, w)));
        }
        var ordered = channels.OrderBy(c => c.Order).ThenBy(c => c.Index).Select(c => c.Channel).ToList();
        return new PowerStat(hasPkg ? pkg : psys, hasCores ? cores : hasPp0 ? pp0 : null, ordered);
    }

    static long CoreOrder(string instance)
    {
        // "group,index"
        var comma = instance.IndexOf(',');
        if (comma < 0) return long.TryParse(instance, out var n) ? n : long.MaxValue;
        long.TryParse(instance[..comma], out var g);
        long.TryParse(instance[(comma + 1)..], out var i);
        return g * 1024 + i;
    }

    static bool ParseEngine(string name, out int pid, out string luid, out string engine, out string type)
    {
        // pid_1234_luid_0x00000000_0x0000D1A5_phys_0_eng_0_engtype_3D
        pid = 0; luid = engine = type = "";
        var t = name.Split('_');
        if (t.Length < 10 || t[0] != "pid" || t[2] != "luid" || !int.TryParse(t[1], out pid)) return false;
        luid = (t[3] + "_" + t[4]).ToLowerInvariant();
        engine = t[6] + "_" + t[8];
        type = t.Length > 10 ? string.Join("_", t, 10, t.Length - 10) : "";
        if (type.Length == 0) type = "Engine " + t[8];
        return true;
    }

    static ulong SumForLuid(Dictionary<string, double> values, string luid)
    {
        double sum = 0;
        foreach (var (k, v) in values)
            if (k.Contains(luid, StringComparison.OrdinalIgnoreCase)) sum += v;
        return (ulong)Math.Max(0, sum);
    }
}
