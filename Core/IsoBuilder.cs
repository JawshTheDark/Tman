using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ComStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Tman.Core;

/// <summary>A problem to show the user as it stands.</summary>
public sealed class IsoProblem(string message) : Exception(message);

/// <summary>One edition inside a Windows image, as the image's own XML describes it.</summary>
public sealed record IsoEdition(int Index, string Name, string EditionId, string Arch, string Language, int Build, long Bytes);

/// <summary>A Windows ISO that's been read and checked. <c>ImageFile</c> is relative to the ISO's root.</summary>
public sealed record IsoMedia(string Path, string Label, string ImageFile, long ImageBytes, long IsoBytes, IReadOnlyList<IsoEdition> Editions);

public enum IsoRemoveKind { App, Capability, Feature }

/// <summary>Something a build can take out. <c>Id</c> is a package, capability or feature name; <c>Files</c> are also deleted, relative to the image's root.</summary>
public sealed record IsoRemovable(string Title, string Note, IsoRemoveKind Kind, string Id, bool Suggested, string[]? Files = null);

/// <summary><c>LocalAccount</c> adds an answer file creating that account; null leaves Windows' own setup screens alone.</summary>
public sealed record IsoPlan(string SourceIso, string OutputIso, char WorkDrive, IsoEdition Edition, IReadOnlyList<IsoRemovable> Remove,
    bool CleanComponents, string? LocalAccount);

public sealed record UsbDisk(int Number, string Name, long Size, bool IsUsb, bool IsSystem, bool IsBoot, bool IsReadOnly, bool IsOffline, string Id, IReadOnlyList<char> Letters)
{
    public string Title => $"Disk {Number}: {Name}, {Fmt.Bytes(Size)}";
    public override string ToString() => Letters.Count == 0 ? Title : $"{Title} ({string.Join(", ", Letters.Select(l => l + ":"))})";
}

/// <summary>
/// Turns an official Windows 11 ISO into a trimmed one and can write the result to a USB drive. A build works in a folder of
/// its own, <c>X:\TmanIsoBuild\&lt;time&gt;</c>, and nothing outside it changes until the finished ISO is moved into place.
///
/// A mounted image left behind ties up gigabytes and blocks later mounts, so three things stop that happening. Every way out of
/// <see cref="BuildAsync"/> discards the mount. A journal names the build before anything is mounted, and a second Tman process
/// watches this one and does the same cleanup if it dies, even from End task, where nothing here gets to run. If the power goes
/// too, the journal is still there for the next start. The ISO itself is attached so that Windows detaches it when Tman goes.
/// No registry hive is ever loaded from the image, because a hive left loaded would stop it unmounting.
/// </summary>
public static class IsoBuilder
{
    public const string WatchdogFlag = "--iso-watchdog";

    const string WorkFolderName = "TmanIsoBuild";
    const long Gb = 1024L * 1024 * 1024;

    // ---- What can be removed ----

    static readonly HashSet<string> SuggestedApps = new(GetAppsCatalog.CommonExtras, StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.BingSearch", "Microsoft.GetHelp", "Microsoft.WindowsFeedbackHub", "Microsoft.MicrosoftOfficeHub", "Microsoft.Copilot", "Microsoft.Todos",
        "Microsoft.PowerAutomateDesktop", "Microsoft.OutlookForWindows", "MSTeams", "MicrosoftCorporationII.MicrosoftFamily", "Microsoft.Windows.DevHome",
        "Microsoft.WindowsMaps", "Microsoft.People", "microsoft.windowscommunicationsapps", "Microsoft.MixedReality.Portal", "Microsoft.Microsoft3DViewer",
        "Microsoft.SkypeApp", "Microsoft.ZuneVideo",
    };

    /// <summary>
    /// The Store apps worth offering: the same names as Get apps, minus what that page protects. Media codecs are left out too.
    /// They're tiny, so taking them out saves nothing and only costs file formats.
    /// </summary>
    public static readonly IsoRemovable[] Apps = GetAppsCatalog.BuiltIns
        .Where(a => GetAppsCatalog.ProtectedReason(a.Key, false, false, "", "") == null && !a.Key.Contains("Extension", StringComparison.OrdinalIgnoreCase))
        .OrderBy(a => a.Value.Name, StringComparer.OrdinalIgnoreCase)
        .Select(a => new IsoRemovable(a.Value.Name, a.Value.Note, IsoRemoveKind.App, a.Key, SuggestedApps.Contains(a.Key)))
        .ToArray();

    /// <summary>Capability names are matched by the start of the name, because their versions differ between releases.</summary>
    public static readonly IsoRemovable[] Extras =
    {
        new("OneDrive", "The installer that sets OneDrive up for each new user. Files already in the cloud aren't touched, and it can be installed later.", IsoRemoveKind.App, "Microsoft.OneDriveSync", true,
            new[] { @"Windows\System32\OneDriveSetup.exe", @"Windows\SysWOW64\OneDriveSetup.exe" }),
        new("Internet Explorer mode", "Edge's mode for very old sites.", IsoRemoveKind.Capability, "Browser.InternetExplorer", true),
        new("Media Player Legacy", "The old Windows Media Player. The newer Media Player app is separate.", IsoRemoveKind.Capability, "Media.WindowsMediaPlayer", true),
        new("WordPad", "The old text editor. Recent Windows 11 releases no longer include it.", IsoRemoveKind.Capability, "Microsoft.Windows.WordPad", true),
        new("Steps Recorder", "Records clicks for support requests.", IsoRemoveKind.Capability, "App.StepsRecorder", true),
        new("Math Recognizer", "Reads handwritten math.", IsoRemoveKind.Capability, "MathRecognizer", true),
        new("Recall", "Saves snapshots of your screen on Copilot+ PCs.", IsoRemoveKind.Feature, "Recall", true),
        new("SMB 1.0 file sharing", "The old, insecure file sharing. Very old NAS boxes and printers may need it.", IsoRemoveKind.Feature, "SMB1Protocol", true),
        new("PowerShell ISE", "The old script editor. PowerShell itself stays.", IsoRemoveKind.Capability, "Microsoft.Windows.PowerShell.ISE", false),
        new("Windows Fax and Scan", "Printing still works. Scanning needs another app.", IsoRemoveKind.Capability, "Print.Fax.Scan", false),
        new("Windows Hello face", "Face sign-in stops working.", IsoRemoveKind.Capability, "Hello.Face", false),
        new("Handwriting", "Pen input recognition.", IsoRemoveKind.Capability, "Language.Handwriting", false),
        new("Speech recognition", "Voice typing and voice commands.", IsoRemoveKind.Capability, "Language.Speech", false),
    };

    // ---- Reading an ISO ----

