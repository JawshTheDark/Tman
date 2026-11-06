namespace Tman.Core;

public enum DockerStepKind { Pull, Stop, Rename, Run, Create, Connect, Start, RemoveOld, Change }

public sealed record DockerStep(DockerStepKind Kind, string[] Args)
{
    public string Text => "docker " + string.Join(" ", Args.Select(DockerTemplates.Quote));
}

/// <summary>
/// Replacing a container with a changed copy of itself, because Docker can't change most settings on one that exists. The old
/// one is set aside rather than removed until the new one is running, so a bad setting puts things back as they were.
/// </summary>
public sealed class RecreatePlan
{
    /// <summary>How long a new container gets to show it isn't falling over straight away.</summary>
    static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    public required DockerDetails Container { get; init; }
    /// <summary>The settings as they stand, to tell what changed.</summary>
    public required ContainerPlan Before { get; init; }
    public required ContainerPlan After { get; init; }
    /// <summary>Settings Docker reported that a new container can't be given.</summary>
    public List<string> NotCarried { get; init; } = new();
    /// <summary>Files changed inside the container itself, or null if Docker wouldn't say.</summary>
    public List<string>? ChangedFiles { get; init; }
    /// <summary>Other containers that rely on this one.</summary>
    public List<string> Dependents { get; init; } = new();
    public IReadOnlyCollection<string> TakenNames { get; init; } = Array.Empty<string>();
    public HashSet<int> BusyTcp { get; init; } = new();
    public HashSet<int> BusyUdp { get; init; } = new();
    /// <summary>Same settings on a newer image, which only happens if a pull actually brings one.</summary>
    public bool IsUpdate { get; init; }
    public bool StartAfter { get; set; }

    public string OldName => Container.Name;
    string NewName => After.Name.Trim();
    bool Renamed => NewName != OldName;
    bool RestartChanged => After.Restart != Before.Restart;
    bool SameCore => After.Fingerprint() == Before.Fingerprint();

    /// <summary>Nothing would happen.</summary>
    public bool Unchanged => !IsUpdate && SameCore && !Renamed && !RestartChanged;

    /// <summary>A name and a restart policy are the two things Docker can change in place, so nothing needs replacing.</summary>
    public bool Live => !IsUpdate && SameCore && (Renamed || RestartChanged);

    /// <summary>The name the old container goes by until the new one has proved itself. Nothing else can be using it.</summary>
    public string SetAside
    {
        get
        {
            var name = $"{OldName}-old";
            for (int i = 2; TakenNames.Contains(name) || name == NewName; i++) name = $"{OldName}-old{i}";
            return name;
        }
    }

    public List<DockerStep> Steps()
    {
        var steps = new List<DockerStep>();
        if (Live)
        {
            if (RestartChanged) steps.Add(new(DockerStepKind.Change, new[] { "update", "--restart", After.Restart, OldName }));
            if (Renamed) steps.Add(new(DockerStepKind.Change, new[] { "rename", OldName, NewName }));
            return steps;
        }
        if (IsUpdate || After.Image.Trim() != Before.Image) steps.Add(new(DockerStepKind.Pull, new[] { "pull", After.Image.Trim() }));
        // One that removes itself when stopped can't be set aside, because stopping it is the end of it.
        if (Container.AutoRemove) steps.Add(new(DockerStepKind.Stop, new[] { "rm", "--force", OldName }));
        else if (Container.Running) steps.Add(new(DockerStepKind.Stop, new[] { "stop", OldName }));
        if (!Container.AutoRemove) steps.Add(new(DockerStepKind.Rename, new[] { "rename", OldName, SetAside }));
        // Run is the same as the container's first start. Create and start set up its input differently, so they're only for when they're needed.
        if (StartAfter && After.AlsoOn.Count == 0)
        {
            steps.Add(new(DockerStepKind.Run, After.RunArgs().ToArray()));
        }
        else
        {
            steps.Add(new(DockerStepKind.Create, After.CreateArgs().ToArray()));
            foreach (var network in After.AlsoOn) steps.Add(new(DockerStepKind.Connect, network.ConnectArgs(NewName)));
            if (StartAfter) steps.Add(new(DockerStepKind.Start, new[] { "start", NewName }));
        }
        if (!Container.AutoRemove) steps.Add(new(DockerStepKind.RemoveOld, new[] { "rm", SetAside }));
        return steps;
    }

