using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace Tman.Core;

public enum CleanupKind { Files, RecycleBin, UpdateCache }

/// <summary>A fixed place files are cleaned from. Files newer than <c>MinAge</c> are left alone, as if they weren't there.</summary>
public sealed record CleanupFolder(string Path, string Pattern = "*", bool Recurse = true, TimeSpan MinAge = default);

public sealed record CleanupCategory(string Title, string Hint, bool NeedsAdmin, CleanupKind Kind, IReadOnlyList<CleanupFolder> Folders);

/// <summary><c>Denied</c> means part of it couldn't be read, so <c>Bytes</c> is a lower bound.</summary>
public sealed record CleanupSize(long Bytes, long Files, bool Denied);

public sealed record CleanupOutcome(long Freed, long Skipped, long SkippedFiles, string? Note = null);

/// <summary>
/// Measures and empties the fixed cleanup locations. Everything it touches comes from the three root folders and the
/// delegates passed in, so tests can point it at a throwaway folder. It never follows a junction or link out of a location.
/// </summary>
public sealed class CleanupEngine
{
    static readonly TimeSpan Day = TimeSpan.FromDays(1);

    static readonly EnumerationOptions Listing = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint, // includes hidden and system files; never links or cloud placeholders
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = false,
    };

    readonly Func<(long Bytes, long Items)> _queryBin;
    readonly Action _emptyBin;
    readonly Func<Action?> _pauseUpdates;

    public IReadOnlyList<CleanupCategory> Categories { get; }

    /// <param name="queryBin">Reads the Recycle Bin's size and item count.</param>
    /// <param name="emptyBin">Empties the Recycle Bin.</param>
    /// <param name="pauseUpdates">Stops Windows Update and returns what starts it again, or null if it wouldn't stop.</param>
    public CleanupEngine(string localAppData, string windows, string programData,
        Func<(long Bytes, long Items)> queryBin, Action emptyBin, Func<Action?> pauseUpdates)
    {
        _queryBin = queryBin;
        _emptyBin = emptyBin;
        _pauseUpdates = pauseUpdates;
        string userWer = Path.Combine(localAppData, @"Microsoft\Windows\WER"), machineWer = Path.Combine(programData, @"Microsoft\Windows\WER");
        Categories = new CleanupCategory[]
        {
            new("User temp", "Temporary files apps left in your profile. Anything from the last 24 hours is kept, because apps may still need it.", false, CleanupKind.Files,
                new[] { new CleanupFolder(Path.Combine(localAppData, "Temp"), MinAge: Day) }),
            new("Windows temp", "Temporary files left by Windows and installers. Anything from the last 24 hours is kept.", true, CleanupKind.Files,
                new[] { new CleanupFolder(Path.Combine(windows, "Temp"), MinAge: Day) }),
            new("Recycle Bin", "Everything in the Recycle Bin, on every drive.", false, CleanupKind.RecycleBin, Array.Empty<CleanupFolder>()),
            new("Thumbnail cache", "Saved picture previews, which Windows rebuilds. File Explorer usually has some open, and those are skipped.", false, CleanupKind.Files,
                new[] { new CleanupFolder(Path.Combine(localAppData, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recurse: false) }),
            new("Crash dumps and minidumps", "Memory dumps from app and Windows crashes. Past crashes can't be analyzed without them. Windows' own dumps need admin.", false, CleanupKind.Files,
                new[]
                {
                    new CleanupFolder(Path.Combine(localAppData, "CrashDumps")),
                    new CleanupFolder(Path.Combine(windows, "Minidump")),
                    new CleanupFolder(windows, "MEMORY.DMP", Recurse: false),
                }),
            new("Windows error reports", "Problem reports saved on this PC or waiting to be sent. Reports shared by all users need admin.", false, CleanupKind.Files,
                new[]
                {
                    new CleanupFolder(Path.Combine(userWer, "ReportArchive")), new CleanupFolder(Path.Combine(userWer, "ReportQueue")), new CleanupFolder(Path.Combine(userWer, "Temp")),
                    new CleanupFolder(Path.Combine(machineWer, "ReportArchive")), new CleanupFolder(Path.Combine(machineWer, "ReportQueue")), new CleanupFolder(Path.Combine(machineWer, "Temp")),
                }),
            new("Windows Update download cache", "Downloaded update files. Updates that haven't installed yet download again. The Windows Update service stops while this runs.", true, CleanupKind.UpdateCache,
                new[] { new CleanupFolder(Path.Combine(windows, @"SoftwareDistribution\Download")) }),
            new("Delivery Optimization files", "Update files Windows keeps to share with other PCs.", true, CleanupKind.Files,
                new[] { new CleanupFolder(Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")) }),
            new("DirectX shader cache", "Compiled graphics shaders. Games rebuild them, so the first launch may stutter.", false, CleanupKind.Files,
                new[] { new CleanupFolder(Path.Combine(localAppData, "D3DSCache")) }),
        };
    }

    /// <summary>The real locations on this PC.</summary>
    public static CleanupEngine ForThisPc() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        QueryRecycleBin, EmptyRecycleBin, PauseUpdateService);

    public CleanupSize Measure(CleanupCategory category, CancellationToken cancel)
    {
        if (category.Kind == CleanupKind.RecycleBin)
        {
            var (bytes, items) = _queryBin();
            return new CleanupSize(bytes, items, false);
        }
        long total = 0, files = 0;
        bool denied = false;
        foreach (var folder in category.Folders)
            Visit(folder, f => { total += f.Length; files++; }, () => denied = true, prune: false, cancel);
        return new CleanupSize(total, files, denied);
    }

    public CleanupOutcome Clean(CleanupCategory category, CancellationToken cancel)
    {
        if (category.Kind == CleanupKind.RecycleBin) return CleanRecycleBin();
        if (category.Kind == CleanupKind.Files) return CleanFiles(category, cancel);

        var resume = _pauseUpdates();
        if (resume == null)
        {
            var size = Measure(category, cancel);
            return new CleanupOutcome(0, size.Bytes, size.Files, "The Windows Update service wouldn't stop, so nothing was removed.");
        }
        var outcome = CleanFiles(category, cancel);
        try { resume(); }
        catch { outcome = outcome with { Note = "The Windows Update service didn't start again. Windows starts it when it's next needed." }; }
        return outcome;
    }

    CleanupOutcome CleanRecycleBin()
    {
        var before = _queryBin();
        if (before.Items == 0) return new CleanupOutcome(0, 0, 0);
        try { _emptyBin(); }
        catch (Exception ex) { return new CleanupOutcome(0, before.Bytes, before.Items, $"The Recycle Bin couldn't be emptied: {ex.Message}"); }
        var after = _queryBin();
        return new CleanupOutcome(Math.Max(0, before.Bytes - after.Bytes), after.Bytes, after.Items);
    }

    CleanupOutcome CleanFiles(CleanupCategory category, CancellationToken cancel)
    {
        long freed = 0, skipped = 0, skippedFiles = 0;
        foreach (var folder in category.Folders)
            Visit(folder, file =>
            {
                long size = file.Length;
                try
                {
                    if (file.IsReadOnly) file.IsReadOnly = false;
                    file.Delete();
                    freed += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped += size;
                    skippedFiles++;
                }
            }, () => { }, prune: true, cancel);
        return new CleanupOutcome(freed, skipped, skippedFiles);
    }

    void Visit(CleanupFolder folder, Action<FileInfo> onFile, Action onDenied, bool prune, CancellationToken cancel)
    {
        // A location that didn't resolve, such as a profile folder with no path, must never turn into a relative path or a drive root.
        if (!Path.IsPathFullyQualified(folder.Path) || Path.GetPathRoot(folder.Path) == folder.Path || !Directory.Exists(folder.Path)) return;
        Visit(new DirectoryInfo(folder.Path), folder, DateTime.UtcNow - folder.MinAge, onFile, onDenied, prune, cancel);
    }

    static void Visit(DirectoryInfo dir, CleanupFolder folder, DateTime cutoff, Action<FileInfo> onFile, Action onDenied, bool prune, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        try
        {
            foreach (var file in dir.EnumerateFiles(folder.Pattern, Listing).ToList())
                if (Newest(file) <= cutoff) onFile(file);
            if (!folder.Recurse) return;
            foreach (var sub in dir.EnumerateDirectories("*", Listing).ToList())
            {
                bool old = Newest(sub) <= cutoff; // read first: emptying a folder updates its time
                Visit(sub, folder, cutoff, onFile, onDenied, prune, cancel);
                if (!prune || !old) continue;
                try { sub.Delete(recursive: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still has files in it
            }
        }
        catch (UnauthorizedAccessException) { onDenied(); }
        catch (IOException) { } // removed by someone else while we looked
    }

    static DateTime Newest(FileSystemInfo item) => item.LastWriteTimeUtc > item.CreationTimeUtc ? item.LastWriteTimeUtc : item.CreationTimeUtc;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct BinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBinW(string? root, ref BinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBinW(IntPtr window, string? root, uint flags);

    static (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new BinInfo { Size = Marshal.SizeOf<BinInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? (info.Bytes, info.Items) : (0, 0);
    }

    static void EmptyRecycleBin()
    {
        const uint noConfirmation = 1, noProgress = 2, noSound = 4;
        int hr = SHEmptyRecycleBinW(IntPtr.Zero, null, noConfirmation | noProgress | noSound);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    static Action? PauseUpdateService()
    {
        const string name = "wuauserv";
        try
        {
            using var service = new ServiceController(name);
            if (service.Status == ServiceControllerStatus.Stopped) return () => { };
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            return () =>
            {
                using var again = new ServiceController(name);
                again.Start();
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
