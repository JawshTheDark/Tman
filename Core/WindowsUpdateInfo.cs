using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Tman.Core;

public sealed record UpdateHistoryItem(string Title, DateTime Date, string Result, bool Failed, int ErrorCode, string Description)
{
    public string DateText => Date.ToString("g");
    public string ErrorText => ErrorCode == 0 ? "" : WindowsUpdateInfo.Explain(ErrorCode);
    public Brush ResultBrush => (Brush)Application.Current.FindResource(Failed ? "BadBrush" : Result is "Installed" or "Removed" ? "GoodBrush" : "WarnBrush");
}

public sealed record PendingUpdate(string Title, string Kb, double Size, string Severity, string Kind, bool Downloaded, string Description)
{
    public string SizeText => Size > 0 ? Fmt.Bytes(Size) : "";
    public string DownloadedText => Downloaded ? "Yes" : "No";
}

/// <summary>Reads update history and looks for pending updates through the Windows Update COM API. Everything here blocks, so call it off the UI thread.</summary>
public static class WindowsUpdateInfo
{
    /// <summary>Newest first. History can run to thousands of entries, so only the newest are read.</summary>
    public static List<UpdateHistoryItem> ReadHistory(int max = 1000)
    {
        var list = new List<UpdateHistoryItem>();
        dynamic session = NewSession();
        try
        {
            dynamic searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total == 0) return list;
            foreach (dynamic entry in searcher.QueryHistory(0, Math.Min(total, max)))
            {
                try
                {
                    string title = (string?)entry.Title ?? "";
                    if (title.Length == 0) continue;
                    int result = entry.ResultCode, code = entry.HResult;
                    int operation = entry.Operation;
                    bool failed = result == 4;
                    // The API reports times in UTC.
                    list.Add(new UpdateHistoryItem(title, DateTime.SpecifyKind((DateTime)entry.Date, DateTimeKind.Utc).ToLocalTime(),
                        ResultName(result, operation), failed, code, (string?)entry.Description ?? ""));
                }
                catch { }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(session);
        }
        return list.OrderByDescending(h => h.Date).ToList();
    }

    static string ResultName(int result, int operation) => result switch
    {
        1 => "In progress",
        2 => operation == 2 ? "Removed" : "Installed",
        3 => "Installed with errors",
        4 => "Failed",
        5 => "Cancelled",
        _ => "Not started",
    };

