using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace Tman.Core;

public sealed record SmbShare(string Name, string Path, string Kind, string Description, bool IsAdmin);

public sealed record SmbSession(string Client, string User, int OpenFiles, TimeSpan Connected, TimeSpan Idle, bool Guest)
{
    public string ConnectedText => Fmt.Span(Connected);
    public string IdleText => Fmt.Span(Idle);
    public string GuestText => Guest ? "Yes" : "No";
}

public sealed record SmbOpenFile(string Path, string User, int Locks, string Access);

/// <summary>Shared folders, who's connected and what they have open. Everything here blocks, so call it off the UI thread.</summary>
public static class SmbShares
{
    const int MaxPreferredLength = -1;
    const int ErrorMoreData = 234;
    const int ErrorAccessDenied = 5;
    const uint StypeSpecial = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SESSION_INFO_502
    {
        public string? sesi502_cname;
        public string? sesi502_username;
        public int sesi502_num_opens;
        public uint sesi502_time;
        public uint sesi502_idle_time;
        public uint sesi502_user_flags;
        public IntPtr sesi502_cltype_name;
        public IntPtr sesi502_transport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FILE_INFO_3
    {
        public uint fi3_id;
        public uint fi3_permissions;
        public uint fi3_num_locks;
        public string? fi3_pathname;
        public string? fi3_username;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetSessionEnum(string? server, string? client, string? user, int level, out IntPtr buffer, int maxLength, out int read, out int total, ref uint resume);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetFileEnum(string? server, string? basePath, string? user, int level, out IntPtr buffer, int maxLength, out int read, out int total, ref IntPtr resume);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetShareDel(string? server, string share, int reserved);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetSessionDel(string? server, string client, string? user);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>True when the Server service, which does the sharing, is running.</summary>
    public static bool ServerRunning()
    {
        try
        {
            using var service = new ServiceController("LanmanServer");
            return service.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads shares through WMI, which also gives their folders to a standard user. A running Server service always has at least IPC$, so empty means the read failed.</summary>
    public static List<SmbShare> ReadShares() =>
        Wmi.Query("SELECT Name, Path, Type, Description FROM Win32_Share")
            .Select(o =>
            {
                uint type = (uint)o.U64("Type");
                bool admin = (type & StypeSpecial) != 0;
                return new SmbShare(o.Str("Name"), o.Str("Path"), admin ? "Admin share" : KindName(type & 0xFF), o.Str("Description"), admin);
            })
            .OrderBy(s => s.IsAdmin).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    static string KindName(uint type) => type switch
    {
        0 => "Folder",
        1 => "Printer",
        2 => "Device",
        3 => "Remote admin",
        _ => "Other",
    };

    /// <summary>Whether the firewall lets file sharing in on the network in use, which is what Windows' own sharing switch controls. Null if unknown.</summary>
    public static (bool On, string Network)? ReadNetworkSharing()
    {
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return null;
            policy = Activator.CreateInstance(type);
            dynamic fw = policy!;
            int profiles = fw.CurrentProfileTypes;
            bool on = fw.IsRuleGroupCurrentlyEnabled("@FirewallAPI.dll,-28502");
            var names = new List<string>();
            if ((profiles & 1) != 0) names.Add("domain");
            if ((profiles & 2) != 0) names.Add("private");
            if ((profiles & 4) != 0) names.Add("public");
            return (on, string.Join(" and ", names));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (policy != null) Marshal.FinalReleaseComObject(policy);
        }
    }

    /// <summary>Sessions connected to this PC, or a sentence saying why they can't be read.</summary>
    public static (List<SmbSession> Items, string? Error) ReadSessions()
    {
        var list = new List<SmbSession>();
        uint resume = 0;
        int rc;
        do
        {
            rc = NetSessionEnum(null, null, null, 502, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (rc != 0 && rc != ErrorMoreData) return (list, Explain(rc));
                for (int i = 0; i < read; i++)
                {
                    var s = Marshal.PtrToStructure<SESSION_INFO_502>(buffer + i * Marshal.SizeOf<SESSION_INFO_502>());
                    list.Add(new SmbSession(s.sesi502_cname ?? "", s.sesi502_username ?? "", s.sesi502_num_opens,
                        TimeSpan.FromSeconds(s.sesi502_time), TimeSpan.FromSeconds(s.sesi502_idle_time), (s.sesi502_user_flags & 1) != 0));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        } while (rc == ErrorMoreData);
        return (list, null);
    }

    /// <summary>Files other computers have open here, or a sentence saying why they can't be read.</summary>
    public static (List<SmbOpenFile> Items, string? Error) ReadOpenFiles()
    {
        var list = new List<SmbOpenFile>();
        var resume = IntPtr.Zero;
        int rc;
        do
        {
            rc = NetFileEnum(null, null, null, 3, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (rc != 0 && rc != ErrorMoreData) return (list, Explain(rc));
                for (int i = 0; i < read; i++)
                {
                    var f = Marshal.PtrToStructure<FILE_INFO_3>(buffer + i * Marshal.SizeOf<FILE_INFO_3>());
                    list.Add(new SmbOpenFile(f.fi3_pathname ?? "", f.fi3_username ?? "", (int)f.fi3_num_locks, AccessName(f.fi3_permissions)));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        } while (rc == ErrorMoreData);
        return (list, null);
    }

    static string AccessName(uint permissions)
    {
        const uint read = 0x1, write = 0x2;
        return (permissions & write) != 0 ? ((permissions & read) != 0 ? "Read and write" : "Write") : (permissions & read) != 0 ? "Read" : "Other";
    }

    /// <summary>Returns null, or a sentence saying why it failed.</summary>
    public static string? StopSharing(string share)
    {
        int rc = NetShareDel(null, share, 0);
        return rc == 0 ? null : Explain(rc);
    }

    /// <summary>Returns null, or a sentence saying why it failed.</summary>
    public static string? CloseSession(string client, string user)
    {
        int rc = NetSessionDel(null, client.StartsWith(@"\\", StringComparison.Ordinal) ? client : @"\\" + client, user.Length > 0 ? user : null);
        return rc == 0 ? null : Explain(rc);
    }

    static string Explain(int code) => code switch
    {
        ErrorAccessDenied => "This needs administrator rights.",
        2114 => "The Server service isn't running.",
        2310 => "That share no longer exists.",
        2312 => "That session has already ended.",
        _ => $"{Marshal.GetPInvokeErrorMessage(code).TrimEnd('.', ' ')} (error {code}).",
    };
}