    public string Preview() => string.Join(Environment.NewLine, Steps().Select(s => s.Text));

    public List<string> Problems()
    {
        var problems = After.Problems();
        if (Renamed && TakenNames.Contains(NewName)) problems.Add($"There's already a container called {NewName}.");
        if (Unchanged) problems.Add("You haven't changed anything yet.");
        return problems;
    }

    public List<string> Warnings()
    {
        var warnings = After.Warnings();
        if (Live) return warnings;
        if (NotCarried.Count > 0) warnings.Add($"Tman can't copy {string.Join(", ", NotCarried)} to a new container, so the new one won't have it.");
        if (Dependents.Count > 0) warnings.Add($"{string.Join(", ", Dependents)} {(Dependents.Count == 1 ? "relies on this container, and needs" : "rely on this container, and need")} restarting once it's been replaced.");
        foreach (var gone in Before.Volumes.Where(v => !After.Volumes.Any(a => a.Source.Trim() == v.Source)))
            warnings.Add($"{(gone.Unnamed ? $"The unnamed volume at {gone.Target}" : gone.Source)} won't be attached any more. Its data stays where it is, but the new container won't see it.");
        if (Container.AutoRemove) warnings.Add("This container removes itself when it stops, so Tman can't put it back if the new one fails.");
        foreach (var port in After.Ports.Where(p => !p.IsBlank && p.Host.Trim().Length > 0))
        {
            if (Before.Ports.Any(b => b.Host == port.Host.Trim() && b.Protocol == port.Protocol)) continue;
            var busy = port.Protocol == "udp" ? BusyUdp : BusyTcp;
            if (int.TryParse(port.Host.Trim(), out var number) && busy.Contains(number)) warnings.Add($"Something on this PC is already using port {number}.");
        }
        return warnings;
    }

    /// <summary>The sentence or two the form leads with, saying what's about to happen.</summary>
    public string Explanation => Live
        ? "These changes don't need a new container, so nothing is lost."
        : "Docker can't change these settings on an existing container, so changing them replaces it. " + Consequences();

    string Consequences()
    {
        var lines = new List<string>
        {
            $"Tman stops {OldName}, makes a new container from these settings, and removes the old one once the new one is running. If it doesn't start, the old one is put back.",
        };
        if (Kept() is { Length: > 0 } kept) lines.Add("Kept: " + kept + ".");
        lines.Add("Lost: " + Lost() + ".");
        return string.Join(" ", lines);
    }

    /// <summary>What stays, by name, for the form and the confirmation.</summary>
    public string Kept()
    {
        var kept = After.Volumes.Where(v => !v.IsBlank).Select(v =>
            v.Unnamed ? $"the unnamed volume at {v.Target}" : v.IsFolder ? $"the folder {v.Source}" : $"the volume {v.Source}").ToList();
        return string.Join(", ", kept);
    }

    /// <summary>What goes, naming real files where Docker can tell.</summary>
    public string Lost()
    {
        var lost = new List<string>();
        // Docker lists the folder a volume is mounted on as a change, but that's the part that stays.
        var files = ChangedFiles?.Where(f => !Before.Volumes.Any(v => f == v.Target || f.StartsWith(v.Target + "/"))).ToList();
        if (files == null) lost.Add("anything written inside the container itself");
        else if (files.Count > 0) lost.Add(DescribeChanges(files));
        lost.Add("its logs");
        return string.Join(", and ", lost);
    }

    string DescribeChanges(List<string> files)
    {
        // A folder is listed whenever something in it changed, so only the deepest paths say anything.
        var leaves = files.Where(f => !files.Any(other => other.StartsWith(f + "/"))).ToList();
        var examples = leaves.OrderBy(f => f.StartsWith("/tmp") || f.StartsWith("/run") || f.StartsWith("/var/run") || f.StartsWith("/var/cache")).Take(3).ToList();
        var size = Container.ChangedBytes > 0 ? $" ({Fmt.Bytes(Container.ChangedBytes)})" : "";
        var more = leaves.Count > examples.Count ? $" and {leaves.Count - examples.Count:N0} more" : "";
        return $"{leaves.Count:N0} changed {(leaves.Count == 1 ? "file" : "files")} inside the container{size}: {string.Join(", ", examples)}{more}";
    }