    /// <summary>Attaches the ISO just long enough to read which editions it has. Blocks, and throws <see cref="IsoProblem"/> for anything it can't use.</summary>
    public static IsoMedia ReadMedia(string path)
    {
        using var iso = AttachedIso.Open(path);
        string At(string relative) => Path.Combine(iso.Root, relative);
        var image = new[] { @"sources\install.wim", @"sources\install.esd" }.FirstOrDefault(f => File.Exists(At(f)));
        if (image == null)
            throw new IsoProblem(File.Exists(At(@"sources\install.swm"))
                ? "This ISO splits its Windows image into pieces, which Tman can't work with. Use an ISO from Microsoft's download page."
                : "This isn't Windows installation media: there's no Windows image in its sources folder.");
        if (!File.Exists(At("setup.exe")) || !File.Exists(At(@"sources\boot.wim")))
            throw new IsoProblem("This isn't complete Windows installation media: setup.exe or boot.wim is missing.");
        if (!File.Exists(At(@"efi\microsoft\boot\efisys.bin")))
            throw new IsoProblem("This ISO has no UEFI boot file, so a rebuilt copy couldn't boot.");
        var editions = ReadEditions(At(image));
        var usable = editions.Where(e => e.Build >= 22000 && e.Arch is "x64" or "arm64").ToList();
        if (usable.Count == 0)
            throw new IsoProblem(editions.Count > 0 && editions[0].Build is > 0 and < 22000
                ? $"This is an older version of Windows (build {editions[0].Build}). Tman builds from Windows 11 ISOs."
                : "This ISO has no Windows 11 edition Tman can use.");
        var label = new DriveInfo(iso.Root).VolumeLabel;
        return new IsoMedia(path, label, image, new FileInfo(At(image)).Length, new FileInfo(path).Length, usable);
    }

    /// <summary>A WIM keeps a UTF-16 XML description of its images, which its header points to, so this needs neither DISM nor admin.</summary>
    public static List<IsoEdition> ReadEditions(string wimPath)
    {
        using var file = File.OpenRead(wimPath);
        var header = new byte[208];
        try { file.ReadExactly(header); }
        catch (EndOfStreamException) { throw new IsoProblem("The Windows image in this ISO is damaged."); }
        if (!header.AsSpan(0, 8).SequenceEqual("MSWIM\0\0\0"u8))
            throw new IsoProblem("The Windows image in this ISO isn't in a format Tman can read.");
        long size = BitConverter.ToInt64(header, 72) & 0x00FF_FFFF_FFFF_FFFF;
        long offset = BitConverter.ToInt64(header, 80);
        if (size is <= 0 or > 64 * 1024 * 1024 || offset <= 0 || offset + size > file.Length)
            throw new IsoProblem("The Windows image in this ISO is damaged.");
        var xml = new byte[size];
        file.Position = offset;
        file.ReadExactly(xml);
        return ParseEditions(Encoding.Unicode.GetString(xml).TrimStart('\uFEFF'));
    }

    public static List<IsoEdition> ParseEditions(string xml)
    {
        XElement? root;
        try { root = XDocument.Parse(xml).Root; }
        catch (System.Xml.XmlException) { throw new IsoProblem("The Windows image in this ISO has an unreadable list of editions."); }
        var editions = new List<IsoEdition>();
        foreach (var image in root?.Elements("IMAGE") ?? Enumerable.Empty<XElement>())
        {
            var windows = image.Element("WINDOWS");
            if (!int.TryParse(image.Attribute("INDEX")?.Value, out var index) || windows == null) continue;
            if (!string.Equals((string?)windows.Element("INSTALLATIONTYPE"), "Client", StringComparison.OrdinalIgnoreCase)) continue;
            var arch = (string?)windows.Element("ARCH") switch { "9" => "x64", "12" => "arm64", "0" => "x86", var other => other ?? "" };
            var languages = windows.Element("LANGUAGES");
            int.TryParse((string?)windows.Element("VERSION")?.Element("BUILD"), out var build);
            long.TryParse((string?)image.Element("TOTALBYTES"), out var bytes);
            var name = (string?)image.Element("DISPLAYNAME") ?? (string?)image.Element("NAME") ?? $"Edition {index}";
            editions.Add(new IsoEdition(index, name, (string?)windows.Element("EDITIONID") ?? "", arch,
                (string?)languages?.Element("DEFAULT") ?? (string?)languages?.Element("LANGUAGE") ?? "", build, bytes));
        }
        return editions;
    }

    // ---- Checks before a build ----

