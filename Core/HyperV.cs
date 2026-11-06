using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace Tman.Core;

public enum HyperVStatus { Ready, NoAccess, NotInEdition, TurnedOff, RestartNeeded, ServiceStopped, Unknown }

public enum VmPower { Unknown, Off, Running, Saved, Paused, Changing }

public enum VmAction { Start, ShutDown, ForceShutDown, TurnOff, Pause, Resume, Save }

/// <summary>A virtual machine. CPU and memory are null when it isn't running, or when Hyper-V didn't say.</summary>
public sealed record HyperVm(string Id, string Name, VmPower Power, string State, uint? Cpu, ulong? MemoryBytes, TimeSpan Uptime);

public sealed record HyperVReading(HyperVStatus Status, IReadOnlyList<HyperVm> Vms, string Detail = "");

public sealed record VmCheckpoint(string Path, string Name, DateTime? Taken)
{
    public string TakenText => Taken?.ToString("g") ?? "";
    public long TakenValue => Taken?.Ticks ?? 0;
}

public sealed record VmDiskFile(string Path, long Bytes);

public sealed record NewVmOptions(IReadOnlyList<string> Switches, string DiskFolder);

public sealed record NewVmPlan(string Name, int Generation, long MemoryBytes, int Processors, long DiskBytes, string IsoPath, string? Switch, bool SecureBoot, bool Tpm);

/// <summary>
/// Virtual machines on this PC. Reading and changing state go through WMI, so a refresh costs no process. Creating a VM and
/// taking a checkpoint go through the Hyper-V PowerShell module: creating takes a dozen WMI calls that the module wraps in
/// one line each, and Checkpoint-VM is what applies a VM's choice of production or standard checkpoints.
/// </summary>
public static class HyperV
{
    const string Root = @"root\virtualization\v2";
    const string HyperVAdministrators = "S-1-5-32-578";
    const string ManagementService = "Msvm_VirtualSystemManagementService";
    const string SnapshotService = "Msvm_VirtualSystemSnapshotService";
    const string RealizedSystem = "Microsoft:Hyper-V:System:Realized";

    // Values from Msvm_ComputerSystem.RequestStateChange and CIM_ConcreteJob.
    const ushort StateRunning = 2, StateOff = 3, StateSaved = 6, StatePaused = 9;
    const ushort FullSnapshot = 2;
    const uint Completed = 0, JobStarted = 4096;
    const ulong JobDone = 7, JobFailed = 10;

    /// <summary>What <see cref="ChangeAsync"/> returns when a VM refuses to shut down because it's locked. Forcing it is the way past that.</summary>
    public const string LockedSystem = "It's locked, and a locked system won't shut down unless it's forced.";

    static readonly IReadOnlyList<HyperVm> NoVms = Array.Empty<HyperVm>();

    static string PowerShell => Cmd.System(@"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Hyper-V answers administrators and the Hyper-V Administrators group, whose members don't need to elevate. Joining counts from the next sign-in.</summary>
    public static bool HasRights { get; } = ReadRights();

    /// <summary>False when the Hyper-V Module for Windows PowerShell isn't installed.</summary>
    public static bool HasModule => Directory.Exists(Cmd.System(@"WindowsPowerShell\v1.0\Modules\Hyper-V"));

    /// <summary>False when the Hyper-V GUI Management Tools aren't installed.</summary>
    public static bool HasConsole => File.Exists(Cmd.System("vmconnect.exe"));

    static bool ReadRights()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = new WindowsPrincipal(identity);
        return user.IsInRole(WindowsBuiltInRole.Administrator) || user.IsInRole(new SecurityIdentifier(HyperVAdministrators));
    }

    // ---- Reading ----

