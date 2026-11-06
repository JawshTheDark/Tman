using System.Runtime.InteropServices;

namespace Tman.Core;

/// <summary>Turns on a privilege the process's token already holds. Admin tokens carry these switched off.</summary>
internal static unsafe class TokenPrivileges
{
    public const string Debug = "SeDebugPrivilege", ProfileSingleProcess = "SeProfileSingleProcessPrivilege";
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;
    const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct SingleTokenPrivilege
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, SingleTokenPrivilege* state, uint length, void* previous, void* returnLength);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    /// <summary>False when this token doesn't hold the privilege, which is normal for a standard user.</summary>
    public static bool Enable(string name)
    {
        if (!Native.OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid)) return false;
            var state = new SingleTokenPrivilege { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            // Returns true even when nothing changed; the last error tells the two apart.
            return AdjustTokenPrivileges(token, false, &state, (uint)sizeof(SingleTokenPrivilege), null, null)
                && Marshal.GetLastPInvokeError() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }
}
