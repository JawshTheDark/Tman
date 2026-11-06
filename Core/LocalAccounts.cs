using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;

namespace Tman.Core;

public sealed record LocalAccount(string Name, string FullName, string Comment, bool Enabled, bool IsAdmin, bool LockedOut, DateTime? LastSignIn,
    bool PasswordRequired, bool IsCurrent, IReadOnlyList<string> Groups)
{
    public string EnabledText => LockedOut ? "Locked out" : Enabled ? "Yes" : "No";
    public string AdminText => IsAdmin ? "Yes" : "No";
    public string LastSignInText => LastSignIn?.ToString("g") ?? "Never";
    public string PasswordText => PasswordRequired ? "Yes" : "No";
    public string GroupsText => string.Join(", ", Groups);
    public string DisplayName => IsCurrent ? Name + " (you)" : Name;
    public Brush EnabledBrush => (Brush)Application.Current.FindResource(LockedOut ? "WarnBrush" : Enabled ? "GoodBrush" : "SubtleBrush");
}

public sealed record LocalGroupMember(string Name, string Kind, string Sid);

public sealed record LocalGroup(string Name, string Comment, IReadOnlyList<LocalGroupMember> Members)
{
    public int MemberCount => Members.Count;
}

public sealed record LocalAccountData(List<LocalAccount> Accounts, List<LocalGroup> Groups, string AdminGroup, string? AccountError, string? GroupError);