    /// <summary>Whether Hyper-V can be used, and if so what's on it. Blocks, so call it off the UI thread. Never throws: this runs on a timer, and no failure here should reach the user as a crash.</summary>
    public static HyperVReading Read()
    {
        try
        {
            var service = ServiceState();
            if (service == null) return new(FeatureState(), NoVms);
            if (service != ServiceControllerStatus.Running) return new(HyperVStatus.ServiceStopped, NoVms);
            return HasRights ? new(HyperVStatus.Ready, ReadVms()) : new(HyperVStatus.NoAccess, NoVms);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
        {
            return new(HyperVStatus.RestartNeeded, NoVms);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new(HyperVStatus.NoAccess, NoVms);
        }
        catch (UnauthorizedAccessException)
        {
            return new(HyperVStatus.NoAccess, NoVms);
        }
        catch (Exception ex)
        {
            return new(HyperVStatus.Unknown, NoVms, ex.Message);
        }
    }

    /// <summary>Parts Hyper-V runs without but this page needs, in plain words. Empty when everything is there. Blocks.</summary>
    public static List<string> Gaps()
    {
        var gaps = new List<string>();
        var system = Wmi.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();
        if (system?.Bool("HypervisorPresent") == false)
            gaps.Add("The hypervisor isn't running, so virtual machines can't start. Check that virtualization is on in the PC's BIOS or UEFI settings.");
        if (!HasModule)
            gaps.Add("The Hyper-V Module for Windows PowerShell isn't installed, so Tman can't create VMs or take checkpoints. Turn it on in Windows features.");
        if (!HasConsole)
            gaps.Add("The Hyper-V GUI Management Tools aren't installed, so Tman can't open a VM's console. Turn them on in Windows features.");
        return gaps;
    }

    /// <summary>The checkpoints of a VM, oldest first, or null if they couldn't be read. Blocks.</summary>
    public static List<VmCheckpoint>? ReadCheckpoints(string vmId)
    {
        try
        {
            // Recovery and replica points belong to backup and replication, and aren't the user's to apply.
            return Query(Root, $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId}'",
                    o => (Type: o.Str("VirtualSystemType"), Item: new VmCheckpoint(o.Path.Path, o.Str("ElementName"), o.Date("CreationTime"))))
                .Where(c => c.Type is "Microsoft:Hyper-V:Snapshot:Realized" or "Microsoft:Hyper-V:Snapshot:Planned")
                .Select(c => c.Item)
                .OrderBy(c => c.Taken)
                .ToList();
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return null;
        }
    }

    /// <summary>What the new VM form offers. Blocks.</summary>
    public static NewVmOptions ReadNewVmOptions()
    {
        try
        {
            var switches = Query(Root, "SELECT ElementName FROM Msvm_VirtualEthernetSwitch", o => o.Str("ElementName"));
            var folder = Query(Root, "SELECT DefaultVirtualHardDiskPath FROM Msvm_VirtualSystemManagementServiceSettingData", o => o.Str("DefaultVirtualHardDiskPath"));
            return new(switches.Where(s => s.Length > 0).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(), folder.FirstOrDefault() ?? "");
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return new(Array.Empty<string>(), "");
        }
    }

    /// <summary>
    /// The disk files deleting this VM could also delete: each disk and the chain of parents under it, which is where the
    /// data really is once a VM has checkpoints. Files another VM still uses are left out. Null means the list couldn't be read.
    /// </summary>
    public static async Task<List<VmDiskFile>?> ReadDisksAsync(HyperVm vm)
    {
        var result = await WingetCli.RunAsync(PowerShell, Arguments(DisksScript(vm.Id)), 60000).ConfigureAwait(false);
        if (result.Code != 0) return null;
        var disks = new List<VmDiskFile>();
        foreach (var line in result.Lines)
        {
            var parts = line.Split('|', 2);
            if (parts.Length == 2 && long.TryParse(parts[0], out var bytes)) disks.Add(new VmDiskFile(parts[1], bytes));
        }
        return disks;
    }

