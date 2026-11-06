using System.Text;
using System.Text.RegularExpressions;

namespace Tman.Core;

public enum JobLevel { Good, Warn, Bad }

/// <summary>What a finished job means, in plain words. <c>Restart</c> is null when the question doesn't arise, such as after a failure.</summary>
public sealed record JobResult(JobLevel Level, string Text, bool? Restart = null);

/// <summary>The job's <c>Run</c> takes the chosen drive (empty when the job has none), a sink for output lines, and a stop token.</summary>
public sealed record RepairJob(string Title, string Summary, string Takes, string Changes, bool CanStop,
    Func<string, Action<string>, CancellationToken, Task<JobResult>> Run)
{
    public bool NeedsDrive { get; init; }
}

/// <summary>One command of a chain. <c>FineIf</c> is text that turns a failure into a success, such as "already stopped".</summary>
public sealed record ChainStep(string Label, string Command, string? FineIf = null);

public sealed record ChainOutcome(ChainStep Step, bool Ok);

/// <summary>
/// The System repair jobs and how to read what they print. Windows' tools say what they found only in their output, so the
/// wording is matched against their English messages (taken from the tools' own resource files). DISM can be asked for English;
/// for sfc and chkdsk on another display language, the exit code decides and the result points at the output.
/// </summary>
public static class RepairJobs
{
    const string NotRunning = "HELPMSG 3521", AlreadyRunning = "HELPMSG 2182";

    public static readonly RepairJob[] All =
    {
        new("Scan system files", "Checks every protected Windows file and replaces any that are damaged.",
            "10 to 30 minutes", "Damaged Windows files are replaced from a copy kept on this PC. Your own files and apps aren't touched.",
            false, ScanFiles),
        new("Check Windows image", "Looks for damage in the store Windows repairs itself from. Changes nothing.",
            "A few seconds for the quick check, then 5 to 20 minutes for the full scan", "Nothing. This only reads.",
            true, CheckImage),
        new("Repair Windows image", "Replaces damaged parts of that store with good copies from Windows Update.",
            "10 to 30 minutes, sometimes longer", "Damaged parts of the Windows component store are replaced. It needs an internet connection.",
            false, RepairImage),
        new("Scan drive for errors", "Checks a drive's file system while Windows keeps running.",
            "A few minutes, longer on a big or slow drive", "Small problems can be fixed while it runs. Anything bigger is noted so it can be fixed offline.",
            true, ScanDrive) { NeedsDrive = true },
        new("Reset network", "Puts Windows' network components back to their defaults and clears the DNS cache.",
            "Under a minute, plus a restart", "Winsock and TCP/IP settings are reset. Your connection may drop, a fixed IP address may be lost, and VPN or firewall apps may need reinstalling. A restart is needed to finish.",
            false, ResetNetwork),
        new("Reset Windows Update", "Stops the update services, sets their folders aside so Windows rebuilds them, and starts the services again.",
            "About a minute", "SoftwareDistribution and catroot2 are renamed with today's date, never deleted. Update history in Settings may look empty, and the next update check takes longer.",
            false, ResetUpdate),
        new("Repair winget", "Registers App Installer again, which fixes a missing or broken winget command.",
            "Under a minute", "The App Installer package is registered again for your account. Apps installed with winget aren't touched.",
            false, RepairWinget),
    };

    /// <summary>Passes each line on and keeps the text, with line wrapping removed, for reading the result.</summary>
    internal sealed class Log(Action<string> onLine)
    {
        readonly StringBuilder _text = new();

        public void Add(string line)
        {
            lock (_text) _text.AppendLine(line);
            onLine(line);
        }

        public string Flat()
        {
            lock (_text) return Regex.Replace(_text.ToString(), @"\s+", " ");
        }
    }

    static async Task<JobResult> ScanFiles(string _, Action<string> onLine, CancellationToken cancel)
    {
        var log = new Log(onLine);
        int code = await Cmd.StreamAsync(Cmd.System("sfc.exe"), "/scannow", log.Add, cancel);
        return ReadSfc(code, log.Flat());
    }

    static async Task<JobResult> CheckImage(string _, Action<string> onLine, CancellationToken cancel)
    {
        var quick = new Log(onLine);
        int code = await Cmd.StreamAsync(Cmd.System("dism.exe"), "/Online /English /Cleanup-Image /CheckHealth", quick.Add, cancel);
        var result = ReadDismCheck(code, quick.Flat());
        if (code != 0 || result.Level == JobLevel.Bad) return result;
        var full = new Log(onLine);
        code = await Cmd.StreamAsync(Cmd.System("dism.exe"), "/Online /English /Cleanup-Image /ScanHealth", full.Add, cancel);
        return ReadDismCheck(code, full.Flat());
    }

