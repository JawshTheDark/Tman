using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace Tman.Core;

/// <summary>Reads the Windows event logs newest first. Every read is capped and can be limited by time. Reads block, so call them off the UI thread.</summary>
public static class EventLogs
{
    public sealed record Result<T>(List<T> Rows, bool Capped, string? Error);

    /// <summary>Logs are in time order only in practice, so one stray old stamp (a clock fixed at boot) mustn't end a read early.</summary>
    const int OlderToStop = 100;

    // A publisher that threw once has broken metadata, and asking again for every event only wastes time.
    static readonly ConcurrentDictionary<string, byte> BrokenPublishers = new();

    /// <summary>
    /// Reads events that match an XPath condition on System (empty for all), newest first, down to <paramref name="since"/>.
    /// <paramref name="project"/> turns each record into a row, or null to skip it, and must not keep the record.
    /// Rows also arrive in batches through <paramref name="onBatch"/>, on the calling thread. Failures come back in the result's Error.
    /// </summary>
    public static Result<T> Read<T>(string log, string filter, DateTime? since, int cap, Func<EventRecord, T?> project,
        CancellationToken cancel = default, Action<List<T>>? onBatch = null) where T : class
    {
        var rows = new List<T>();
        var batch = new List<T>();
        bool capped = false;
        string? error = null;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, Query(filter, since)) { ReverseDirection = true }) { BatchSize = 256 };
            using var stop = cancel.Register(() =>
            {
                try { reader.CancelReading(); } catch { }
            });
            int older = 0;
            while (!cancel.IsCancellationRequested)
            {
                using var record = reader.ReadEvent();
                if (record == null) break;
                if (since != null && record.TimeCreated < since)
                {
                    if (++older == OlderToStop) break;
                    continue;
                }
                older = 0;
                if (rows.Count == cap)
                {
                    capped = true;
                    break;
                }
                T? row;
                try { row = project(record); }
                catch (EventLogException) { continue; }
                if (row == null) continue;
                rows.Add(row);
                batch.Add(row);
                if (onBatch != null && batch.Count >= (rows.Count <= 250 ? 250 : 1000))
                {
                    onBatch(batch);
                    batch = new List<T>();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException) { error = "Windows only lets administrators read this log."; }
        catch (EventLogNotFoundException) { error = "This log doesn't exist on this PC."; }
        catch (EventLogException ex)
        {
            if (!cancel.IsCancellationRequested) error = $"Windows couldn't read this log: {ex.Message}";
        }
        if (batch.Count > 0 && !cancel.IsCancellationRequested) onBatch?.Invoke(batch);
        return new Result<T>(rows, capped, error);
    }

    /// <summary>
    /// A plain time condition doesn't shorten a read, because the log service still scans to the oldest record for matches.
    /// So older records are asked for too: the first ones back mark the end of the range, and <see cref="Read"/> stops there.
    /// </summary>
    static string Query(string filter, DateTime? since)
    {
        if (filter.Length == 0) return "*";
        if (since == null) return $"*[System[{filter}]]";
        var bound = since.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return $"*[System[({filter}) or TimeCreated[@SystemTime<'{bound}']]]";
    }

    /// <summary>The event's text, which is the slow part of a read. When the app that wrote the event is gone, this falls back to the event's raw values.</summary>
    public static string Message(EventRecord r)
    {
        var publisher = r.ProviderName ?? "";
        if (!BrokenPublishers.ContainsKey(publisher))
        {
            try
            {
                var text = r.FormatDescription();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            catch (Exception ex) when (ex is EventLogException or InvalidOperationException)
            {
                BrokenPublishers.TryAdd(publisher, 0);
            }
        }
        var values = string.Join(", ", r.Properties.Select(p => p.Value?.ToString()?.Trim()).Where(v => !string.IsNullOrEmpty(v) && v != "System.Byte[]"));
        const string none = "Windows has no description for this event, because the app that wrote it is missing or didn't include one.";
        return values.Length > 0 ? values + Environment.NewLine + Environment.NewLine + "This is the event's raw data. " + none : none;
    }

    /// <summary>Logs this user can read that hold events and can be read newest first, in no useful order. Takes about a second.</summary>
    public static List<string> ReadableLogs()
    {
        var logs = new List<string>();
        try
        {
            var session = EventLogSession.GlobalSession;
            foreach (var name in session.GetLogNames())
            {
                try
                {
                    if (session.GetLogInformation(name, PathType.LogName).RecordCount is null or 0) continue;
                    using var config = new EventLogConfiguration(name);
                    if (config.LogType is EventLogType.Administrative or EventLogType.Operational) logs.Add(name);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or EventLogException) { }
        return logs;
    }
}