    static ServiceControllerStatus? ServiceState()
    {
        try
        {
            using var service = new ServiceController("vmms");
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>For when the service isn't there. A restart is what's missing if the feature is on already.</summary>
    static HyperVStatus FeatureState()
    {
        var features = Query(@"root\cimv2", "SELECT Name, InstallState FROM Win32_OptionalFeature WHERE Name LIKE 'Microsoft-Hyper-V%'",
            o => (Name: o.Str("Name"), State: o.U64("InstallState")));
        if (!features.Any(f => f.Name == "Microsoft-Hyper-V-All")) return HyperVStatus.NotInEdition;
        return features.Any(f => f.Name == "Microsoft-Hyper-V" && f.State == 1) ? HyperVStatus.RestartNeeded : HyperVStatus.TurnedOff;
    }

    /// <summary>The host appears in Msvm_ComputerSystem too. Its name is the PC's, and a VM's is a GUID.</summary>
    static List<HyperVm> ReadVms()
    {
        var settings = Query(Root, $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = '{RealizedSystem}'", o => o.Path.Path);
        var usage = ReadUsage(settings.ToArray());
        return Query(Root, "SELECT Name, ElementName, EnabledState, OnTimeInMilliseconds FROM Msvm_ComputerSystem",
                o => (Id: o.Str("Name"), Name: o.Str("ElementName"), State: o.U64("EnabledState"), Up: o.U64("OnTimeInMilliseconds")))
            .Where(v => Guid.TryParse(v.Id, out _))
            .Select(v =>
            {
                var (power, state) = DescribeState(v.State);
                bool live = power is VmPower.Running or VmPower.Paused or VmPower.Changing;
                var seen = usage.TryGetValue(v.Id, out var u) && live;
                return new HyperVm(v.Id, v.Name, power, state, seen ? u.Cpu : null, seen ? u.MemoryBytes : null, live ? TimeSpan.FromMilliseconds(v.Up) : TimeSpan.Zero);
            })
            .ToList();
    }

    /// <summary>CPU and memory by VM. The list is still worth showing without them, so a failure here leaves those columns blank.</summary>
    static Dictionary<string, (uint Cpu, ulong MemoryBytes)> ReadUsage(string[] settings)
    {
        const uint Name = 0, ProcessorLoad = 101, MemoryUsage = 103;
        var usage = new Dictionary<string, (uint, ulong)>(StringComparer.OrdinalIgnoreCase);
        if (settings.Length == 0) return usage;
        try
        {
            using var service = Service(ManagementService);
            using var input = service.GetMethodParameters("GetSummaryInformation");
            input["SettingData"] = settings;
            input["RequestedInformation"] = new[] { Name, ProcessorLoad, MemoryUsage };
            using var output = service.InvokeMethod("GetSummaryInformation", input, null);
            if (output["SummaryInformation"] is ManagementBaseObject[] rows)
                foreach (var row in rows)
                    using (row) usage[row.Str("Name")] = ((uint)row.U64("ProcessorLoad"), row.U64("MemoryUsage") * 1048576);
        }
        catch (Exception ex) when (IsWmiFailure(ex)) { }
        return usage;
    }

    /// <summary>EnabledState, including the values older Hyper-V versions used. Anything else is a step Hyper-V is partway through.</summary>
    public static (VmPower Power, string Text) DescribeState(ulong state) => state switch
    {
        2 => (VmPower.Running, "Running"),
        3 => (VmPower.Off, "Off"),
        6 or 32769 or 32779 => (VmPower.Saved, "Saved"),
        9 or 32768 => (VmPower.Paused, "Paused"),
        4 or 32774 => (VmPower.Changing, "Stopping"),
        10 or 32770 => (VmPower.Changing, "Starting"),
        32771 => (VmPower.Changing, "Checkpoint"),
        32773 or 32780 => (VmPower.Changing, "Saving"),
        32776 => (VmPower.Changing, "Pausing"),
        32777 => (VmPower.Changing, "Resuming"),
        0 or 1 => (VmPower.Unknown, "Unknown"),
        _ => (VmPower.Changing, "Busy"),
    };

    /// <summary>
    /// <see cref="Wmi.Query"/> turns every failure into an empty list, which here would read as "no virtual machines".
    /// This one lets the failure through, and hands each object back to be disposed, since it runs on a timer.
    /// </summary>
    static List<T> Query<T>(string scope, string wql, Func<ManagementObject, T> read)
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var found = searcher.Get();
        var items = new List<T>();
        foreach (ManagementObject o in found)
            using (o) items.Add(read(o));
        return items;
    }

    // ---- Actions ----

    /// <summary>Changes a VM's state and waits for Hyper-V to finish. Returns what went wrong, or null.</summary>
    public static Task<string?> ChangeAsync(HyperVm vm, VmAction action) => Guard(async () =>
    {
        if (action is VmAction.ShutDown or VmAction.ForceShutDown) return await ShutDown(vm, action == VmAction.ForceShutDown).ConfigureAwait(false);
        ushort state = action switch
        {
            VmAction.TurnOff => StateOff,
            VmAction.Pause => StatePaused,
            VmAction.Save => StateSaved,
            _ => StateRunning,
        };
        using var system = VmObject(vm.Id);
        return await Call(system, "RequestStateChange", p => p["RequestedState"] = state, _ => { }).ConfigureAwait(false);
    });

    /// <summary>
    /// Asks the system inside the VM to shut down, the same as choosing Shut down in it, which needs its integration services
    /// running. Unforced, a program holding unsaved work can refuse, and a locked VM refuses outright with <see cref="LockedSystem"/>.
    /// </summary>
    static async Task<string?> ShutDown(HyperVm vm, bool force)
    {
        var parts = Query(Root, $"SELECT * FROM Msvm_ShutdownComponent WHERE SystemName = '{vm.Id}'", o => o.Path.Path);
        if (parts.Count == 0) return "It has no shutdown service running. It may still be starting, or its system may not support this.";
        using var shutdown = new ManagementObject(parts[0]);
        return await Call(shutdown, "InitiateShutdown", p =>
        {
            p["Force"] = force;
            p["Reason"] = "Shut down from Tman";
        }, _ => { }).ConfigureAwait(false);
    }

    /// <summary>Opens the VM's console. Returns what went wrong, or null. Declining the elevation prompt isn't a failure.</summary>
    public static string? OpenConsole(HyperVm vm)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Cmd.System("vmconnect.exe"), $"localhost -G {vm.Id}") { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    public static async Task<JobResult> TakeCheckpointAsync(HyperVm vm, Action<string> onLine)
    {
        int code = await RunScript(CheckpointScript(vm.Id), onLine).ConfigureAwait(false);
        return code == 0
            ? new(JobLevel.Good, $"Took a checkpoint of {vm.Name}.")
            : new(JobLevel.Bad, "No checkpoint was taken. The log above says why.");
    }

    public static async Task<JobResult> ApplyCheckpointAsync(HyperVm vm, VmCheckpoint checkpoint, Action<string> onLine)
    {
        var failed = await Guard(async () =>
        {
            using var service = Service(SnapshotService);
            return await Call(service, "ApplySnapshot", p => p["Snapshot"] = checkpoint.Path, onLine).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return failed == null
            ? new(JobLevel.Good, $"{vm.Name} is back at {checkpoint.Name}.")
            : new(JobLevel.Bad, $"{vm.Name} wasn't changed. {failed}");
    }

    public static async Task<JobResult> DeleteCheckpointAsync(HyperVm vm, VmCheckpoint checkpoint, Action<string> onLine)
    {
        var failed = await Guard(async () =>
        {
            using var service = Service(SnapshotService);
            return await Call(service, "DestroySnapshot", p => p["AffectedSnapshot"] = checkpoint.Path, onLine).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return failed == null
            ? new(JobLevel.Good, $"Deleted the checkpoint. Hyper-V may keep merging its data into {vm.Name}'s disk for a while.")
            : new(JobLevel.Bad, $"The checkpoint wasn't deleted. {failed}");
    }

    /// <param name="disks">Files to delete along with the VM. Empty keeps them all.</param>
    public static async Task<JobResult> DeleteVmAsync(HyperVm vm, IReadOnlyList<VmDiskFile> disks, Action<string> onLine)
    {
        var failed = await Guard(async () =>
        {
            using var service = Service(ManagementService);
            using var system = VmObject(vm.Id);
            return await Call(service, "DestroySystem", p => p["AffectedSystem"] = system.Path.Path, onLine).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (failed != null) return new(JobLevel.Bad, $"{vm.Name} wasn't deleted. {failed}");
        onLine($"Deleted {vm.Name}.");
        int left = 0;
        foreach (var disk in disks)
            if (!await DeleteFile(disk.Path, onLine).ConfigureAwait(false)) left++;
        return left > 0
            ? new(JobLevel.Warn, $"{vm.Name} was deleted, but {left} of its disk files are still there. The log above says which.")
            : new(JobLevel.Good, disks.Count > 0 ? $"{vm.Name} and its disks were deleted." : $"{vm.Name} was deleted. Its disk files are still on this PC.");
    }

    /// <summary>Hyper-V can hold a disk for a few seconds after its VM is gone.</summary>
    static async Task<bool> DeleteFile(string path, Action<string> onLine)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                onLine($"Removed {path}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    onLine($"Couldn't remove {path}: {ex.Message}");
                    return false;
                }
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    }

    public static async Task<JobResult> CreateAsync(NewVmPlan plan, Action<string> onLine)
    {
        int code = await RunScript(CreateScript(plan), onLine).ConfigureAwait(false);
        return code == 0
            ? new(JobLevel.Good, $"{plan.Name} is ready. Start it, open its console, and press a key when it offers to boot from the ISO.")
            : new(JobLevel.Bad, $"{plan.Name} wasn't created. The log above says why, and whether anything was left behind.");
    }

    /// <summary>What's wrong with a plan, in plain words. Empty means it can go ahead.</summary>
    public static List<string> CheckPlan(NewVmPlan plan, IEnumerable<string> takenNames, long hostMemoryBytes)
    {
        const int MaxName = 100;
        var problems = new List<string>();
        var name = plan.Name;
        if (name.Length == 0) problems.Add("Give the virtual machine a name.");
        else if (name.Length > MaxName) problems.Add($"Names can be up to {MaxName} characters long.");
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.')) problems.Add(@"The name becomes a file name, so it can't contain \ / : * ? "" < > | or end with a dot.");
        else if (takenNames.Contains(name, StringComparer.OrdinalIgnoreCase)) problems.Add($"There's already a virtual machine called {name}.");
        if (plan.IsoPath.Length == 0) problems.Add("Choose an ISO to install from.");
        else if (!File.Exists(plan.IsoPath)) problems.Add("That ISO isn't there any more.");
        if (hostMemoryBytes > 0 && plan.MemoryBytes > hostMemoryBytes) problems.Add($"This PC only has {Fmt.Bytes(hostMemoryBytes)} of memory.");
        return problems;
    }

    // ---- WMI method calls ----

    /// <summary>Returns the failure's message for the actions to report. The caller's wording says what didn't happen.</summary>
    static async Task<string?> Guard(Func<Task<string?>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsWmiFailure(ex))
        {
            return ex.Message.Trim();
        }
    }

    static bool IsWmiFailure(Exception ex) => ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException;

    static ManagementObject VmObject(string id) => new($@"\\.\{Root}:Msvm_ComputerSystem.CreationClassName=""Msvm_ComputerSystem"",Name=""{id}""");

    static ManagementObject Service(string name) => new(Query(Root, $"SELECT * FROM {name}", o => o.Path.Path).First());

    /// <summary>Calls a method, which either finishes at once or starts a job to follow. Returns what went wrong, or null.</summary>
    static async Task<string?> Call(ManagementObject target, string method, Action<ManagementBaseObject> fill, Action<string> onLine)
    {
        string? job;
        using (var input = target.GetMethodParameters(method))
        {
            fill(input);
            using var output = target.InvokeMethod(method, input, null);
            var code = Convert.ToUInt32(output["ReturnValue"]);
            if (code == Completed) return null;
            if (code != JobStarted) return ExplainCode(code);
            // InitiateShutdown can say a job started, but has nowhere to say which.
            job = output.Properties.Cast<PropertyData>().FirstOrDefault(p => p.Name == "Job")?.Value as string;
        }
        return job == null ? null : await FollowJob(job, onLine).ConfigureAwait(false);
    }

    /// <summary>Reports progress as "40%" lines, which the job panel draws as a bar.</summary>
    static async Task<string?> FollowJob(string path, Action<string> onLine)
    {
        using var job = new ManagementObject(path);
        ulong shown = 0;
        while (true)
        {
            try
            {
                job.Get();
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
            {
                return null; // Finished jobs are removed. The next refresh shows how it ended.
            }
            var state = job.U64("JobState");
            if (state is >= JobDone and <= JobFailed)
            {
                if (state == JobDone && job.U64("ErrorCode") == 0) return null;
                var error = job.Str("ErrorDescription");
                return error.Length > 0 ? error : "Hyper-V stopped the job without saying why.";
            }
            var percent = job.U64("PercentComplete");
            if (percent > shown)
            {
                shown = percent;
                onLine($"{percent}%");
            }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    static string ExplainCode(uint code) => code switch
    {
        32768 => "Hyper-V reported a failure.",
        32769 => "Windows refused access.",
        32770 => "Hyper-V doesn't support that here.",
        32772 => "It took too long.",
        32773 => "Hyper-V rejected the request.",
        32774 => "The virtual machine is busy with something else.",
        32775 => "The virtual machine can't do that in its current state.",
        32777 => "The virtual machine isn't available.",
        32778 => "There isn't enough memory.",
        32779 => "A file it needs is missing.",
        32780 => "The system inside it isn't ready yet.",
        32781 => LockedSystem,
        32782 => "It's already shutting down.",
        _ => $"Hyper-V returned code {code}.",
    };

    // ---- PowerShell ----

    /// <summary>A failure ends with its message as the last output line and exit code 1. UTF-8 keeps names and paths intact on the way back.</summary>
    static IEnumerable<string> Arguments(string body) => new[]
    {
        "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
        "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
        "$ProgressPreference = 'SilentlyContinue'\n" +
        "$ErrorActionPreference = 'Stop'\n" +
        "try {\nImport-Module Hyper-V\n" + body + "\n} catch {\n$_.Exception.Message\nexit 1\n}",
    };

    static Task<int> RunScript(string body, Action<string> onLine) => WingetCli.StreamAsync(PowerShell, Arguments(body), onLine);

    /// <summary>Single-quoted, so nothing in it runs. PowerShell also treats curly single quotes as quotes, so those are doubled along with the straight one.</summary>
    internal static string Literal(string text) => "'" + Regex.Replace(text, "['\u2018\u2019\u201A\u201B]", "$0$0") + "'";

    internal static string CheckpointScript(string vmId) => $"""
        'Taking the checkpoint'
        Checkpoint-VM -VM (Get-VM -Id {Literal(vmId)})
        """;

    /// <summary>Files come out as "size|path", which can't be ambiguous because a Windows path can't contain a bar.</summary>
    internal static string DisksScript(string vmId) => $$"""
        function Files($machine) {
            foreach ($drive in Get-VMHardDiskDrive -VM $machine) {
                $file = $drive.Path
                while ($file -and (Test-Path -LiteralPath $file)) {
                    $file
                    $file = (Get-VHD -Path $file).ParentPath
                }
            }
        }
        $vm = Get-VM -Id {{Literal(vmId)}}
        $others = @(Get-VM | Where-Object { $_.Id -ne $vm.Id } | ForEach-Object { Files $_ })
        foreach ($file in (Files $vm | Select-Object -Unique)) {
            if ($others -notcontains $file) { '{0}|{1}' -f (Get-Item -LiteralPath $file).Length, $file }
        }
        """;

    /// <summary>
    /// Everything happens in one go, so that a failed attempt can be tried again under the same name: on any failure the
    /// VM goes, and so does the disk, which can only be this attempt's because the script checks for it first.
    /// </summary>
    internal static string CreateScript(NewVmPlan p)
    {
        var options = p.Switch == null ? "" : $"$new.SwitchName = {Literal(p.Switch)}\n";
        var boot = p.Generation == 2
            ? $"""
              Add-VMDvdDrive -VM $vm -Path {Literal(p.IsoPath)}
              Set-VMFirmware -VM $vm -FirstBootDevice (Get-VMDvdDrive -VM $vm) -EnableSecureBoot {(p.SecureBoot ? "On" : "Off")}
              """
            : $"Get-VMDvdDrive -VM $vm | Set-VMDvdDrive -Path {Literal(p.IsoPath)}";
        var tpm = p.Generation == 2 && p.Tpm
            ? """
              'Adding the virtual TPM'
              Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
              Enable-VMTPM -VM $vm
              """
            : "";
        return $$"""
            $vm = $null
            $disk = $null
            try {
                $file = Join-Path (Get-VMHost).VirtualHardDiskPath {{Literal(p.Name + ".vhdx")}}
                if (Test-Path -LiteralPath $file) { throw "$file already exists. Choose another name, or move that file first." }
                $disk = $file
                'Creating the virtual machine and its disk'
                $new = @{ Name = {{Literal(p.Name)}}; Generation = {{p.Generation}}; MemoryStartupBytes = {{p.MemoryBytes}}; NewVHDPath = $file; NewVHDSizeBytes = {{p.DiskBytes}} }
                {{options}}$vm = New-VM @new
                'Setting processors and memory'
                Set-VM -VM $vm -ProcessorCount {{p.Processors}} -StaticMemory
                'Attaching the ISO'
                {{boot}}
                {{tpm}}
                'Done'
            } catch {
                $_.Exception.Message
                try {
                    if ($vm) { Remove-VM -VM $vm -Force }
                    if ($disk -and (Test-Path -LiteralPath $disk)) { Remove-Item -LiteralPath $disk -Force }
                    'Nothing was left behind.'
                } catch {
                    "Some of it was left behind: $($_.Exception.Message)"
                }
                exit 1
            }
            """;
    }
}