    /// <summary>The one room to work in: the unpacked image, a copy to edit, the final copy, and the rest of the ISO, plus some for DISM's temporary files.</summary>
    public static long WorkBytes(IsoMedia media, IsoEdition edition)
    {
        // An ESD is packed tighter than the WIM it becomes.
        long wim = media.ImageFile.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) ? media.ImageBytes * 3 / 2 : media.ImageBytes;
        return edition.Bytes + wim * 2 + (media.IsoBytes - media.ImageBytes) + 4 * Gb;
    }

    /// <summary>Whether the work drive can hold a build: DISM can only mount on a local NTFS drive. The type comes first because asking a busy USB drive if it's ready can take minutes.</summary>
    public static bool CanWorkOn(DriveInfo d) => d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat == "NTFS";

    /// <summary>What stops this build from starting, or null when it can go ahead. <c>free</c> and <c>format</c> read a drive, so tests can stand in for real disks.</summary>
    public static string? CheckPlan(IsoPlan plan, IsoMedia media, Func<char, long>? free = null, Func<char, string>? format = null)
    {
        free ??= letter => new DriveInfo(letter.ToString()).AvailableFreeSpace;
        format ??= letter => new DriveInfo(letter.ToString()).DriveFormat;
        var output = Path.GetFullPath(plan.OutputIso);
        if (string.Equals(output, Path.GetFullPath(plan.SourceIso), StringComparison.OrdinalIgnoreCase))
            return "The output can't be the ISO you're building from. Choose another name.";
        if (plan.LocalAccount != null && AccountProblem(plan.LocalAccount) is { } account) return account;
        char outDrive = char.ToUpperInvariant(output[0]);
        long work = WorkBytes(media, plan.Edition), iso = media.IsoBytes;
        char workDrive = char.ToUpperInvariant(plan.WorkDrive);
        if (output.StartsWith(@"\\")) return free(workDrive) >= work ? null : NotEnough(workDrive, work, free(workDrive));
        // Found now, because otherwise it's found an hour in, when the ISO reaches 4 GB.
        if (format(outDrive).StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && media.IsoBytes > Fat32MaxFile / 2)
            return $"{outDrive}: is formatted as FAT32, which can't hold a file this size. Save the ISO to an NTFS or exFAT drive.";
        if (outDrive == workDrive) work += iso;
        else if (free(outDrive) < iso) return NotEnough(outDrive, iso, free(outDrive));
        return free(workDrive) >= work ? null : NotEnough(workDrive, work, free(workDrive));
    }

    static string NotEnough(char drive, long needed, long free) =>
        $"This build needs about {Fmt.Bytes(needed)} on {drive}:, and {drive}: has {Fmt.Bytes(free)} free. Free up some space or choose another drive.";

    /// <summary>Windows' rules for an account name, checked here because Setup would only fail at the end of the install.</summary>
    public static string? AccountProblem(string name)
    {
        if (name.Length == 0) return "Enter a name for the local account.";
        if (name.Length > 20) return "An account name can have up to 20 characters.";
        if (name.IndexOfAny("\"/\\[]:;|=,+*?<>@".ToCharArray()) >= 0 || name.Any(char.IsControl)) return "An account name can't contain any of these: \" / \\ [ ] : ; | = , + * ? < > @";
        if (name.Trim(' ', '.').Length == 0 || name.EndsWith('.')) return "An account name can't be only dots and spaces, or end with a dot.";
        var reserved = new[] { "Administrator", "Guest", "DefaultAccount", "SYSTEM", "NONE", "WDAGUtilityAccount", "Users", "Administrators" };
        return reserved.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"Windows keeps the name {name} for itself. Choose another." : null;
    }

    // ---- Answer file ----

    /// <summary>
    /// An answer file for first-time setup. Nothing here describes a disk, so Setup still asks where to install. Listing a local
    /// account is the documented way to deploy without a Microsoft account, and hiding the network screen matters as much,
    /// since that screen is what blocks a PC with no connection.
    ///
    /// The windowsPE section is what makes Setup use the file at all. Setup looks for answer files in that stage and drops any
    /// with no settings for it ("does not meet criteria to be used for this unattend pass"), which silently loses everything
    /// else. <c>UseConfigurationSet</c> is false by default, so the section is there to be seen and changes nothing.
    /// </summary>
    public static string AnswerFile(string account, string arch)
    {
        XNamespace u = "urn:schemas-microsoft-com:unattend", wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        XElement Settings(string pass, string component, params object[] content) =>
            new(u + "settings", new XAttribute("pass", pass),
                new XElement(u + "component",
                    new XAttribute("name", component),
                    new XAttribute("processorArchitecture", arch == "arm64" ? "arm64" : "amd64"),
                    new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                    new XAttribute("language", "neutral"),
                    new XAttribute("versionScope", "nonSxS"),
                    content));
        var doc = new XDocument(new XElement(u + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", wcm),
            Settings("windowsPE", "Microsoft-Windows-Setup",
                new XElement(u + "UseConfigurationSet", "false")),
            Settings("oobeSystem", "Microsoft-Windows-Shell-Setup",
                    new XElement(u + "OOBE",
                        new XElement(u + "HideEULAPage", "true"),
                        new XElement(u + "HideOEMRegistrationScreen", "true"),
                        new XElement(u + "HideOnlineAccountScreens", "true"),
                        new XElement(u + "HideWirelessSetupInOOBE", "true"),
                        new XElement(u + "HideLocalAccountScreen", "true")),
                    new XElement(u + "UserAccounts",
                        new XElement(u + "LocalAccounts",
                            new XElement(u + "LocalAccount", new XAttribute(wcm + "action", "add"),
                                new XElement(u + "Name", account),
                                new XElement(u + "DisplayName", account),
                                new XElement(u + "Group", "Administrators"),
                                new XElement(u + "Password",
                                    new XElement(u + "Value", ""),
                                    new XElement(u + "PlainText", "true"))))))
        ));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + doc;
    }

    /// <summary>
    /// With only one edition left, a PC whose firmware holds a key for a different edition stops Setup with "no images are
    /// available". Naming the edition here stops Setup going by the firmware's key.
    /// </summary>
    public static string EditionConfig(string editionId) => $"[EditionID]\r\n{editionId}\r\n[Channel]\r\nRetail\r\n[VL]\r\n0\r\n";

    // ---- Reading DISM's lists ----

    public static List<string> ParseProvisioned(string output) =>
        Regex.Matches(output, @"^\s*PackageName\s*:\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToList();

    public static List<string> ParseInstalledCapabilities(string output)
    {
        var installed = new List<string>();
        string? name = null;
        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(Capability Identity|State)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            if (m.Groups[1].Value.StartsWith("Cap", StringComparison.OrdinalIgnoreCase)) name = m.Groups[2].Value;
            else
            {
                if (name != null && m.Groups[2].Value.Equals("Installed", StringComparison.OrdinalIgnoreCase)) installed.Add(name);
                name = null;
            }
        }
        return installed;
    }

    /// <summary>Feature names that still have their files, from <c>/Get-Features /Format:Table</c>.</summary>
    public static HashSet<string> ParseFeatures(string output)
    {
        var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(output, @"^\s*(\S+)\s*\|\s*(.+?)\s*$", RegexOptions.Multiline))
            if (!m.Groups[2].Value.Contains("Payload Removed", StringComparison.OrdinalIgnoreCase)) features.Add(m.Groups[1].Value);
        return features;
    }

    /// <summary>"Hello.Face" matches "Hello.Face.20134~~~~0.0.1.0" but not "Hello.Faces~~~~0.0.1.0".</summary>
    public static bool CapabilityMatches(string capability, string prefix) =>
        capability.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && capability.Length > prefix.Length && capability[prefix.Length] is '~' or '.';

    /// <summary>The status DISM gives a mounted folder, or null when that folder isn't one of its mounts.</summary>
    public static string? ParseMountStatus(string output, string mount)
    {
        string? dir = null;
        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(Mount Dir|Status)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            if (m.Groups[1].Value.StartsWith("Mount", StringComparison.OrdinalIgnoreCase)) dir = m.Groups[2].Value;
            else if (dir != null && dir.Equals(mount, StringComparison.OrdinalIgnoreCase)) return m.Groups[2].Value;
        }
        return null;
    }

    // ---- Building ----

    /// <summary>Builds the ISO and always cleans up after itself, however it ends. Stopping is safe at any point. Never throws.</summary>
    public static async Task<JobResult> BuildAsync(IsoPlan plan, IsoMedia media, Action<string> onLine, CancellationToken cancel)
    {
        var work = "";
        string partial = plan.OutputIso + ".partial";
        BuildJob? job = null;
        JobResult result;
        try
        {
            if (Journal.Read() is { } earlier)
                return new(JobLevel.Bad, earlier.Alive
                    ? "Another Tman is building an ISO or cleaning up after one. Wait for it to finish."
                    : "An earlier build left a mounted image behind. Clean that up first.");
            if (CheckPlan(plan, media) is { } problem) return new(JobLevel.Bad, problem);
            // A folder Tman can't write to should stop the build now, not when the ISO is finally written.
            File.Create(partial).Dispose();
            work = $@"{char.ToUpperInvariant(plan.WorkDrive)}:\{WorkFolderName}\{DateTime.Now:yyyyMMdd-HHmmss}";
            Directory.CreateDirectory(work);
            Journal.Begin(work);
            job = new BuildJob(plan, media, work, partial, onLine, cancel);
            result = await job.RunAsync();
        }
        catch (OperationCanceledException)
        {
            result = new(JobLevel.Warn, "Stopped. No ISO was made, and your original ISO wasn't touched.");
        }
        catch (IsoProblem ex)
        {
            result = new(JobLevel.Bad, ex.Message);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            result = new(JobLevel.Bad, $"The build failed: {ex.Message}");
        }
        TryDelete(partial);
        if (work.Length == 0) return result;
        onLine("Cleaning up");
        // Deliberately not cancellable. This is the one part that has to finish.
        if (await ReleaseAsync(work, onLine, job?.Interrupted ?? true)) return result;
        return new(JobLevel.Bad, $"{result.Text} The image is still mounted at {Path.Combine(work, "mount")}. Tman will try again when it next starts, or open ISO builder and choose Clean up.");
    }

    /// <summary>One build's steps. Anything that goes wrong throws, and <see cref="BuildAsync"/> cleans up.</summary>
    sealed class BuildJob(IsoPlan plan, IsoMedia media, string work, string partial, Action<string> onLine, CancellationToken cancel)
    {
        const int Steps = 8;

        readonly string _tree = Path.Combine(work, "iso"), _mount = Path.Combine(work, "mount"), _scratch = Path.Combine(work, "scratch"), _wim = Path.Combine(work, "install.wim");
        readonly HashSet<string> _removed = new(), _failed = new();
        int _step;

        /// <summary>True while DISM is partway through mounting or saving, when stopping it can leave a half-done mount.</summary>
        public bool Interrupted { get; private set; }

        void Step(string what)
        {
            cancel.ThrowIfCancellationRequested();
            onLine($"Step {++_step} of {Steps}: {what}");
        }

        string Scratch => $"/ScratchDir:\"{_scratch}\"";

        public async Task<JobResult> RunAsync()
        {
            Directory.CreateDirectory(_tree);
            Directory.CreateDirectory(_mount);
            Directory.CreateDirectory(_scratch);
            using (var iso = AttachedIso.Open(plan.SourceIso))
            {
                Step("Copying the setup files");
                await Robocopy(iso.Root, _tree, "/E /XF install.wim install.esd", cancel, onLine);
                Step($"Copying {plan.Edition.Name} out of the ISO");
                await Dism($"/Export-Image /SourceImageFile:\"{Path.Combine(iso.Root, media.ImageFile)}\" /SourceIndex:{plan.Edition.Index} /DestinationImageFile:\"{_wim}\" /Compress:max {Scratch}",
                    "Copying the edition");
            }

            // The journal already covers this folder, so a crash from here on is cleaned up too.
            Step("Mounting the image");
            Interrupted = true;
            await Dism($"/Mount-Image /ImageFile:\"{_wim}\" /Index:1 /MountDir:\"{_mount}\"", "Mounting the image");
            Interrupted = false;

            Step($"Removing {plan.Remove.Count} items");
            await RemoveApps();
            await RemoveCapabilities();
            await RemoveFeatures();
            await RemoveFiles();

            Step(plan.CleanComponents ? "Cleaning up the component store. This is the slowest step" : "Skipping the component store cleanup");
            if (plan.CleanComponents) await CleanComponents();

            // Stopping this one could leave the image half-written. It's quick, and Stop takes effect straight after.
            Step("Saving the changes");
            Interrupted = true;
            await Dism($"/Unmount-Image /MountDir:\"{_mount}\" /Commit", "Saving the changes", canStop: false);
            Interrupted = false;

            // Committing only adds to the WIM, so the space isn't back until it's copied into a new one.
            Step("Compressing the image");
            await Dism($"/Export-Image /SourceImageFile:\"{_wim}\" /SourceIndex:1 /DestinationImageFile:\"{Path.Combine(_tree, "sources", "install.wim")}\" /Compress:max {Scratch}",
                "Compressing the image");

            AddSetupFiles();
            Step("Making the ISO");
            await MakeIsoAsync(_tree, partial, media.Label, onLine, cancel);
            File.Move(partial, plan.OutputIso, overwrite: true);
            return Summary();
        }

        void AddSetupFiles()
        {
            var config = Path.Combine(_tree, "sources", "ei.cfg");
            if (!File.Exists(config) && !File.Exists(Path.Combine(_tree, "sources", "pid.txt")))
                File.WriteAllText(config, EditionConfig(plan.Edition.EditionId));
            if (plan.LocalAccount == null) return;
            File.WriteAllText(Path.Combine(_tree, "autounattend.xml"), AnswerFile(plan.LocalAccount, plan.Edition.Arch), new UTF8Encoding(false));
            onLine($"Added an answer file with the local account {plan.LocalAccount}");
        }

        JobResult Summary()
        {
            var size = Fmt.Bytes(new FileInfo(plan.OutputIso).Length);
            var missing = plan.Remove.Count(r => !_removed.Contains(r.Title) && !_failed.Contains(r.Title));
            var text = $"Your ISO is ready: {plan.OutputIso} ({size}, was {Fmt.Bytes(media.IsoBytes)}). Removed {_removed.Count} of {plan.Remove.Count} items.";
            if (missing > 0) text += $" {missing} weren't in this image.";
            return _failed.Count == 0
                ? new(JobLevel.Good, text)
                : new(JobLevel.Warn, $"{text} These couldn't be removed and are still there: {string.Join(", ", _failed)}.");
        }

        /// <summary>Returns what DISM printed, line by line. Throws if it fails, so that the build stops there.</summary>
        async Task<string> Dism(string args, string doing, bool canStop = true, bool show = true)
        {
            var text = new StringBuilder();
            var log = new RepairJobs.Log(line =>
            {
                lock (text) text.AppendLine(line);
                if (show) onLine(line);
            });
            int code = await Cmd.StreamAsync(Cmd.System("dism.exe"), $"{args} /English", log.Add, canStop ? cancel : default);
            if (code == Cmd.Cancelled) throw new OperationCanceledException();
            if (code is not (0 or 3010)) throw new IsoProblem($"{doing} failed. {RepairJobs.DismReason(code, log.Flat())}");
            lock (text) return text.ToString();
        }

        string OnImage => $"/Image:\"{_mount}\"";

        /// <summary>One thing the user ticked. A failure here is reported at the end instead of throwing away a whole build.</summary>
        async Task Remove(IsoRemovable item, string args)
        {
            onLine($"Removing {item.Title}");
            try
            {
                await Dism(args, $"Removing {item.Title}", show: false);
                _removed.Add(item.Title);
            }
            catch (IsoProblem ex)
            {
                onLine($"    {ex.Message}");
                _failed.Add(item.Title);
            }
        }

        async Task RemoveApps()
        {
            var apps = plan.Remove.Where(r => r.Kind == IsoRemoveKind.App).ToList();
            if (apps.Count == 0) return;
            var packages = ParseProvisioned(await Dism($"{OnImage} /Get-ProvisionedAppxPackages", "Reading the app list", show: false));
            foreach (var app in apps)
                foreach (var package in packages.Where(p => p.StartsWith(app.Id + "_", StringComparison.OrdinalIgnoreCase)))
                    await Remove(app, $"{OnImage} /Remove-ProvisionedAppxPackage /PackageName:{package} {Scratch}");
        }

        async Task RemoveCapabilities()
        {
            var items = plan.Remove.Where(r => r.Kind == IsoRemoveKind.Capability).ToList();
            if (items.Count == 0) return;
            var installed = ParseInstalledCapabilities(await Dism($"{OnImage} /Get-Capabilities", "Reading the capability list", show: false));
            foreach (var item in items)
                foreach (var capability in installed.Where(c => CapabilityMatches(c, item.Id)))
                    await Remove(item, $"{OnImage} /Remove-Capability /CapabilityName:{capability} {Scratch}");
        }

        async Task RemoveFeatures()
        {
            var items = plan.Remove.Where(r => r.Kind == IsoRemoveKind.Feature).ToList();
            if (items.Count == 0) return;
            var present = ParseFeatures(await Dism($"{OnImage} /Get-Features /Format:Table", "Reading the feature list", show: false));
            foreach (var item in items.Where(i => present.Contains(i.Id)))
                await Remove(item, $"{OnImage} /Disable-Feature /FeatureName:{item.Id} /Remove {Scratch}");
        }

        /// <summary>These files belong to TrustedInstaller, so even an administrator has to take them over first.</summary>
        async Task RemoveFiles()
        {
            foreach (var item in plan.Remove.Where(r => r.Files != null))
                foreach (var relative in item.Files!)
                {
                    var file = Path.Combine(_mount, relative);
                    if (!File.Exists(file)) continue;
                    onLine($"Removing {relative}");
                    var log = new RepairJobs.Log(_ => { });
                    await Cmd.StreamAsync(Cmd.System("takeown.exe"), $"/f \"{file}\" /a", log.Add, cancel);
                    await Cmd.StreamAsync(Cmd.System("icacls.exe"), $"\"{file}\" /grant *S-1-5-32-544:F", log.Add, cancel);
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        File.Delete(file);
                        _removed.Add(item.Title);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        onLine($"    {ex.Message}");
                        _failed.Add(item.Title);
                    }
                }
        }

        /// <summary>Saves space but isn't essential: an image that can't be cleaned is still a good image, so the build carries on.</summary>
        async Task CleanComponents()
        {
            try
            {
                await Dism($"{OnImage} /Cleanup-Image /StartComponentCleanup /ResetBase {Scratch}", "Cleaning up the component store");
            }
            catch (IsoProblem ex)
            {
                onLine($"Skipped the cleanup, so the ISO will be a little larger. {ex.Message}");
            }
        }
    }

    /// <summary><c>what</c> comes straight after the folders because file names have to. Robocopy's exit code is a set of flags, where anything under 8 means the copy worked.</summary>
    static async Task Robocopy(string from, string to, string what, CancellationToken cancel, Action<string> onLine)
    {
        // A trailing backslash in quotes would escape the quote, so a drive root stays bare.
        var source = from.Length <= 3 ? from : $"\"{from}\"";
        var destination = to.Length <= 3 ? to : $"\"{to}\"";
        int code = await Cmd.StreamAsync(Cmd.System("robocopy.exe"), $"{source} {destination} {what} /COPY:DT /DCOPY:T /A-:RSH /R:1 /W:1 /NFL /NDL /NJH /NJS /NP", onLine, cancel);
        if (code == Cmd.Cancelled) throw new OperationCanceledException();
        if (code is < 0 or >= 8) throw new IsoProblem("Copying the setup files failed. The output shows which file stopped it.");
    }

    // ---- Making the ISO ----

    /// <summary>oscdimg ships with the Windows ADK, so it's often not there. Windows' own IMAPI2 is the fallback, and it always is.</summary>
    public static string? FindOscdimg()
    {
        var arch = RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "arm64", Architecture.X64 => "amd64", _ => "x86" };
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "oscdimg.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim().Trim('"'), "oscdimg.exe"));
        var kits = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10") };
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots"))
            if (key?.GetValue("KitsRoot10") is string root) kits.Insert(0, root);
        foreach (var root in kits)
            candidates.Add(Path.Combine(root, "Assessment and Deployment Kit", "Deployment Tools", arch, "Oscdimg", "oscdimg.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>A label both tools accept. Windows doesn't use it, so nothing depends on it surviving exactly.</summary>
    public static string IsoLabel(string label)
    {
        var clean = Regex.Replace(label, "[^A-Za-z0-9_]", "_");
        clean = clean.Length > 32 ? clean[..32] : clean;
        return clean.Trim('_').Length == 0 ? "WINDOWS11" : clean;
    }

    /// <summary>Makes a bootable ISO from a folder with Windows' layout: UEFI always, BIOS too when the media has the BIOS boot sector.</summary>
    public static async Task MakeIsoAsync(string tree, string output, string label, Action<string> onLine, CancellationToken cancel)
    {
        label = IsoLabel(label);
        var bios = Path.Combine(tree, "boot", "etfsboot.com");
        var uefi = Path.Combine(tree, "efi", "microsoft", "boot", "efisys.bin");
        if (!File.Exists(uefi)) throw new IsoProblem("The UEFI boot file is missing, so this ISO couldn't boot.");
        if (FindOscdimg() is { } oscdimg)
        {
            onLine($"Using {oscdimg}");
            var boot = File.Exists(bios) ? $"2#p0,e,b\"{bios}\"#pEF,e,b\"{uefi}\"" : $"1#pEF,e,b\"{uefi}\"";
            int code = await Cmd.StreamAsync(oscdimg, $"-m -o -u2 -udfver102 -l{label} -bootdata:{boot} \"{tree}\" \"{output}\"", onLine, cancel);
            if (code == Cmd.Cancelled) throw new OperationCanceledException();
            if (code == 0) return;
            onLine($"oscdimg stopped with code {code}, so trying Windows' built-in image writer instead.");
            TryDelete(output);
        }
        else onLine("Using Windows' built-in image writer. oscdimg isn't installed, and isn't needed.");
        await Task.Run(() => WriteImapi(tree, output, label, File.Exists(bios) ? bios : null, uefi, onLine, cancel), cancel);
    }

    /// <summary>
    /// Windows' own disc image writer. UDF only, like Microsoft's media: install.wim is usually over 4 GB, which ISO 9660 can't
    /// hold, and IMAPI2 refuses outright if asked to write both. <c>Emulation</c> must be set after <c>AssignBootImage</c>, which
    /// otherwise treats a 1.44 MB file as a floppy disk.
    /// </summary>
    static void WriteImapi(string tree, string output, string label, string? bios, string uefi, Action<string> onLine, CancellationToken cancel)
    {
        const int UdfOnly = 4, Udf102 = 0x102, PlatformBios = 0, PlatformUefi = 0xEF, NoEmulation = 0;
        const uint StgmRead = 0, StgmShareDenyWrite = 0x20;
        var made = new List<object>();
        T Keep<T>(T item) where T : notnull
        {
            made.Add(item);
            return item;
        }
        object Boot(string file, int platform)
        {
            SHCreateStreamOnFileEx(file, StgmRead | StgmShareDenyWrite, 0, false, IntPtr.Zero, out var stream);
            Keep(stream);
            dynamic options = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.BootOptions")!)!);
            options.Manufacturer = "Microsoft";
            options.PlatformId = platform;
            options.AssignBootImage(new UnknownWrapper(stream));
            options.Emulation = NoEmulation;
            return options;
        }
        try
        {
            dynamic image = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")!)!);
            image.FileSystemsToCreate = UdfOnly;
            image.UDFRevision = Udf102;
            image.FreeMediaBlocks = 0;      // No size limit.
            image.VolumeName = label;
            dynamic root = Keep<object>(image.Root);
            root.AddTree(tree, false);
            image.BootImageOptionsArray = bios == null
                ? new object[] { Boot(uefi, PlatformUefi) }
                : new object[] { Boot(bios, PlatformBios), Boot(uefi, PlatformUefi) };
            dynamic result = Keep<object>(image.CreateResultImage());
            long total = (long)result.TotalBlocks * result.BlockSize, written = 0;
            var stream = Keep((ComStream)result.ImageStream);
            using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buffer = new byte[1 << 20];
            var count = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                int shown = -1;
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    stream.Read(buffer, buffer.Length, count);
                    int read = Marshal.ReadInt32(count);
                    if (read <= 0) break;
                    file.Write(buffer, 0, read);
                    written += read;
                    int percent = (int)(written * 100 / Math.Max(total, 1));
                    if (percent != shown) onLine($"Writing the ISO: {shown = percent}%");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(count);
            }
            if (written != total) throw new IsoProblem($"The ISO came out short: {Fmt.Bytes(written)} of {Fmt.Bytes(total)}.");
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new IsoProblem($"Windows couldn't write the ISO: {ex.Message}");
        }
        finally
        {
            // IMAPI2 keeps the source files open until it's released, and they're about to be deleted.
            for (int i = made.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(made[i])) Marshal.FinalReleaseComObject(made[i]);
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out ComStream stream);

    // ---- Cleaning up ----

    /// <summary>A journal left by a build whose process and watchdog are both gone, or null. When this isn't null, there's something to clean.</summary>
    public static string? Leftover() => Journal.Read() is { Alive: false } journal ? journal.Work : null;

    /// <summary>Cleans up what an earlier build left. Safe to call at any time; true when nothing is left.</summary>
    public static async Task<bool> RecoverAsync(Action<string> onLine)
    {
        if (Journal.Read() is not { Alive: false } journal) return true;
        Log($"Recovering {journal.Work}");
        bool released = await ReleaseAsync(journal.Work, line =>
        {
            Log(line);
            onLine(line);
        });
        Log(released ? "Recovered" : "Recovery failed");
        return released;
    }

    /// <summary>
    /// Runs in a second Tman process for as long as a build does. If the build ends normally its journal goes, and so does this.
    /// If Tman goes first, by any means, this does the cleanup the build couldn't.
    /// </summary>
    public static async Task WatchAsync(int ownerId)
    {
        Process? owner = null;
        try { owner = Process.GetProcessById(ownerId); }
        catch (ArgumentException) { }
        using (owner)
        {
            while (owner != null && !owner.WaitForExit(1000))
                if (!Journal.Exists) return;
        }
        if (Journal.Read() is not { } journal || journal.OwnerId != ownerId) return;
        Log($"Tman closed during a build. Cleaning up {journal.Work}");
        bool released = await ReleaseAsync(journal.Work, Log);
        Log(released ? "Cleaned up" : "Cleanup failed. The next start of Tman tries again");
    }

    /// <summary>
    /// Discards the mount, then deletes the work folder. The folder stays until the mount has really gone: deleting into a live
    /// mount would wreck it. <c>interrupted</c> says a mount may be half done; anything that can't know has to assume so.
    /// </summary>
    static async Task<bool> ReleaseAsync(string work, Action<string> onLine, bool interrupted = true)
    {
        StopStrays(work);
        if (!await DiscardMountAsync(Path.Combine(work, "mount"), onLine, interrupted)) return false;
        var log = new RepairJobs.Log(onLine);
        await Cmd.StreamAsync(Cmd.System("cmd.exe"), $"/d /s /c \"rd /s /q \"{work}\"\"", log.Add);
        if (Directory.Exists(work)) onLine($"Couldn't delete all of {work}. It's safe to delete it yourself.");
        try { Directory.Delete(Path.GetDirectoryName(work)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Journal.End();
        return true;
    }

    /// <summary>Stopped tools don't always die with Tman, and one still running would hold the image open.</summary>
    static void StopStrays(string work)
    {
        var tools = Wmi.Query("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='dism.exe' OR Name='oscdimg.exe' OR Name='robocopy.exe'");
        foreach (var tool in tools.Where(t => t.Str("CommandLine").Contains(work, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var process = Process.GetProcessById((int)tool.U64("ProcessId"));
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
    }

    static async Task<bool> DiscardMountAsync(string mount, Action<string> onLine, bool interrupted)
    {
        const int Attempts = 4;
        var dism = Cmd.System("dism.exe");
        bool stuck = false;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var status = await MountStatusAsync(mount);
            if (status == null) break;
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            onLine("Discarding the mounted image");
            // After a restart Windows needs it remounted before it can be let go.
            if (status.Contains("Remount", StringComparison.OrdinalIgnoreCase))
                await Cmd.StreamAsync(dism, $"/Remount-Image /MountDir:\"{mount}\" /English", onLine);
            stuck = await Cmd.StreamAsync(dism, $"/Unmount-Image /MountDir:\"{mount}\" /Discard /English", onLine) != 0;
        }
        // This scans the whole drive, which can take minutes, so a mount that let go cleanly skips it. One stopped
        // partway may never have reached the list above, so that has to go by what the build was doing at the time.
        if (interrupted || stuck) await Cmd.StreamAsync(dism, "/Cleanup-Wim /English", onLine);
        return await MountStatusAsync(mount) == null;
    }

    /// <summary>Null when the folder isn't mounted. "Unknown" when DISM won't say, which has to count as mounted.</summary>
    static async Task<string?> MountStatusAsync(string mount)
    {
        var text = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        int code = await Cmd.StreamAsync(Cmd.System("dism.exe"), "/Get-MountedImageInfo /English", line => { lock (text) text.AppendLine(line); }, timeout.Token);
        if (code != 0) return "Unknown";
        lock (text) return ParseMountStatus(text.ToString(), mount);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static readonly string CleanupLog = Path.Combine(Path.GetDirectoryName(ErrorLog.FilePath)!, "iso-cleanup.log");

    /// <summary>The watchdog has no window, so this is the only record of what it did.</summary>
    static void Log(string line)
    {
        try { File.AppendAllText(CleanupLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Which build is running, kept on disk so that it outlives Tman. The file exists only while there's a build, or a mess from one.</summary>
    sealed record Journal(string Work, int OwnerId, long OwnerStart, int WatchdogId, long WatchdogStart)
    {
        static readonly string FilePath = Path.Combine(Path.GetDirectoryName(ErrorLog.FilePath)!, "iso-build.json");

        // This file is in the user's profile but steers an elevated rd /s, so it's only believed if it names a work folder and nothing else.
        static readonly Regex ValidWork = new($@"^[A-Za-z]:\\{WorkFolderName}\\\d{{8}}-\d{{6}}$");

        [JsonIgnore]
        public bool Alive => IsRunning(OwnerId, OwnerStart) || IsRunning(WatchdogId, WatchdogStart);

        public static bool Exists => File.Exists(FilePath);

        public static Journal? Read()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(FilePath));
                if (journal != null && ValidWork.IsMatch(journal.Work)) return journal;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
            TryDelete(FilePath);
            return null;
        }

        public static void Begin(string work)
        {
            using var self = Process.GetCurrentProcess();
            var journal = new Journal(work, self.Id, StartOf(self), 0, 0);
            Save(journal);
            var watchdog = StartWatchdog();
            if (watchdog != null)
                using (watchdog) Save(journal with { WatchdogId = watchdog.Id, WatchdogStart = StartOf(watchdog) });
        }

        public static void End() => TryDelete(FilePath);

        static void Save(Journal journal)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Swapped in whole, because the watchdog can read this at any moment.
            var next = FilePath + ".new";
            File.WriteAllText(next, JsonSerializer.Serialize(journal));
            File.Move(next, FilePath, overwrite: true);
        }

        static Process? StartWatchdog()
        {
            // Only Tman.exe can do this, which rules out a test host starting copies of itself.
            if (Environment.ProcessPath is not { } exe || !Path.GetFileName(exe).Equals("Tman.exe", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                return Process.Start(new ProcessStartInfo(exe, $"{WatchdogFlag} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                ErrorLog.Write(ex);
                return null;
            }
        }

        static long StartOf(Process process) => process.StartTime.ToUniversalTime().Ticks;

        /// <summary>The start time guards against Windows reusing the process ID.</summary>
        static bool IsRunning(int id, long start)
        {
            if (id == 0) return false;
            try
            {
                using var process = Process.GetProcessById(id);
                try { return !process.HasExited && StartOf(process) == start; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return true; } // A process Tman can't inspect is running.
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    // ---- USB ----

    const int BusUsb = 7;
    const long MinUsbBytes = 8 * Gb;

    /// <summary>Windows' format tool won't make a FAT32 volume over 32 GB, and only FAT32 boots on every PC, so a bigger drive gets a partition this size.</summary>
    const int Fat32PartitionMb = 32000;

    /// <summary>FAT32 can't hold a file of 4 GB or more.</summary>
    const long Fat32MaxFile = 4 * Gb - 1;

    /// <summary>Every disk Windows sees, for checking against. Blocks.</summary>
    public static List<UsbDisk> ListDisks()
    {
        const string scope = @"root\Microsoft\Windows\Storage";
        var letters = Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", scope)
            .Select(p => (Disk: (int)p.U64("DiskNumber"), Letter: (char)p.U64("DriveLetter")))
            .Where(p => char.IsAsciiLetter(p.Letter))
            .ToLookup(p => p.Disk, p => char.ToUpperInvariant(p.Letter));
        return Wmi.Query("SELECT Number, FriendlyName, Size, BusType, IsSystem, IsBoot, IsReadOnly, IsOffline, UniqueId FROM MSFT_Disk", scope)
            .Select(d => new UsbDisk((int)d.U64("Number"), d.Str("FriendlyName"), (long)d.U64("Size"), d.U64("BusType") == BusUsb,
                d.Bool("IsSystem") == true, d.Bool("IsBoot") == true, d.Bool("IsReadOnly") == true, d.Bool("IsOffline") == true,
                d.Str("UniqueId"), letters[(int)d.U64("Number")].OrderBy(l => l).ToList()))
            .OrderBy(d => d.Number)
            .ToList();
    }

    /// <summary>
    /// Why this disk mustn't be erased, or null. Windows' own drive is caught by several checks on purpose: nothing here may ever
    /// reach it. <c>keep</c> is drive letters that have to survive, such as the one holding the ISO.
    /// </summary>
    public static string? UsbRefusal(UsbDisk disk, char windowsDrive, IEnumerable<char> keep)
    {
        if (disk.IsSystem || disk.IsBoot || disk.Letters.Contains(char.ToUpperInvariant(windowsDrive)))
            return $"{disk.Title} is the drive Windows runs from. Tman won't erase it.";
        if (!disk.IsUsb) return $"{disk.Title} isn't a USB drive. Tman only writes to USB drives.";
        if (disk.IsReadOnly) return $"{disk.Title} is write-protected.";
        if (disk.IsOffline) return $"{disk.Title} is offline.";
        if (disk.Size < MinUsbBytes) return $"{disk.Title} is too small. Windows needs at least {Fmt.Bytes(MinUsbBytes)}.";
        var kept = disk.Letters.Intersect(keep.Select(char.ToUpperInvariant)).ToList();
        return kept.Count > 0 ? $"{disk.Title} holds a file this job needs, on {kept[0]}:. Move the ISO to another drive first." : null;
    }

    /// <summary>The partitions on the drive, so the confirmation can say what's about to go.</summary>
    public static string DescribeContents(UsbDisk disk)
    {
        var parts = new List<string>();
        foreach (var letter in disk.Letters)
        {
            try
            {
                var drive = new DriveInfo(letter.ToString());
                if (!drive.IsReady) { parts.Add($"{letter}:"); continue; }
                var name = drive.VolumeLabel.Length > 0 ? $"{letter}: {drive.VolumeLabel}" : $"{letter}:";
                parts.Add($"{name} ({Fmt.Bytes(drive.TotalSize - drive.TotalFreeSpace)} used of {Fmt.Bytes(drive.TotalSize)})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { parts.Add($"{letter}:"); }
        }
        return parts.Count == 0 ? "No drive letters" : string.Join(", ", parts);
    }

    public static string DiskpartScript(int number, long size, char letter)
    {
        var limit = size > Fat32PartitionMb * 1024L * 1024 ? $" size={Fat32PartitionMb}" : "";
        return $"""
            select disk {number}
            attributes disk clear readonly
            clean
            convert mbr
            create partition primary{limit}
            format fs=fat32 quick label="WIN11"
            active
            assign letter={letter}
            exit

            """;
    }

    /// <summary>Erases the disk and makes it bootable from the ISO. The caller has confirmed with the user. Never throws.</summary>
    public static async Task<JobResult> WriteUsbAsync(UsbDisk chosen, string isoPath, Action<string> onLine, CancellationToken cancel)
    {
        bool erased = false;
        var script = "";
        try
        {
            var windowsDrive = Environment.SystemDirectory[0];
            var keep = new[] { Path.GetFullPath(isoPath)[0] };
            // Checked again now, because the number may belong to another drive if sticks were swapped since the list was read.
            var disk = (await Task.Run(ListDisks, cancel)).FirstOrDefault(d => d.Number == chosen.Number);
            if (disk == null || disk.Id != chosen.Id || disk.Size != chosen.Size)
                return new(JobLevel.Bad, $"{chosen.Title} has changed or gone since the list was read. Nothing was erased. Refresh the list and try again.");
            if (UsbRefusal(disk, windowsDrive, keep) is { } refused) return new(JobLevel.Bad, refused);

            using var iso = AttachedIso.Open(isoPath);
            var files = Directory.EnumerateFiles(iso.Root, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
            var wim = Path.Combine(iso.Root, "sources", "install.wim");
            var tooBig = files.FirstOrDefault(f => f.Length > Fat32MaxFile && !f.FullName.Equals(wim, StringComparison.OrdinalIgnoreCase));
            if (tooBig != null) return new(JobLevel.Bad, $"{tooBig.Name} is too big for a USB drive that every PC can boot. Nothing was erased.");
            long needed = files.Sum(f => f.Length);
            if (needed > Math.Min(disk.Size, Fat32PartitionMb * 1024L * 1024))
                return new(JobLevel.Bad, $"This ISO needs {Fmt.Bytes(needed)}, which is more than a bootable partition on this drive can hold. Nothing was erased.");

            var letter = Enumerable.Range('D', 23).Select(c => (char)c).Reverse().First(c => !DriveInfo.GetDrives().Any(d => d.Name[0] == c));
            var root = $@"{letter}:\";
            onLine($"Erasing {disk.Title}");
            script = Path.GetTempFileName();
            File.WriteAllText(script, DiskpartScript(disk.Number, disk.Size, letter));
            erased = true;
            int code = await Cmd.StreamAsync(Cmd.System("diskpart.exe"), $"/s \"{script}\"", onLine, cancel);
            if (code == Cmd.Cancelled) throw new OperationCanceledException();
            if (code != 0) return new(JobLevel.Bad, $"Windows couldn't prepare the drive (diskpart code {code}). Close anything using it and try again. The drive may need formatting before it's usable.");

            onLine("Copying the setup files");
            await Robocopy(iso.Root, root, "/E /XF install.wim", cancel, onLine);
            if (File.Exists(wim) && new FileInfo(wim).Length > Fat32MaxFile)
            {
                onLine("Splitting the Windows image to fit on the drive");
                var log = new RepairJobs.Log(onLine);
                code = await Cmd.StreamAsync(Cmd.System("dism.exe"), $"/Split-Image /ImageFile:\"{wim}\" /SWMFile:\"{root}sources\\install.swm\" /FileSize:3800 /English", log.Add, cancel);
                if (code == Cmd.Cancelled) throw new OperationCanceledException();
                if (code != 0) throw new IsoProblem($"Splitting the image failed. {RepairJobs.DismReason(code, log.Flat())}");
            }
            else if (File.Exists(wim))
            {
                onLine("Copying the Windows image");
                await Robocopy(Path.Combine(iso.Root, "sources"), root + "sources", "install.wim", cancel, onLine);
            }

            await AddBiosBootAsync(iso.Root, letter, onLine, cancel);
            Flush(letter);
            return new(JobLevel.Good, $"{disk.Title} is ready on {letter}:. Use Safely Remove Hardware before you unplug it, then boot from it to install Windows.");
        }
        catch (OperationCanceledException)
        {
            return new(JobLevel.Warn, erased ? "Stopped. The drive was already erased and only part of Windows was copied, so it won't boot. Format it to use it again." : "Stopped before anything was erased.");
        }
        catch (IsoProblem ex)
        {
            return new(JobLevel.Bad, erased ? $"{ex.Message} The drive won't boot. Format it to use it again." : ex.Message);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            return new(JobLevel.Bad, $"Writing the drive failed: {ex.Message}");
        }
        finally
        {
            if (script.Length > 0) TryDelete(script);
        }
    }

    /// <summary>Only older firmware needs this, and UEFI boots without it, so a failure is mentioned but doesn't fail the job.</summary>
    static async Task AddBiosBootAsync(string isoRoot, char letter, Action<string> onLine, CancellationToken cancel)
    {
        var bootsect = Path.Combine(isoRoot, "boot", "bootsect.exe");
        if (!File.Exists(bootsect)) return;
        int code = await Cmd.StreamAsync(bootsect, $"/nt60 {letter}: /force /mbr", onLine, cancel);
        if (code == Cmd.Cancelled) throw new OperationCanceledException();
        if (code != 0) onLine("Couldn't add the boot code older PCs need. UEFI PCs can still boot from the drive.");
    }

    /// <summary>Some drives cache writes, and a drive pulled before they land won't boot.</summary>
    static void Flush(char letter)
    {
        try
        {
            using var volume = File.OpenHandle($@"\\.\{letter}:", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            RandomAccess.FlushToDisk(volume);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// An ISO attached read-only with a drive letter of its own. Windows ties the attachment to this handle, so it detaches however
/// Tman ends. <c>Mount-DiskImage</c> can't offer that: its mounts stay until something dismounts them.
/// </summary>
public sealed class AttachedIso : IDisposable
{
    const uint DeviceIso = 1, AccessRead = 0xD0000;
    const int AttachReadOnly = 1, DetachNone = 0;
    static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    readonly SafeFileHandle _disk;

    public string Root { get; }

    AttachedIso(SafeFileHandle disk, string root)
    {
        _disk = disk;
        Root = root;
    }

    public static AttachedIso Open(string path)
    {
        TokenPrivileges.Enable("SeManageVolumePrivilege");
        var type = new StorageType { DeviceId = DeviceIso, VendorId = VendorMicrosoft };
        int error = OpenVirtualDisk(ref type, path, AccessRead, 0, IntPtr.Zero, out var disk);
        if (error != 0) throw new IsoProblem($"Windows couldn't open the ISO: {new Win32Exception(error).Message}");
        try
        {
            error = AttachVirtualDisk(disk, IntPtr.Zero, AttachReadOnly, 0, IntPtr.Zero, IntPtr.Zero);
            if (error != 0) throw new IsoProblem($"Windows couldn't attach the ISO: {new Win32Exception(error).Message}");
            return new AttachedIso(disk, FindRoot(disk));
        }
        catch
        {
            disk.Dispose();
            throw;
        }
    }

    /// <summary>The volume takes a moment to arrive after attaching.</summary>
    static string FindRoot(SafeFileHandle disk)
    {
        var path = new StringBuilder(300);
        int size = path.Capacity * sizeof(char);
        int error = GetVirtualDiskPhysicalPath(disk, ref size, path);
        if (error != 0) throw new IsoProblem($"Windows couldn't find the attached ISO: {new Win32Exception(error).Message}");
        var number = Regex.Match(path.ToString(), @"(\d+)$").Value;
        var device = @"\Device\CdRom" + number;
        var target = new StringBuilder(300);
        for (int i = 0; i < 100; i++)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
                if (QueryDosDevice(drive.Name[..2], target, target.Capacity) > 0 && target.ToString() == device && drive.IsReady)
                    return drive.Name;
            Thread.Sleep(200);
        }
        throw new IsoProblem("Windows attached the ISO but didn't give it a drive letter.");
    }

    public void Dispose()
    {
        DetachVirtualDisk(_disk, DetachNone, 0);
        _disk.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    static extern int OpenVirtualDisk(ref StorageType type, string path, uint access, int flags, IntPtr parameters, out SafeFileHandle handle);

    [DllImport("virtdisk.dll")]
    static extern int AttachVirtualDisk(SafeFileHandle disk, IntPtr securityDescriptor, int flags, uint providerFlags, IntPtr parameters, IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    static extern int DetachVirtualDisk(SafeFileHandle disk, int flags, uint providerFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    static extern int GetVirtualDiskPhysicalPath(SafeFileHandle disk, ref int size, StringBuilder path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int QueryDosDevice(string name, StringBuilder target, int size);
}
