using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Tman.Core;

/// <summary>
/// Keeps one Tman per signed-in user. Closing the window leaves Tman in the tray, so without this every launch from the
/// shortcut would add another full set of tray icons, and every copy would overwrite the others' settings. A second launch
/// brings the running copy forward instead. Dev and helper runs pass arguments, and those are left alone.
/// </summary>
public static class SingleInstance
{
    /// <summary>Followed by a process ID: start once that process has gone, for a copy replacing itself.</summary>
    public const string AfterFlag = "--after";

    const string MutexName = @"Local\Tman.SingleInstance";
    const int ReplaceWaitMs = 8000;

    static readonly IntPtr Broadcast = new(0xFFFF);
    static readonly uint ShowMessage = RegisterWindowMessageW("Tman.ShowWindow");
    static readonly uint QuitMessage = RegisterWindowMessageW("Tman.Quit");

    /// <summary>Held for the life of the process. The name existing is what says a copy is running.</summary>
    static Mutex? _claim;

    /// <summary>True if this process should carry on. False means another copy took over and this one should leave.</summary>
    public static bool Start(string[] args)
    {
        if (args.Length != 0 && args[0] != AfterFlag) return true;
        if (args.Length == 2 && int.TryParse(args[1], out var previous)) WaitFor(previous);
        if (Claim()) return true;

        if (OlderCopyIsRunning() && MessageBox.Show(
                "An older build of Tman is running. Close it and start this one?\n\nAnything it's in the middle of, such as an ISO build, will stop.",
                "Tman", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            PostMessageW(Broadcast, QuitMessage, IntPtr.Zero, IntPtr.Zero);
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ReplaceWaitMs)
            {
                Thread.Sleep(150);
                if (Claim()) return true;
            }
        }

        // The running copy needs this process's say-so to come to the front.
        AllowSetForegroundWindow(-1);
        PostMessageW(Broadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
        return false;
    }

    /// <summary>Lets the running copy answer those requests, including from a standard-user launch when it's elevated.</summary>
    public static void Listen(Window window, Action show, Action quit)
    {
        var handle = new WindowInteropHelper(window).Handle;
        const uint Allow = 1;
        ChangeWindowMessageFilterEx(handle, ShowMessage, Allow, IntPtr.Zero);
        ChangeWindowMessageFilterEx(handle, QuitMessage, Allow, IntPtr.Zero);
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if ((uint)message == ShowMessage)
            {
                show();
                handled = true;
            }
            else if ((uint)message == QuitMessage)
            {
                quit();
                handled = true;
            }
            return IntPtr.Zero;
        });
    }

    static bool Claim()
    {
        try
        {
            var mutex = new Mutex(false, MutexName, out bool created);
            if (created)
            {
                _claim = mutex;
                return true;
            }
            mutex.Dispose();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // It exists, and an elevated copy made it.
        }
    }

    static void WaitFor(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(ReplaceWaitMs);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// A copy left in the tray would otherwise hide every new build behind itself. Its file's date tells them apart, and a
    /// file that's since been replaced or deleted counts as older.
    /// </summary>
    static bool OlderCopyIsRunning()
    {
        if (Environment.ProcessPath is not { } mine) return false;
        var built = File.GetLastWriteTimeUtc(mine);
        var margin = TimeSpan.FromSeconds(2);
        foreach (var other in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
        {
            using (other)
            {
                if (other.Id != Environment.ProcessId && PathOf(other.Id) is { } path && File.GetLastWriteTimeUtc(path) < built - margin) return true;
            }
        }
        return false;
    }

    /// <summary>Works on an elevated process too, which is why this isn't Process.MainModule.</summary>
    static string? PathOf(int processId)
    {
        const uint QueryLimited = 0x1000;
        var handle = OpenProcess(QueryLimited, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref size) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int size);
}