    /// <summary>The last time Windows successfully looked for updates, and the last time it installed any.</summary>
    public static (DateTime? LastCheck, DateTime? LastInstall) ReadLastTimes()
    {
        DateTime? check = null, install = null;
        object? auto = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
            if (type != null)
            {
                auto = Activator.CreateInstance(type);
                dynamic results = ((dynamic)auto!).Results;
                check = Utc(results.LastSearchSuccessDate);
                install = Utc(results.LastInstallationSuccessDate);
            }
        }
        catch { }
        finally
        {
            if (auto != null) Marshal.FinalReleaseComObject(auto);
        }
        // Searches started from Settings or an app don't always reach that record, but they all log this event.
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-WindowsUpdateClient/Operational", PathType.LogName, "*[System[EventID=26]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            using var newest = reader.ReadEvent();
            if (newest?.TimeCreated is { } t && (check == null || t > check)) check = t;
        }
        catch { }
        return (check, install);
    }

    static DateTime? Utc(object? value) =>
        value is DateTime d && d.Year > 2000 ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime() : null;

    /// <summary>Asks Windows Update what's available without downloading or installing anything. Can take minutes. Throws on failure.</summary>
    public static List<PendingUpdate> SearchPending()
    {
        var list = new List<PendingUpdate>();
        dynamic session = NewSession();
        try
        {
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            foreach (dynamic u in result.Updates)
            {
                try
                {
                    string kb = "";
                    foreach (dynamic id in u.KBArticleIDs)
                    {
                        kb = "KB" + (string)id;
                        break;
                    }
                    list.Add(new PendingUpdate((string?)u.Title ?? "", kb, Convert.ToDouble(u.MaxDownloadSize), (string?)u.MsrcSeverity ?? "",
                        (int)u.Type == 2 ? "Driver" : "Software", (bool)u.IsDownloaded, (string?)u.Description ?? ""));
                }
                catch { }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(session);
        }
        return list.OrderBy(u => u.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static object NewSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("The Windows Update API isn't available on this PC.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "Tman";
        return session;
    }

    /// <summary>A plain sentence for an update error, with the code itself so it can be looked up.</summary>
    public static string Explain(int code)
    {
        var hex = $"0x{code:X8}";
        var meaning = Meaning((uint)code);
        if (meaning == null && ((uint)code & 0xFFFF0000) == 0x80070000)
            meaning = Marshal.GetPInvokeErrorMessage(code & 0xFFFF).TrimEnd('.', ' ');
        return meaning == null ? $"{hex}: Windows gives no plain meaning for this code. Try searching for it online." : $"{hex}: {meaning}";
    }

    static string? Meaning(uint code) => code switch
    {
        0x80004004 => "The operation was stopped before it finished",
        0x80004005 => "Windows reported an unspecified failure",
        0x8000FFFF => "Windows hit an unexpected internal failure",
        0x80070002 or 0x80070003 => "A file or folder the update needs is missing",
        0x80070005 => "Access was denied",
        0x8007000D => "The update's data is corrupt or unreadable",
        0x8007000E => "The PC ran out of memory",
        0x80070020 => "A file the update needs is in use by another program",
        0x80070070 => "There isn't enough free disk space",
        0x8007045B => "Windows was shutting down",
        0x80070422 => "The Windows Update service is disabled",
        0x800704C7 => "The update was cancelled",
        0x800705B4 => "The operation timed out",
        0x80070643 => "The installer failed, often because of a damaged .NET or Office setup",
        0x80070BC2 => "The update needs a restart to finish",
        0x80073701 or 0x80073712 => "Windows' component store is damaged. System repair can fix this",
        0x800B0100 or 0x800B0101 or 0x800B0109 => "The update's digital signature couldn't be trusted",
        0x800F081F or 0x800F0831 or 0x800F0906 or 0x800F0907 => "Windows couldn't find the source files it needs for the update",
        0x800F0922 => "The update failed to install, often because the system partition is too small or a VPN got in the way",
        0x800F0986 or 0x800F0988 or 0x800F0991 or 0x800F0993 => "Windows couldn't apply the update to its component store",
        0x8024000B => "The operation was cancelled",
        0x80240016 => "Another update was already installing",
        0x80240017 => "The update doesn't apply to this PC",
        0x8024001E => "The Windows Update service was shutting down",
        0x80240022 => "All the updates in the batch failed",
        0x8024002E => "Access to Windows Update is blocked by policy",
        0x80240034 => "The update failed to download",
        0x80240438 => "The PC couldn't reach Windows Update",
        0x8024200D => "The update needs more data than was downloaded and will try again",
        0x80242014 => "The update finished downloading and needs a restart to install",
        0x80242016 => "The update failed after the restart",
        0x80244018 or 0x80244019 => "The update server refused the request, often because of a proxy or firewall",
        0x8024401C or 0x80244022 or 0x8024401F => "The update server timed out or was busy",
        0x8024402C or 0x80072EE2 or 0x80072EE7 or 0x80072EFD or 0x80072EFE or 0x80072F8F => "The PC couldn't connect to the update server. Check the network, proxy and clock",
        0x80246007 or 0x80246008 => "The update hadn't finished downloading",
        0x80248007 => "Windows Update's own data is incomplete",
        0x8024A205 => "Windows Update couldn't finish because of a pending change",
        0xC1900101 => "A driver problem rolled the update back",
        0xC1900208 => "An app that isn't compatible blocked the update",
        0xC190020E => "There isn't enough free disk space for the update",
        _ => null,
    };
}
