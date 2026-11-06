using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Tman.Core;
using Tman.Monitoring;

namespace Tman;

public partial class App : Application
{
    /// <summary>Set once Tman is really closing, as opposed to hiding its window in the tray.</summary>
    public static bool Exiting { get; set; }

    /// <summary>Runs just before a forced exit, which skips the window's Closed event and <see cref="OnExit"/>.</summary>
    public static event Action? Terminating;

    public static void Quit()
    {
        Exiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        if (!SingleInstance.Start(e.Args))
        {
            // Nothing has been loaded, so there's nothing to save, and leaving now means no window ever appears.
            TerminateProcess(GetCurrentProcess(), 0);
            return;
        }
        int watchdog = Array.IndexOf(e.Args, IsoBuilder.WatchdogFlag);
        if (watchdog >= 0 && watchdog + 1 < e.Args.Length && int.TryParse(e.Args[watchdog + 1], out var owner))
        {
            RunIsoWatchdog(owner);
            return;
        }
        SystemMonitor.Instance.IntervalMs = Settings.Current.IntervalMs;
        // Recorders subscribe before any page so they've processed each sample by the time pages read them.
        AppUsage.Instance.Start();
        FlightRecorder.Instance.Start();
        SystemMonitor.Instance.Start(Dispatcher);
        StaticInfo.LoadAsync(Dispatcher);
        // A build that lost its power as well as its process leaves only the journal to go on.
        if (Tman.MainWindow.IsAdmin && IsoBuilder.Leftover() != null) _ = Task.Run(() => IsoBuilder.RecoverAsync(_ => { }));
    }

    /// <summary>The second process that sees a build through if this one dies. It never gets a window, and it runs off the UI thread so its awaits don't wait for this.</summary>
    static void RunIsoWatchdog(int owner)
    {
        try { Task.Run(() => IsoBuilder.WatchAsync(owner)).GetAwaiter().GetResult(); }
        catch (Exception ex) { ErrorLog.Write(ex); }
        // Straight out, so that no exit code here saves settings this process never loaded.
        TerminateProcess(GetCurrentProcess(), 0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveState();
        base.OnExit(e);
    }

    static void SaveState()
    {
        if (Tman.MainWindow.SnapshotMode) return;
        Settings.Current.Save();
        AppUsage.Instance.Save();
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A task manager should never die because one row or action failed.
        e.Handled = true;
        ErrorLog.Write(e.Exception);
        if (Exiting)
        {
            // Something failed on the way out, such as WPF's own shutdown step, which throws when .NET was updated
            // while Tman ran and a file it needs is gone. Handling it cancels the shutdown and would leave Tman
            // running with no window, so finish the exit here instead.
            try { Terminating?.Invoke(); } catch { }
            try { SaveState(); } catch { }
            // Not Environment.Exit: WPF's own exit handlers fail the same way and would crash the process.
            TerminateProcess(GetCurrentProcess(), 0);
        }
        Ui.Error(Describe(e.Exception), "Tman - unexpected error");
    }

    static string Describe(Exception ex)
    {
        if (ex is FileNotFoundException or FileLoadException && RuntimeReplaced())
            return ".NET was updated while Tman was running, and files from the old version are gone. Restart Tman to keep using it.";
        string text = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        return $"{text}\n\nDetails were saved to {ErrorLog.FilePath}";
    }

    /// <summary>True when the .NET runtime this process started on has since been removed from disk.</summary>
    static bool RuntimeReplaced()
    {
        try
        {
            string coreLib = typeof(object).Assembly.Location;
            return coreLib.Length > 0 && !File.Exists(coreLib);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