    static async Task<JobResult> RepairImage(string _, Action<string> onLine, CancellationToken cancel)
    {
        var log = new Log(onLine);
        int code = await Cmd.StreamAsync(Cmd.System("dism.exe"), "/Online /English /Cleanup-Image /RestoreHealth", log.Add, cancel);
        return ReadDismRepair(code, log.Flat());
    }

    static async Task<JobResult> ScanDrive(string drive, Action<string> onLine, CancellationToken cancel)
    {
        var log = new Log(onLine);
        int code = await Cmd.StreamAsync(Cmd.System("chkdsk.exe"), $"{drive} /scan", log.Add, cancel);
        return ReadChkdsk(code, log.Flat(), drive);
    }

    static async Task<JobResult> ResetNetwork(string _, Action<string> onLine, CancellationToken cancel)
    {
        var netsh = Quote(Cmd.System("netsh.exe"));
        var steps = new ChainStep[]
        {
            new("Reset Winsock", $"{netsh} winsock reset"),
            new("Reset TCP/IP", $"{netsh} int ip reset"),
            new("Clear the DNS cache", $"{Quote(Cmd.System("ipconfig.exe"))} /flushdns"),
        };
        return ReadNetworkReset(await RunChain(steps, onLine, cancel), steps.Length);
    }