    public string ConfirmText()
    {
        var what = IsUpdate ? $"Update {OldName} to the newest {After.Image.Trim()}?" : $"Replace {OldName} with a new container?";
        var also = NotCarried.Count > 0 ? $"\n\nThe new one won't have {string.Join(", ", NotCarried)}." : "";
        return $"{what}\n\n{Consequences()}{also}";
    }

    /// <summary>Runs the steps, and puts everything back if any of them fails.</summary>
    public async Task<JobResult> RunAsync(Action<string> log, CancellationToken cancel)
    {
        var steps = Steps();
        if (Live) return await RunLiveAsync(steps, log).ConfigureAwait(false);

        bool stopped = false, setAside = false, created = false;

        async Task<JobResult> PutBack(string why)
        {
            log("");
            log($"{why} Putting {OldName} back.");
            if (Container.AutoRemove) return new JobResult(JobLevel.Bad, $"{why} {OldName} removed itself when it stopped, so it can't be put back.");
            bool ok = true;
            if (created) await Step(log, new[] { "rm", "--force", NewName }, default).ConfigureAwait(false);
            if (setAside) ok = await Step(log, new[] { "rename", SetAside, OldName }, default).ConfigureAwait(false) == 0;
            if (ok && stopped) ok = await Step(log, new[] { "start", OldName }, default).ConfigureAwait(false) == 0;
            return ok
                ? new JobResult(JobLevel.Warn, $"{why} {OldName} is back as it was.")
                : new JobResult(JobLevel.Bad, $"{why} {OldName} couldn't be put back automatically. It's still there as {(setAside ? SetAside : OldName)}, so nothing is lost. The log above has the details.");
        }

        // A new container that dies at once hasn't really worked, even though Docker reported success.
        async Task<JobResult?> Settle()
        {
            await Task.Delay(SettleTime).ConfigureAwait(false);
            var fresh = (await DockerCli.InspectAsync(new[] { NewName }).ConfigureAwait(false)).FirstOrDefault();
            if (fresh != null && !fresh.Restarting && (fresh.Running || fresh.ExitCode == 0)) return null;
            await Step(log, new[] { "logs", "--tail", "20", NewName }, default).ConfigureAwait(false);
            return await PutBack("The new container stopped straight away.").ConfigureAwait(false);
        }

        foreach (var step in steps)
        {
            if (cancel.IsCancellationRequested)
                return stopped || setAside ? await PutBack("Stopped before it finished.").ConfigureAwait(false) : new JobResult(JobLevel.Warn, "Stopped before anything changed.");

            switch (step.Kind)
            {
                case DockerStepKind.Pull:
                    int pulled = await Step(log, step.Args, cancel).ConfigureAwait(false);
                    if (pulled == Cmd.Cancelled) return new JobResult(JobLevel.Warn, "Stopped before anything changed.");
                    var pulledId = await DockerCli.ImageIdAsync(After.Image.Trim()).ConfigureAwait(false);
                    // A failed pull is fine for a new image that's already here, and useless for an update.
                    if (pulled != 0 && (IsUpdate || pulledId == null))
                        return new JobResult(JobLevel.Bad, $"Couldn't get {After.Image.Trim()}, so {OldName} wasn't touched. The log above has the details.");
                    if (IsUpdate && pulledId == Container.ImageId)
                        return new JobResult(JobLevel.Good, $"{OldName} already has the newest {After.Image.Trim()}, so nothing needed changing.");
                    break;

                case DockerStepKind.Stop:
                    if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                        return new JobResult(JobLevel.Bad, $"{OldName} wouldn't stop, so it wasn't touched. The log above has the details.");
                    stopped = true;
                    break;

                case DockerStepKind.Rename:
                    if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                        return await PutBack($"Couldn't set {OldName} aside.").ConfigureAwait(false);
                    setAside = true;
                    break;

                case DockerStepKind.Run:
                    // It can exist even if this fails, as when its port is taken.
                    created = true;
                    if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                        return await PutBack("The new container wouldn't start.").ConfigureAwait(false);
                    if (await Settle().ConfigureAwait(false) is { } dead) return dead;
                    break;

                case DockerStepKind.Create:
                    if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                        return await PutBack("Docker wouldn't make the new container.").ConfigureAwait(false);
                    created = true;
                    break;

                case DockerStepKind.Connect:
                    if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                        return await PutBack("The new container couldn't join one of its networks.").ConfigureAwait(false);
                    break;

                case DockerStepKind.Start:
                    if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                        return await PutBack("The new container wouldn't start.").ConfigureAwait(false);
                    if (await Settle().ConfigureAwait(false) is { } failed) return failed;
                    break;

                case DockerStepKind.RemoveOld:
                    if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                        return new JobResult(JobLevel.Warn, $"{NewName} is ready, but the old one couldn't be removed. It's still there as {SetAside}.");
                    break;
            }
        }
        return new JobResult(JobLevel.Good, StartAfter ? $"{NewName} is running with the new settings." : $"{NewName} has the new settings. It's stopped, as it was before.");
    }

