using System.Diagnostics.Eventing.Reader;
using System.Net;

namespace Tman.Core;

/// <summary>
/// The second way of learning which connections the filter engine refused: Windows' own audit of "Filtering Platform
/// Connection" failures, which lands in the Security log as event 5157 with the app, the addresses, the port and the
/// filter that did it. Used when the engine's net-event log can't be read (some PCs return ERROR_PATH_NOT_FOUND for it).
/// Turning the audit on is a change to the PC's audit policy, so it only happens when the user asks, and it can be
/// turned off again the same way. Everything here needs administrator rights.
/// </summary>
public static class BlockedAudit
{
    public const string Subcategory = "Filtering Platform Connection";
    const int BlockedConnection = 5157;

    /// <summary>True when failures in the subcategory are audited, false when not, null when auditpol couldn't say.</summary>
    public static bool? IsOn()
    {
        var result = Cmd.Run(Cmd.System("auditpol.exe"), $"/get /subcategory:\"{Subcategory}\" /r");
        if (!result.Ok) return null;
        foreach (var line in result.Output.Split('\n'))
        {
            if (!line.Contains(Subcategory, StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line.Split(',');
            // Machine, target, subcategory, GUID, inclusion setting, exclusion setting.
            return fields.Length > 4 && fields[4].Contains("Failure", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>Turns failure auditing of the subcategory on or off. Success auditing is left as it was. Returns why it failed, or null.</summary>
    public static string? SetOn(bool on)
    {
        var result = Cmd.Run(Cmd.System("auditpol.exe"), $"/set /subcategory:\"{Subcategory}\" /failure:{(on ? "enable" : "disable")}");
        return result.Ok ? null : result.Output.Trim();
    }

    /// <summary>Starts watching the Security log for blocked connections. The callback runs on a thread pool thread. Throws when the log can't be read.</summary>
    public static EventLogWatcher Watch(Action<WfpDrop> onDrop)
    {
        var watcher = new EventLogWatcher(new EventLogQuery("Security", PathType.LogName, $"*[System[(EventID={BlockedConnection})]]"));
        watcher.EventRecordWritten += (_, e) =>
        {
            try
            {
                using var record = e.EventRecord;
                if (record != null && Parse(record) is { } drop) onDrop(drop);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
        };
        watcher.Enabled = true;
        return watcher;
    }

    /// <summary>One 5157 event as a drop. Fields come by name from the event's data, so a changed order doesn't matter.</summary>
    public static WfpDrop? Parse(EventRecord record)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var xml = System.Xml.Linq.XDocument.Parse(record.ToXml());
        var ns = xml.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
        foreach (var d in xml.Descendants(ns + "Data"))
            if (d.Attribute("Name")?.Value is { } name) data[name] = d.Value;
        if (!data.TryGetValue("Application", out var app) || app.Length == 0) return null;
        bool inbound = data.GetValueOrDefault("Direction", "") == "%%14592";
        var remoteText = data.GetValueOrDefault(inbound ? "SourceAddress" : "DestAddress", "");
        var remotePort = int.TryParse(data.GetValueOrDefault(inbound ? "SourcePort" : "DestPort", ""), out var rp) ? rp : 0;
        var localPort = int.TryParse(data.GetValueOrDefault(inbound ? "DestPort" : "SourcePort", ""), out var lp) ? lp : 0;
        byte protocol = byte.TryParse(data.GetValueOrDefault("Protocol", ""), out var proto) ? proto : (byte)0;
        ulong filter = ulong.TryParse(data.GetValueOrDefault("FilterRTID", ""), out var f) ? f : 0;
        IPAddress.TryParse(remoteText, out var remote);
        return new WfpDrop(record.TimeCreated ?? DateTime.Now, WfpEngine.DosPath(app), remote, remotePort, localPort, protocol, filter, inbound);
    }
}