    static async Task<JobResult> ResetUpdate(string _, Action<string> onLine, CancellationToken cancel)
    {
        var net = Quote(Cmd.System("net.exe"));
        var suffix = ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var steps = new List<ChainStep>
        {
            new("Stop Windows Update", $"{net} stop wuauserv /y", NotRunning),
            new("Stop Background Intelligent Transfer Service", $"{net} stop bits /y", NotRunning),
            new("Stop Cryptographic Services", $"{net} stop cryptsvc /y", NotRunning),
        };
        var kept = new List<string>();
        foreach (var folder in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution"), Path.Combine(Environment.SystemDirectory, "catroot2") })
        {
            if (!Directory.Exists(folder)) continue;
            var name = Path.GetFileName(folder) + suffix;
            steps.Add(new ChainStep($"Rename {Path.GetFileName(folder)} to {name}", Builtin($"ren {Quote(folder)} {Quote(name)}")));
            kept.Add(name);
        }
        steps.Add(new ChainStep("Start Cryptographic Services", $"{net} start cryptsvc", AlreadyRunning));
        steps.Add(new ChainStep("Start Background Intelligent Transfer Service", $"{net} start bits", AlreadyRunning));
        steps.Add(new ChainStep("Start Windows Update", $"{net} start wuauserv", AlreadyRunning));
        return ReadUpdateReset(await RunChain(steps, onLine, cancel), steps.Count, kept);
    }

    static async Task<JobResult> RepairWinget(string _, Action<string> onLine, CancellationToken cancel)
    {
        var log = new Log(onLine);
        const string script = "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; " +
                              "Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe; winget --version";
        int code = await Cmd.StreamAsync(Cmd.System(@"WindowsPowerShell\v1.0\powershell.exe"), $"-NoLogo -NoProfile -NonInteractive -Command \"{script}\"", log.Add, cancel);
        return ReadWinget(code, log.Flat());
    }

    static string Quote(string s) => $"\"{s}\"";

    /// <summary>A step that needs a command built into cmd.exe, which <see cref="RunChain"/> must not run in the chain's own cmd.exe.</summary>
    public static string Builtin(string command) => $"{Quote(Cmd.System("cmd.exe"))} /d /c {command}";

    /// <summary>
    /// Runs commands one after another inside a single cmd.exe, so closing Tman partway can't strand the job with its
    /// services stopped: the chain carries on by itself. Each step reports in, and a failed step doesn't end the chain.
    /// cmd.exe gives up on the rest of a chain the moment its own output fails, as it would once Tman has gone, so the
    /// chain's cmd.exe must never print: its report lines and any built-in commands each run in a child cmd.exe.
    /// </summary>
    public static async Task<List<ChainOutcome>> RunChain(IReadOnlyList<ChainStep> steps, Action<string> onLine, CancellationToken cancel = default)
    {
        string Say(string what, int i) => Builtin($"echo #{what} {i}");
        var script = string.Join(" & ", steps.Select((s, i) => $"{Say("step", i)} & {s.Command} 2>&1 && {Say("ok", i)} || {Say("fail", i)}"));
        var outcomes = new List<ChainOutcome>();
        var text = new StringBuilder();
        await Cmd.StreamAsync(Cmd.System("cmd.exe"), $"/d /s /c \"{script}\"", line =>
        {
            lock (text)
            {
                var m = Regex.Match(line, @"^#(step|ok|fail) (\d+)\s*$");
                if (!m.Success)
                {
                    text.AppendLine(line);
                    onLine("    " + line);
                    return;
                }
                var step = steps[int.Parse(m.Groups[2].Value)];
                if (m.Groups[1].Value == "step")
                {
                    text.Clear();
                    onLine(step.Label);
                    return;
                }
                bool ok = m.Groups[1].Value == "ok" || (step.FineIf != null && text.ToString().Contains(step.FineIf, StringComparison.OrdinalIgnoreCase));
                outcomes.Add(new ChainOutcome(step, ok));
                onLine(ok ? "    Done" : "    Failed");
            }
        }, cancel);
        return outcomes;
    }

    static JobResult? Interrupted(int code) => code switch
    {
        Cmd.Cancelled => new(JobLevel.Warn, "Stopped before it finished, so there's no result."),
        Cmd.FailedToStart => new(JobLevel.Bad, "The tool couldn't be started. The reason is in the output."),
        _ => null,
    };

    static JobResult Unread(int code) => code == 0
        ? new(JobLevel.Good, "Finished with no error. The output shows what it found.")
        : new(JobLevel.Warn, $"Finished with code {code}. The output shows what happened.");

    static bool Has(this string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);

    public static JobResult ReadSfc(int code, string text)
    {
        if (Interrupted(code) is { } stopped) return stopped;
        if (text.Has("must be an administrator")) return new(JobLevel.Bad, "Windows refused because this needs administrator rights.");
        if (text.Has("repair pending")) return new(JobLevel.Warn, "An earlier repair is waiting for a restart. Restart Windows, then run this again.", true);
        if (text.Has("Another servicing or repair operation")) return new(JobLevel.Warn, "Another repair or update is running. Wait for it to finish, then run this again.");
        if (text.Has("cannot guarantee")) return new(JobLevel.Warn, "No damaged files were found, but the Windows image is damaged, so this result can't be trusted. Run Repair Windows image, then scan again.", false);
        if (text.Has("did not find any integrity violations")) return new(JobLevel.Good, "No problems found. Your Windows system files are intact.", false);
        if (text.Has("maximum number of files")) return new(JobLevel.Warn, "More damaged files were found than one scan can repair. Restart Windows and run this again.", true);
        if (text.Has("successfully repaired")) return new(JobLevel.Good, "Damaged system files were found and repaired.", true);
        if (text.Has("unable to fix")) return new(JobLevel.Bad, "Damaged system files were found, and some couldn't be repaired. Run Repair Windows image, restart, then scan again. Details are in C:\\Windows\\Logs\\CBS\\CBS.log.", false);
        if (text.Has("could not perform") || text.Has("could not start the repair service")) return new(JobLevel.Bad, "Windows couldn't run the scan. Restart and try again.");
        return Unread(code);
    }

    public static JobResult ReadDismCheck(int code, string text)
    {
        if (Interrupted(code) is { } stopped) return stopped;
        if (code != 0) return new(JobLevel.Bad, "The check didn't finish. " + DismReason(code, text));
        if (text.Has("cannot be repaired")) return new(JobLevel.Bad, "The Windows image is damaged beyond what Windows can repair. An in-place reinstall (Reset this PC, keeping your files) is the way out.", false);
        if (text.Has("is repairable")) return new(JobLevel.Warn, "The Windows image is damaged, and it can be repaired. Run Repair Windows image.", false);
        if (text.Has("No component store corruption")) return new(JobLevel.Good, "No damage found. The Windows image is healthy.", false);
        return Unread(code);
    }

    public static JobResult ReadDismRepair(int code, string text)
    {
        if (Interrupted(code) is { } stopped) return stopped;
        const string done = "The Windows image is healthy, and anything damaged was repaired. If the system file scan found problems, run it again.";
        return code switch
        {
            0 => new(JobLevel.Good, done, false),
            3010 => new(JobLevel.Good, done, true),
            _ => new(JobLevel.Bad, "The repair failed. " + DismReason(code, text)),
        };
    }

    /// <summary>Reads a DISM run that turned something on or off, where exit code 3010 means it worked but needs a restart.</summary>
    public static JobResult ReadDismChange(string done, string failed, int code, string text)
    {
        if (Interrupted(code) is { } stopped) return stopped;
        return code switch
        {
            0 => new(JobLevel.Good, done, false),
            3010 => new(JobLevel.Good, done, true),
            _ => new(JobLevel.Bad, $"{failed} {DismReason(code, text)}"),
        };
    }

    public static string DismReason(int code, string text)
    {
        uint hr = unchecked((uint)code);
        var id = code < 0 ? $"0x{hr:X8}" : code.ToString();
        if (hr is 5 or 740) return $"Windows says this needs administrator rights (error {id}).";
        if (hr is 0x800F081F or 0x800F0906 or 0x800F0907 or 0x800F0950 or 0x800F0954)
            return $"Windows couldn't get the files it needs. Check your internet connection and that Windows Update works, then try again (error {id}).";
        var said = Regex.Match(text, @"Error:\s*\S+\s+(.{1,240}?)\s*(The DISM log file|$)", RegexOptions.IgnoreCase);
        return said.Success ? $"{said.Groups[1].Value} (error {id})" : $"The output shows what went wrong (error {id}).";
    }

    public static JobResult ReadChkdsk(int code, string text, string drive)
    {
        if (Interrupted(code) is { } stopped) return stopped;
        var offline = $"To fix them, open Command Prompt as administrator and run chkdsk {drive} /spotfix. On the system drive that happens at the next restart.";
        if (text.Has("Access Denied")) return new(JobLevel.Bad, "Windows refused because this needs administrator rights.");
        if (text.Has("some were fixed online")) return new(JobLevel.Warn, $"Some problems on {drive} were fixed, and the rest can only be fixed offline. {offline}");
        if (text.Has("must be fixed offline") || text.Has("spotfix") || text.Has("queued for offline repair")) return new(JobLevel.Warn, $"{drive} has problems that can only be fixed offline. {offline}");
        if (text.Has("chkdsk /f") || text.Has("could not be corrected")) return new(JobLevel.Bad, $"{drive} has problems a scan can't fix. Open Command Prompt as administrator and run chkdsk {drive} /f.");
        if (text.Has("Snapshot creation failed") || text.Has("snapshot error")) return new(JobLevel.Warn, "Windows couldn't take the snapshot a scan works from. Try again later.");
        if (text.Has("all fixed online") || text.Has("made corrections")) return new(JobLevel.Good, $"Problems on {drive} were found and fixed. Nothing more is needed.", false);
        if (text.Has("found no problems")) return new(JobLevel.Good, $"No problems found on {drive}.", false);
        return code switch
        {
            0 => new(JobLevel.Good, $"No errors found on {drive}.", false),
            1 => new(JobLevel.Good, $"Errors on {drive} were found and fixed.", false),
            2 => new(JobLevel.Good, $"No errors found on {drive}, though there's routine cleanup left to do.", false),
            3 => new(JobLevel.Warn, $"{drive} couldn't be fully checked, or it has errors a scan can't fix. The output has the details."),
            _ => Unread(code),
        };
    }

    static List<string> Failed(IReadOnlyList<ChainOutcome> done, int planned)
    {
        var failed = done.Where(o => !o.Ok).Select(o => o.Step.Label).ToList();
        if (done.Count < planned) failed.Add($"{planned - done.Count} more step(s) didn't report back");
        return failed;
    }

    public static JobResult ReadNetworkReset(IReadOnlyList<ChainOutcome> done, int planned)
    {
        var failed = Failed(done, planned);
        return failed.Count == 0
            ? new(JobLevel.Good, "The network was reset. It may not work properly until you restart.", true)
            : new(JobLevel.Warn, $"Part of the reset failed: {string.Join(", ", failed)}. The steps above show why.", true);
    }

    public static JobResult ReadUpdateReset(IReadOnlyList<ChainOutcome> done, int planned, IReadOnlyList<string> kept)
    {
        var failed = Failed(done, planned);
        var renamed = kept.Where(name => done.Any(o => o.Ok && o.Step.Label.EndsWith($" to {name}"))).ToList();
        var folders = renamed.Count == 0 ? "" : $" The old folders are kept as {string.Join(" and ", renamed)}. You can delete them once updates work.";
        if (failed.Count == 0) return new(JobLevel.Good, "Windows Update was reset. Check for updates to rebuild its folders." + folders, false);
        bool serviceDown = done.Any(o => !o.Ok && o.Step.Label.StartsWith("Start ")) || done.Count < planned;
        return new(JobLevel.Warn, $"Not every step worked: {string.Join(", ", failed)}. The steps above show why." + folders, serviceDown);
    }

    public static JobResult ReadWinget(int code, string text)
    {
        if (Interrupted(code) is { } stopped) return stopped;
        if (code == 0)
        {
            var version = Regex.Match(text, @"v\d+(\.\d+)+");
            return new(JobLevel.Good, version.Success ? $"App Installer was registered again, and winget works ({version.Value})." : "App Installer was registered again, and winget works.", false);
        }
        if (text.Has("0x80073D02")) return new(JobLevel.Warn, "App Installer is in use. Close it and any open winget windows, then try again.");
        if (text.Has("0x80073CF1")) return new(JobLevel.Bad, "App Installer isn't on this PC, so there's nothing to register. Install App Installer from the Microsoft Store.");
        if (text.Has("not recognized")) return new(JobLevel.Warn, "App Installer was registered again, but the winget command still isn't there. Sign out and back in, then try it.");
        return new(JobLevel.Bad, "App Installer couldn't be registered again. The reason is in the output.");
    }
}
