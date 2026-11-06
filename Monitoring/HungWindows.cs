using System.Runtime.InteropServices;

namespace Tman.Monitoring;

/// <summary>Finds apps Windows considers not responding: the same test that puts "(Not Responding)" in a title bar.</summary>
internal static unsafe class HungWindows
{
    [DllImport("user32.dll")]
    static extern bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr context);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool IsHungAppWindow(IntPtr window);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

    /// <summary>Set while the Processes page is on screen; nothing else needs this every sample.</summary>
    public static volatile bool Watching;

    [ThreadStatic] static HashSet<int>? _found;

    /// <summary>Process ids with a hung top-level window. Takes a millisecond or two.</summary>
    public static HashSet<int> Scan()
    {
        var found = _found = new HashSet<int>();
        EnumWindows(&Visit, IntPtr.Zero);
        _found = null;
        return found;
    }

    [UnmanagedCallersOnly]
    static int Visit(IntPtr window, IntPtr context)
    {
        if (IsWindowVisible(window) && IsHungAppWindow(window))
        {
            GetWindowThreadProcessId(window, out var pid);
            _found?.Add(pid);
        }
        return 1;
    }
}
