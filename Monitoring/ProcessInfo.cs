using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Monitoring;

/// <summary>Resolves and caches per-process details (path, description, icon, user, command line).</summary>
internal static unsafe class ProcessInfo
{
    const int SystemProcessIdInformation = 88;
    const int ProcessCommandLineInformation = 60;
    const uint TOKEN_QUERY = 0x0008;

    static readonly Dictionary<(int, long), ProcessDetails> Cache = new();
    static readonly ConcurrentDictionary<string, (string? Desc, string? Company, ImageSource? Icon)> Files = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string> SidNames = new();
    static Dictionary<string, string>? _devices;

    /// <summary>Called from the sampler thread only.</summary>
    public static ProcessDetails Get(int pid, long createTime, string name)
    {
        var key = (pid, createTime);
        if (Cache.TryGetValue(key, out var d)) return d;
        d = Resolve(pid, name);
        Cache[key] = d;
        return d;
    }

    public static void Prune(HashSet<(int, long)> alive)
    {
        if (Cache.Count < alive.Count + 64) return;
        foreach (var k in Cache.Keys.Where(k => !alive.Contains(k)).ToList()) Cache.Remove(k);
    }

    static ProcessDetails Resolve(int pid, string name)
    {
        if (pid == 0) return new ProcessDetails { Description = "Percentage of time the processor is idle", User = "SYSTEM", Icon = Icons.DefaultExe };
        if (pid == 4) return new ProcessDetails { Description = "NT Kernel & System", User = "SYSTEM", Icon = Icons.ForFile(Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe")) };

        string? path = null, user = null, cmd = null;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                path = ImagePath(h);
                user = TokenUser(h);
                cmd = CommandLine(h);
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
        path ??= NtImagePath(pid);

        if (path == null)
            return new ProcessDetails { User = user, CommandLine = cmd, Icon = Icons.DefaultExe };

        var meta = Files.GetOrAdd(path, p =>
        {
            string? desc = null, company = null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(p);
                desc = string.IsNullOrWhiteSpace(v.FileDescription) ? null : v.FileDescription.Trim();
                company = string.IsNullOrWhiteSpace(v.CompanyName) ? null : v.CompanyName.Trim();
            }
            catch { }
            return (desc, company, Icons.ForFile(p));
        });

        return new ProcessDetails
        {
            Path = path,
            Description = meta.Desc,
            Company = meta.Company,
            Icon = meta.Icon,
            User = user,
            CommandLine = cmd,
        };
    }

    static string? ImagePath(IntPtr h)
    {
        char* buf = stackalloc char[1024];
        int size = 1024;
        return Native.QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
    }

    static string? TokenUser(IntPtr h)
    {
        if (!Native.OpenProcessToken(h, TOKEN_QUERY, out var token)) return null;
        try
        {
            using var id = new WindowsIdentity(token);
            var sid = id.User?.Value;
            if (sid == null) return null;
            return SidNames.GetOrAdd(sid, _ =>
            {
                var n = id.Name;
                var slash = n.IndexOf('\\');
                return slash >= 0 ? n[(slash + 1)..] : n;
            });
        }
        catch
        {
            return null;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    static string? CommandLine(IntPtr h)
    {
        int size = 2048;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = new byte[size];
            fixed (byte* p = buf)
            {
                int st = Native.NtQueryInformationProcess(h, ProcessCommandLineInformation, p, size, out var needed);
                if (st == 0)
                {
                    var us = (Native.UNICODE_STRING*)p;
                    if (us->Buffer == IntPtr.Zero || us->Length == 0) return null;
                    return new string((char*)us->Buffer, 0, us->Length / 2);
                }
                if (st != Native.STATUS_INFO_LENGTH_MISMATCH && st != Native.STATUS_BUFFER_TOO_SMALL) return null;
                size = Math.Max(needed, size * 2);
            }
        }
        return null;
    }

    /// <summary>Gets the image path without opening the process, so it works for protected/elevated processes.</summary>
    static string? NtImagePath(int pid)
    {
        const int max = 1024;
        char* buf = stackalloc char[max];
        var info = new Native.SYSTEM_PROCESS_ID_INFORMATION
        {
            ProcessId = pid,
            ImageName = new Native.UNICODE_STRING { Length = 0, MaximumLength = max * 2, Buffer = (IntPtr)buf },
        };
        if (Native.NtQuerySystemInformation(SystemProcessIdInformation, &info, sizeof(Native.SYSTEM_PROCESS_ID_INFORMATION), out _) != 0)
            return null;
        if (info.ImageName.Length == 0) return null;
        return ToDosPath(new string(buf, 0, info.ImageName.Length / 2));
    }

    static string? ToDosPath(string nt)
    {
        if (nt.StartsWith(@"\??\")) return nt[4..];
        if (nt.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), nt[12..]);
        _devices ??= BuildDeviceMap();
        foreach (var (device, drive) in _devices)
            if (nt.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return drive + nt[device.Length..];
        return null;
    }

    static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        char* buf = stackalloc char[512];
        foreach (var root in Environment.GetLogicalDrives())
        {
            var drive = root.TrimEnd('\\');
            if (Native.QueryDosDeviceW(drive, buf, 512) > 0) map[new string(buf)] = drive;
        }
        return map;
    }
}
