using System.Windows;
using System.Windows.Threading;
using Tman.Core;
using Tman.Monitoring;

namespace Tman;

public partial class App : Application
{
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
        if (!Tman.MainWindow.SnapshotMode)
        {
            Settings.Current.Save();
            AppUsage.Instance.Save();
        }
        base.OnExit(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A task manager should never die because one row or action failed.
        e.Handled = true;
        Ui.Error(e.Exception.Message, "Tman - unexpected error");
    }
}