    async Task<JobResult> RunLiveAsync(List<DockerStep> steps, Action<string> log)
    {
        foreach (var step in steps)
        {
            if (await Step(log, step.Args, default).ConfigureAwait(false) != 0)
                return new JobResult(JobLevel.Bad, "Docker reported a problem. The log above has the details.");
        }
        return new JobResult(JobLevel.Good, $"{NewName} is changed. It wasn't restarted.");
    }

    static Task<int> Step(Action<string> log, string[] args, CancellationToken cancel)
    {
        log("> docker " + string.Join(" ", args.Select(DockerTemplates.Quote)));
        return DockerCli.StreamAsync(args, log, cancel);
    }
}

/// <summary>Changing a deployed Compose stack. Compose only replaces what changed, so this leans on it for the hard part.</summary>
public static class DockerStackChange
{
    /// <summary>
    /// Saves the edited files and brings the stack in line with them. The old files stay beside the new ones as .bak, and
    /// go back if the new ones are rejected or don't come up, so a typo can't take a working stack down.
    /// </summary>
    public static async Task<JobResult> ApplyAsync(StackPlan plan, Action<string> log, CancellationToken cancel)
    {
        var deployed = plan.Deployed!;
        var files = new[] { plan.ComposeFile, plan.EnvFile };
        try
        {
            foreach (var file in files.Where(File.Exists)) File.Copy(file, file + ".bak", overwrite: true);
            plan.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JobResult(JobLevel.Bad, $"Couldn't save the files: {ex.Message}");
        }

        if (await Step(log, deployed.Args("config", "--quiet"), cancel).ConfigureAwait(false) != 0)
        {
            Restore(files, log);
            return new JobResult(JobLevel.Bad, "Compose rejected those files, so nothing changed and the old files are back. The log above has the details.");
        }

        int code = await Step(log, plan.UpArgs(), cancel).ConfigureAwait(false);
        if (code == 0) return new JobResult(JobLevel.Good, $"{plan.Project} is up to date with its files.");

        log("");
        log("That didn't work, so Tman is going back to the old files.");
        Restore(files, log);
        bool back = await Step(log, plan.UpArgs(), default).ConfigureAwait(false) == 0;
        return back
            ? new JobResult(JobLevel.Warn, $"The change didn't work, so {plan.Project} is back on its old files.")
            : new JobResult(JobLevel.Bad, $"The change didn't work, and {plan.Project} wouldn't come back up on its old files. The log above has the details.");
    }

    static void Restore(IEnumerable<string> files, Action<string> log)
    {
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file + ".bak")) File.Copy(file + ".bak", file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Couldn't restore {file}: {ex.Message}");
            }
        }
    }

    /// <summary>Compose only replaces the services whose image actually changed.</summary>
    public static string[][] UpdateSteps(DockerStackFiles files) => new[] { files.Args("pull", "--ignore-buildable"), files.Args("up", "--detach") };

    public static string UpdatePreview(DockerStackFiles files) =>
        string.Join(Environment.NewLine, UpdateSteps(files).Select(s => "docker " + string.Join(" ", s.Select(DockerTemplates.Quote))));

    public static async Task<JobResult> UpdateAsync(DockerStackFiles files, Action<string> log, CancellationToken cancel)
    {
        foreach (var step in UpdateSteps(files))
        {
            int code = await Step(log, step, cancel).ConfigureAwait(false);
            if (code == Cmd.Cancelled) return new JobResult(JobLevel.Warn, "Stopped before it finished.");
            if (code != 0) return new JobResult(JobLevel.Bad, "Docker reported a problem. The log above has the details.");
        }
        return new JobResult(JobLevel.Good, $"{files.Project} is on the newest images.");
    }

    static Task<int> Step(Action<string> log, string[] args, CancellationToken cancel)
    {
        log("> docker " + string.Join(" ", args.Select(DockerTemplates.Quote)));
        return DockerCli.StreamAsync(args, log, cancel);
    }
}
