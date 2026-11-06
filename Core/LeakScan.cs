using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Tman.Monitoring;

namespace Tman.Core;

/// <param name="Conclusion">One plain line saying what the scan found.</param>
/// <param name="Text">The full entry as written to the log.</param>
public sealed record LeakScanResult(string Conclusion, string Text);

/// <summary>
/// Runs every leak check once and adds the results to one text file, so scans from different days or from Safe Mode can be
/// compared side by side. It starts a few harmless short processes for the exit test and changes nothing else. The log names
/// apps and drivers, but not the user, the PC or any file paths, so it's reasonable to share.
/// </summary>
public static class LeakScan
{
    const int TopRows = 15;

    public static string LogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "leak-scans.txt");

    /// <summary>Does the checks and saves the entry. Blocks for about 10 seconds. Throws if the log can't be written.</summary>
    public static LeakScanResult Run(Action<string> status)
    {
        // Right after Tman starts there may be no reading yet, and these are the sections a Safe Mode scan most needs.
        status("Waiting for the first reading…");
        var waited = Stopwatch.StartNew();
        while (SystemMonitor.Instance.Latest == null && waited.Elapsed.TotalSeconds < 30) Thread.Sleep(100);
        var snapshot = SystemMonitor.Instance.Latest;
        var text = new StringBuilder();
        void Line(string line = "") => text.AppendLine(line);
        void Heading(string title)
        {
            Line();
            Line(title);
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Line($"==== Tman leak scan, {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        Line($"Boot: {BootMode()}");
        Line($"Up {uptime.TotalHours:0.0} hours · Windows {Environment.OSVersion.Version} · Tman {version?.ToString(3)} · {(MainWindow.IsAdmin ? "administrator" : "standard user")}");

        status("Reading memory…");
        int conclusionAt = text.Length;
        if (snapshot != null)
        {
            var m = snapshot.Mem;
            double apps = snapshot.Processes.Sum(p => (double)p.Commit), pools = (double)m.Paged + m.NonPaged, commit = m.CommitTotal;
            Heading("MEMORY");
            Line($"  RAM              {Fmt.Bytes(m.Used)} of {Fmt.Bytes(m.Total)} in use");
            Line($"  Commit           {Fmt.Bytes(commit)} of {Fmt.Bytes(m.CommitLimit)}");
            Line($"  Apps             {Fmt.Bytes(apps)}");
            Line($"  Paged pool       {Fmt.Bytes(m.Paged)}");
            Line($"  Non-paged pool   {Fmt.Bytes(m.NonPaged)}");
            Line($"  Everything else  {Fmt.Bytes(Math.Max(0, commit - apps - pools))}");
        }

        else
        {
            Heading("MEMORY");
            Line("  No reading was available.");
        }

        status("Counting zombie processes…");
        var zombies = ZombieCheck.Read();
        Heading("ZOMBIE PROCESSES");
        if (zombies == null)
        {
            Line("  Windows wouldn't give the kernel's process count.");
        }
        else
        {
            Line($"  {zombies.Zombies:N0} zombies: {zombies.KernelProcesses:N0} process objects in the kernel, {zombies.Running:N0} running");
            Line($"  Apps hold {zombies.ProcessHandles:N0} process handles in total");
            if (uptime.TotalHours > 0.01) Line($"  About {zombies.Zombies / uptime.TotalHours:N0} an hour since Windows started");
            foreach (var h in zombies.Holders.Take(6)) Line($"    {h.ProcessHandles,7:N0}  {h.Name} (PID {h.Pid})");
        }

        status("Testing process exits…");
        var exits = ZombieCheck.TestExits();
        Heading("PROCESS EXIT TEST");
        Line(exits == null ? "  Couldn't read the kernel's count."
            : $"  Started {exits.Started} short processes, and about {exits.Stayed} stayed in memory after exiting: {(exits.Leaking ? "LEAKING" : "clean")}");

        status("Reading kernel pools…");
        Heading("LARGEST KERNEL POOL TAGS");
        var buffer = new byte[256 * 1024];
        var tags = MemoryMapReader.ReadAllPoolTags(ref buffer);
        if (tags == null) Line("  Windows wouldn't give the list.");
        else
        {
            foreach (var t in tags.OrderByDescending(t => t.Total).Take(TopRows))
                Line($"  {t.Tag,-5} {t.TotalText,10}  {t.Blocks,9:N0} blocks  {t.Meaning}");
        }

        if (snapshot != null)
        {
            Heading("LARGEST APPS");
            foreach (var app in snapshot.Processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(p => (double)p.Commit)).Take(TopRows))
                Line($"  {Fmt.Bytes(app.Sum(p => (double)p.Commit)),10}  {app.Sum(p => p.Handles),8:N0} handles  {app.Count(),3} × {app.Key}");
        }

        var report = LeakRecorder.Instance.Build(null);
        Heading($"RECORDED HISTORY ({report.WatchedHours:0.0} hours, {report.Samples:N0} samples)");
        var growing = report.Apps.Concat(report.Tags).Where(r => r.Verdict >= LeakVerdict.Suspect).ToList();
        if (growing.Count == 0) Line(report.WatchedHours < LeakAnalysis.MinHours ? "  Not enough history to judge yet." : "  Nothing is growing like a leak.");
        foreach (var r in growing.Take(TopRows))
            Line($"  {LeakAnalysis.Name(r.Verdict),-12} {r.Name}: {Fmt.Bytes(r.Memory.Last)} now, {LeakRecorder.Signed(r.Memory.PerHour)} an hour");

        status("Listing drivers…");
        Heading("RUNNING DRIVERS NOT FROM MICROSOFT");
        var drivers = ZombieCheck.ThirdPartyDrivers();
        if (drivers.Count == 0) Line("  None.");
        foreach (var d in drivers) Line($"  {d.File,-30} {d.Company}");
        Line();
        Line();

        var conclusion = Conclude(zombies, exits);
        text.Insert(conclusionAt, $"Result: {conclusion}{Environment.NewLine}");
        var entry = text.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.AppendAllText(LogPath, entry);
        return new LeakScanResult(conclusion, entry);
    }

    static string Conclude(ZombieState? zombies, ExitTestResult? exits)
    {
        if (exits?.Leaking == true) return $"exited processes are leaking, with about {exits.Stayed} of {exits.Started} staying in memory.";
        if (zombies is { Zombies: >= 1000 }) return $"new exits look clean, but {zombies.Zombies:N0} zombies are already stuck. A restart clears those.";
        if (exits == null) return "the exit test couldn't run.";
        return "no zombie process leak found.";
    }

    static string BootMode() => GetSystemMetrics(SmCleanBoot) switch
    {
        1 => "Safe Mode",
        2 => "Safe Mode with Networking",
        _ => "Normal",
    };

    const int SmCleanBoot = 67;

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);
}
