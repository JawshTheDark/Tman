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
        SystemMonitor.Instance.IntervalMs = Settings.Current.IntervalMs;
        // Recorders subscribe before any page so they've processed each sample by the time pages read them.
        AppUsage.Instance.Start();
        FlightRecorder.Instance.Start();
        SystemMonitor.Instance.Start(Dispatcher);
        StaticInfo.LoadAsync(Dispatcher);
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