/// <summary>
/// Local users and groups through the NetApi functions. Passwords are never read or written: the API's password field stays
/// untouched, and the only account change made here is the disabled flag and Administrators membership.
/// </summary>
public static class LocalAccounts
{
    const int MaxPreferredLength = -1;
    const int ErrorMoreData = 234;
    const int ErrorAccessDenied = 5;
    const int FilterNormalAccount = 2;
    const uint UfAccountDisable = 0x2, UfLockout = 0x10, UfPasswordNotRequired = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct USER_INFO_2
    {
        public string usri2_name;
        public IntPtr usri2_password;
        public uint usri2_password_age;
        public uint usri2_priv;
        public IntPtr usri2_home_dir;
        public string? usri2_comment;
        public uint usri2_flags;
        public IntPtr usri2_script_path;
        public uint usri2_auth_flags;
        public string? usri2_full_name;
        public IntPtr usri2_usr_comment;
        public IntPtr usri2_parms;
        public IntPtr usri2_workstations;
        public uint usri2_last_logon;
        public uint usri2_last_logoff;
        public uint usri2_acct_expires;
        public uint usri2_max_storage;
        public uint usri2_units_per_week;
        public IntPtr usri2_logon_hours;
        public uint usri2_bad_pw_count;
        public uint usri2_num_logons;
        public IntPtr usri2_logon_server;
        public uint usri2_country_code;
        public uint usri2_code_page;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct USER_INFO_20
    {
        public string usri20_name;
        public IntPtr usri20_full_name;
        public IntPtr usri20_comment;
        public uint usri20_flags;
        public uint usri20_user_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct USER_INFO_1008
    {
        public uint usri1008_flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct LOCALGROUP_INFO_1
    {
        public string lgrpi1_name;
        public string? lgrpi1_comment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct LOCALGROUP_MEMBERS_INFO_2
    {
        public IntPtr lgrmi2_sid;
        public int lgrmi2_sidusage;
        public string lgrmi2_domainandname;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserEnum(string? server, int level, int filter, out IntPtr buffer, int maxLength, out int read, out int total, ref uint resume);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserSetInfo(string? server, string user, int level, ref USER_INFO_1008 info, out int badParam);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetLocalGroupEnum(string? server, int level, out IntPtr buffer, int maxLength, out int read, out int total, ref IntPtr resume);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int maxLength, out int read, out int total, ref IntPtr resume);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetLocalGroupAddMembers(string? server, string group, int level, ref IntPtr members, int count);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetLocalGroupDelMembers(string? server, string group, int level, ref IntPtr members, int count);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>Reads users and groups. Each side reports its own failure, so one can still show if the other is refused.</summary>
    public static LocalAccountData Read()
    {
        string machine = Environment.MachineName, admins = AdministratorsGroupName();
        var groups = new List<LocalGroup>();
        string? groupError = null;
        try { groups = ReadGroups(); }
        catch (NetApiException ex) { groupError = ex.Message; }

        var memberOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
            foreach (var m in g.Members)
            {
                var key = m.Sid.Length > 0 ? m.Sid : m.Name;
                if (!memberOf.TryGetValue(key, out var list)) memberOf[key] = list = new List<string>();
                list.Add(g.Name);
            }

        string current = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var accounts = new List<LocalAccount>();
        string? accountError = null;
        try
        {
            foreach (var u in ReadUsers())
            {
                var full = $@"{machine}\{u.usri2_name}";
                var sid = SidOf(full);
                var mine = memberOf.TryGetValue(sid.Length > 0 ? sid : full, out var list) ? list.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
                accounts.Add(new LocalAccount(u.usri2_name, u.usri2_full_name ?? "", u.usri2_comment ?? "", (u.usri2_flags & UfAccountDisable) == 0,
                    mine.Contains(admins, StringComparer.OrdinalIgnoreCase), (u.usri2_flags & UfLockout) != 0,
                    u.usri2_last_logon == 0 ? null : DateTime.UnixEpoch.AddSeconds(u.usri2_last_logon).ToLocalTime(),
                    (u.usri2_flags & UfPasswordNotRequired) == 0, sid.Length > 0 && sid == current, mine));
            }
        }
        catch (NetApiException ex)
        {
            accountError = ex.Message;
        }
        return new LocalAccountData(accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(), admins, accountError, groupError);
    }

    /// <summary>Group lists can name an account differently from the user list (MicrosoftAccount\me@example.com), so accounts are matched by SID.</summary>
    static string SidOf(string account)
    {
        try { return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value; }
        catch { return ""; }
    }

    static string SidText(IntPtr sid)
    {
        try { return new SecurityIdentifier(sid).Value; }
        catch { return ""; }
    }

    /// <summary>The group's real name: Windows translates it, so it's looked up by its well-known ID.</summary>
    static string AdministratorsGroupName()
    {
        try
        {
            var account = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount)).Value;
            return account[(account.IndexOf('\\') + 1)..];
        }
        catch
        {
            return "Administrators";
        }
    }

    static List<USER_INFO_2> ReadUsers()
    {
        var list = new List<USER_INFO_2>();
        uint resume = 0;
        int rc;
        do
        {
            rc = NetUserEnum(null, 2, FilterNormalAccount, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (rc != 0 && rc != ErrorMoreData) throw new NetApiException(rc);
                for (int i = 0; i < read; i++)
                    list.Add(Marshal.PtrToStructure<USER_INFO_2>(buffer + i * Marshal.SizeOf<USER_INFO_2>()));
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        } while (rc == ErrorMoreData);
        return list;
    }

    static List<LocalGroup> ReadGroups()
    {
        var list = new List<LocalGroup>();
        var resume = IntPtr.Zero;
        int rc;
        do
        {
            rc = NetLocalGroupEnum(null, 1, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (rc != 0 && rc != ErrorMoreData) throw new NetApiException(rc);
                for (int i = 0; i < read; i++)
                {
                    var g = Marshal.PtrToStructure<LOCALGROUP_INFO_1>(buffer + i * Marshal.SizeOf<LOCALGROUP_INFO_1>());
                    list.Add(new LocalGroup(g.lgrpi1_name, g.lgrpi1_comment ?? "", ReadMembers(g.lgrpi1_name)));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        } while (rc == ErrorMoreData);
        return list;
    }

    static List<LocalGroupMember> ReadMembers(string group)
    {
        var list = new List<LocalGroupMember>();
        var resume = IntPtr.Zero;
        int rc;
        do
        {
            rc = NetLocalGroupGetMembers(null, group, 2, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (rc != 0 && rc != ErrorMoreData)
                {
                    // Who's an administrator decides what's safe to change, so a failure there can't pass as an empty group.
                    if (group.Equals(AdministratorsGroupName(), StringComparison.OrdinalIgnoreCase)) throw new NetApiException(rc);
                    break;
                }
                for (int i = 0; i < read; i++)
                {
                    var m = Marshal.PtrToStructure<LOCALGROUP_MEMBERS_INFO_2>(buffer + i * Marshal.SizeOf<LOCALGROUP_MEMBERS_INFO_2>());
                    list.Add(new LocalGroupMember(m.lgrmi2_domainandname ?? "", KindName(m.lgrmi2_sidusage), SidText(m.lgrmi2_sid)));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        } while (rc == ErrorMoreData);
        return list;
    }

    static string KindName(int sidUsage) => sidUsage switch
    {
        1 => "User",
        2 or 4 or 5 => "Group",
        6 => "Deleted account",
        _ => "Unknown",
    };

    /// <summary>
    /// A sentence if disabling or demoting this account would leave no enabled administrator, or if that can't be ruled out; otherwise null.
    /// Reads the accounts again, in case something changed since the page loaded them.
    /// </summary>
    public static string? LastAdminProblem(string name)
    {
        var fresh = Read();
        if (fresh.AccountError != null || fresh.GroupError != null) return "Tman couldn't confirm another enabled administrator exists, so it made no change.";
        var admins = fresh.Accounts.Where(a => a.IsAdmin && a.Enabled).ToList();
        if (admins.Count == 0) return "Tman couldn't find an enabled administrator to check against, so it made no change.";
        return admins.Count == 1 && admins[0].Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? $"\"{name}\" is the last enabled administrator. Without one, nobody could manage this PC. Make another account an administrator first."
            : null;
    }

    /// <summary>Turns an account on or off, leaving its other flags alone. Returns null, or a sentence saying why it failed.</summary>
    public static string? SetEnabled(string name, bool enabled)
    {
        int rc = ReadFlags(name, out var flags);
        if (rc != 0) return Explain(rc);
        var info = new USER_INFO_1008 { usri1008_flags = enabled ? flags & ~UfAccountDisable : flags | UfAccountDisable };
        rc = NetUserSetInfo(null, name, 1008, ref info, out _);
        return rc == 0 ? null : Explain(rc);
    }

    static int ReadFlags(string name, out uint flags)
    {
        flags = 0;
        int rc = NetUserGetInfo(null, name, 20, out var buffer);
        if (rc != 0) return rc;
        try
        {
            flags = Marshal.PtrToStructure<USER_INFO_20>(buffer).usri20_flags;
            return 0;
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    /// <summary>Adds or removes a local account in a local group. Returns null, or a sentence saying why it failed.</summary>
    public static string? SetGroupMember(string group, string name, bool member)
    {
        var entry = Marshal.StringToHGlobalUni($@"{Environment.MachineName}\{name}");
        try
        {
            // Level 3 passes the "MACHINE\name" form.
            int rc = member ? NetLocalGroupAddMembers(null, group, 3, ref entry, 1) : NetLocalGroupDelMembers(null, group, 3, ref entry, 1);
            return rc == 0 ? null : Explain(rc);
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
        }
    }

    static string Explain(int code) => code switch
    {
        ErrorAccessDenied => "This needs administrator rights.",
        1377 => "The account isn't in that group.",
        1378 => "The account is already in that group.",
        2220 => "Windows couldn't find that group.",
        2221 => "Windows couldn't find that account.",
        _ => $"{Marshal.GetPInvokeErrorMessage(code).TrimEnd('.', ' ')} (error {code}).",
    };

    sealed class NetApiException(int code) : Exception(Explain(code));
}
