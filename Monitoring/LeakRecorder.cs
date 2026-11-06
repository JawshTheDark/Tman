using System.Text.Json;
using Tman.Core;

namespace Tman.Monitoring;

public sealed class LeakProcess
{
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public long Created { get; set; }
    public int LastSeen { get; set; }
    public LeakSeries Commit { get; set; } = new();
    public LeakSeries Handles { get; set; } = new();
    public LeakSeries Threads { get; set; } = new();
}

public sealed class LeakApp
{
    public string Name { get; set; } = "";
    public int LastSeen { get; set; }
    public int Copies { get; set; }
    public LeakSeries Commit { get; set; } = new();
    public LeakSeries Handles { get; set; } = new();
}

public sealed class LeakTag
{
    public string Tag { get; set; } = "";
    public bool NonPaged { get; set; }
    public int LastSeen { get; set; }
    public LeakSeries Bytes { get; set; } = new();
}

/// <summary>What's kept on disk. It only means anything for one boot, because a restart clears every leak.</summary>
public sealed class LeakStore
{
    public DateTime Boot { get; set; }
    public DateTime Origin { get; set; }
    public int LastSample { get; set; }
    public int Samples { get; set; }
    public LeakSeries Commit { get; set; } = new();
    public LeakSeries CommitLimit { get; set; } = new();
    public LeakSeries AllProcesses { get; set; } = new();
    public LeakSeries Pool { get; set; } = new();
    public LeakSeries Zombies { get; set; } = new();
    public Dictionary<string, LeakProcess> Processes { get; set; } = new();
    public Dictionary<string, LeakApp> Apps { get; set; } = new();
    public Dictionary<string, LeakTag> Tags { get; set; } = new();
}

/// <summary>One line of the results, for a process, an app or a kernel pool tag.</summary>
public sealed record LeakRow
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Detail { get; init; } = "";
    public LeakVerdict Verdict { get; init; }
    public Trend Memory { get; init; } = new(LeakVerdict.Watching, 0, 0, 0, 0, 0, 0, 0, 0);
    public Trend? Handles { get; init; }
    public Trend? Threads { get; init; }
    public List<TrendPoint> Points { get; init; } = new();
}

public sealed record LeakReport(
    DateTime? Started, double WatchedHours, int Samples, List<LeakRow> Processes, List<LeakRow> Apps, List<LeakRow> Tags,
    LeakRow Commit, LeakRow Zombies, string Headline, string Detail, LeakVerdict Worst, bool HasPoolTags);

/// <summary>
/// Quietly notes memory, handles, threads and kernel pools once a minute for as long as Tman runs, because a leak only
/// shows over hours. History is saved, so restarting Tman doesn't lose it, and is dropped when Windows restarts.
/// </summary>
public sealed class LeakRecorder
{
    public static LeakRecorder Instance { get; } = new();

    /// <summary>Time between samples. Tests shorten it.</summary>
    public static TimeSpan Every { get; set; } = TimeSpan.FromMinutes(1);

    const ulong SmallestProcess = 20 * 1024 * 1024, SmallestTag = 1024 * 1024;
    const long ManyZombies = 1000;
    const int ExitedKeptSeconds = 24 * 3600;
    static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(10);

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "leak-history.json");

    readonly object _gate = new();
    LeakStore _store = new();
    byte[] _tagBuffer = new byte[256 * 1024];
    DateTime _nextSample = DateTime.MinValue, _nextSave = DateTime.UtcNow + SaveEvery;
    bool _sampling, _tagsWork, _sample;

    public void Start()
    {
        Load();
        SystemMonitor.Instance.Updated += OnUpdated;
    }

    void OnUpdated(Snapshot s)
    {
        if (_sample) return;
        var now = DateTime.UtcNow;
        if (now < _nextSample || _sampling) return;
        _nextSample = now + Every;
        _sampling = true;
        Task.Run(() =>
        {
            try
            {
                Take(s, now);
                if (now >= _nextSave)
                {
                    _nextSave = now + SaveEvery;
                    if (!MainWindow.SnapshotMode) Save();
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
            finally
            {
                _sampling = false;
            }
        });
    }

    void Take(Snapshot s, DateTime now)
    {
        var tags = MemoryMapReader.ReadAllPoolTags(ref _tagBuffer);
        lock (_gate)
        {
            var boot = now - s.Uptime;
            if (_store.Samples == 0 || Math.Abs((boot - _store.Boot).TotalMinutes) > 5)
                _store = new LeakStore { Boot = boot, Origin = now };

            int t = (int)(now - _store.Origin).TotalSeconds;
            _store.LastSample = t;
            _store.Samples++;
            _store.Commit.Add(t, (long)s.Mem.CommitTotal);
            _store.CommitLimit.Add(t, (long)s.Mem.CommitLimit);
            _store.AllProcesses.Add(t, s.Processes.Sum(p => (long)p.Commit));
            _store.Pool.Add(t, (long)s.Mem.Paged + (long)s.Mem.NonPaged);

            foreach (var p in s.Processes)
            {
                var key = $"{p.Pid}:{p.CreateTime}";
                if (!_store.Processes.TryGetValue(key, out var track))
                {
                    if (p.Commit < SmallestProcess || p.Pid == 0) continue;
                    _store.Processes[key] = track = new LeakProcess { Name = p.Name, Pid = p.Pid, Created = p.CreateTime };
                }
                track.LastSeen = t;
                track.Commit.Add(t, (long)p.Commit);
                track.Handles.Add(t, p.Handles);
                track.Threads.Add(t, p.Threads);
            }

            foreach (var group in s.Processes.Where(p => p.Pid != 0 && p.Name.Length > 0).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                long commit = group.Sum(p => (long)p.Commit);
                if (!_store.Apps.TryGetValue(group.Key, out var app))
                {
                    if ((ulong)commit < SmallestProcess) continue;
                    _store.Apps[group.Key] = app = new LeakApp { Name = group.Key };
                }
                app.LastSeen = t;
                app.Copies = group.Count();
                app.Commit.Add(t, commit);
                app.Handles.Add(t, group.Sum(p => (long)p.Handles));
            }

            if (tags?.FirstOrDefault(tag => tag.Tag == ZombieCheck.ProcessTag) is { } process)
                _store.Zombies.Add(t, Math.Max(0, process.Blocks - s.Processes.Count));

            _tagsWork = tags != null;
            foreach (var tag in tags ?? new List<PoolTagUse>())
            {
                void Note(ulong bytes, bool nonPaged)
                {
                    var key = (nonPaged ? "N:" : "P:") + tag.Tag;
                    if (!_store.Tags.TryGetValue(key, out var track))
                    {
                        if (bytes < SmallestTag) return;
                        _store.Tags[key] = track = new LeakTag { Tag = tag.Tag, NonPaged = nonPaged };
                    }
                    track.LastSeen = t;
                    track.Bytes.Add(t, (long)bytes);
                }
                Note(tag.Paged, false);
                Note(tag.NonPaged, true);
            }

            foreach (var gone in _store.Processes.Where(e => t - e.Value.LastSeen > ExitedKeptSeconds).Select(e => e.Key).ToList()) _store.Processes.Remove(gone);
        }
    }

    public void Reset()
    {
        lock (_gate) _store = new LeakStore();
        _nextSample = DateTime.MinValue;
        if (!MainWindow.SnapshotMode) Save();
    }

    /// <summary>Works out the results for the last so many hours, or the whole history when null.</summary>
    public LeakReport Build(double? hours)
    {
        lock (_gate)
        {
            var s = _store;
            int from = hours is { } h ? (int)(s.LastSample - h * 3600) : 0;
            var commitPoints = s.Commit.Points(from);
            var commit = new LeakRow { Name = "Total commit", Memory = LeakAnalysis.Measure(commitPoints, LeakLimits.Commit), Points = commitPoints, Verdict = LeakVerdict.Stable };
            commit = commit with { Verdict = commit.Memory.Verdict };
            var zombiePoints = s.Zombies.Points(from);
            var zombieTrend = LeakAnalysis.Measure(zombiePoints, LeakLimits.Zombies);
            var zombies = new LeakRow { Name = "Zombie processes", Memory = zombieTrend, Points = zombiePoints, Verdict = zombieTrend.Verdict };

            var processes = s.Processes.Values.Where(p => p.LastSeen == s.LastSample).Select(p => ProcessRow(p, from)).ToList();
            var apps = s.Apps.Values.Where(a => a.LastSeen == s.LastSample).Select(a => AppRow(a, from)).ToList();
            var tags = s.Tags.Values.Where(t => t.LastSeen == s.LastSample).Select(t => TagRow(t, from)).ToList();
            double watched = s.Samples > 1 ? s.LastSample / 3600.0 : 0;
            var (headline, detail, worst) = Summarize(s, from, processes, apps, tags, commit, zombies, watched);
            return new LeakReport(s.Samples > 0 ? s.Origin.ToLocalTime() : null, watched, s.Samples,
                Rank(processes), Rank(apps), Rank(tags), commit, zombies, headline, detail, worst, _tagsWork || s.Tags.Count > 0);
        }
    }

    static List<LeakRow> Rank(IEnumerable<LeakRow> rows) =>
        rows.OrderByDescending(r => r.Verdict).ThenByDescending(r => r.Memory.Growth).ToList();

    static LeakRow ProcessRow(LeakProcess p, int from)
    {
        var points = p.Commit.Points(from);
        var memory = LeakAnalysis.Measure(points, LeakLimits.Memory);
        var handles = LeakAnalysis.Measure(p.Handles.Points(from), LeakLimits.Handles);
        var threads = LeakAnalysis.Measure(p.Threads.Points(from), LeakLimits.Threads);
        return new LeakRow
        {
            Name = p.Name, Id = p.Pid.ToString(), Memory = memory, Handles = handles, Threads = threads, Points = points,
            Verdict = new[] { memory.Verdict, handles.Verdict, threads.Verdict }.Max(),
        };
    }

    static LeakRow AppRow(LeakApp a, int from)
    {
        var points = a.Commit.Points(from);
        var memory = LeakAnalysis.Measure(points, LeakLimits.Memory);
        var handles = LeakAnalysis.Measure(a.Handles.Points(from), LeakLimits.Handles);
        return new LeakRow
        {
            Name = a.Name, Id = a.Copies == 1 ? "1 copy" : $"{a.Copies} copies", Memory = memory, Handles = handles, Points = points,
            Verdict = new[] { memory.Verdict, handles.Verdict }.Max(),
        };
    }

    static LeakRow TagRow(LeakTag t, int from)
    {
        var points = t.Bytes.Points(from);
        var memory = LeakAnalysis.Measure(points, LeakLimits.Pool);
        return new LeakRow { Name = t.Tag, Id = t.NonPaged ? "Non-paged" : "Paged", Detail = MemoryMapReader.DescribeTag(t.Tag), Memory = memory, Points = points, Verdict = memory.Verdict };
    }

    static (string Headline, string Detail, LeakVerdict Worst) Summarize(LeakStore s, int from, List<LeakRow> processes, List<LeakRow> apps, List<LeakRow> tags, LeakRow commit, LeakRow zombies, double watched)
    {
        // Thousands of zombies are a finding straight away. Unlike a growing app, they need no history to prove.
        if (s.Zombies.V.LastOrDefault() is var stuck and >= ManyZombies)
        {
            var rate = zombies.Memory.Hours > 0 ? $", growing about {zombies.Memory.PerHour:N0} an hour" : "";
            var outside = s.Commit.V[^1] - s.AllProcesses.V[^1] - s.Pool.V[^1];
            return ($"{stuck:N0} exited processes are stuck in kernel memory{rate}",
                $"They belong to no app, so they don't show in Task Manager, and {Fmt.Bytes(outside)} of commit sits outside apps and kernel pools. The Zombie processes tab shows how to find the cause.",
                LeakVerdict.Leak);
        }

        if (s.Samples < 2) return ("Watching for leaks", "Tman has only just started recording. Leave it running, even in the tray, and check back in an hour or two.", LeakVerdict.Watching);
        if (watched < LeakAnalysis.MinHours) return ("Watching for leaks", $"{Fmt.Span(TimeSpan.FromHours(watched))} recorded so far. A verdict needs at least {LeakAnalysis.MinHours * 60:0} minutes, and a few hours is better.", LeakVerdict.Watching);

        var top = processes.Concat(apps).Concat(tags).OrderByDescending(r => r.Verdict).ThenByDescending(r => r.Memory.PerHour).FirstOrDefault();
        var worst = top?.Verdict ?? LeakVerdict.Stable;
        string headline = worst switch
        {
            LeakVerdict.Leak => $"Likely leak: {Label(top!)} is growing about {Fmt.Bytes(top!.Memory.PerHour)} an hour",
            LeakVerdict.Suspect => $"Worth watching: {Label(top!)} is growing about {Fmt.Bytes(top!.Memory.PerHour)} an hour",
            _ => "No leak found so far",
        };

        var growth = commit.Memory.Growth;
        string detail = $"Watched for {Fmt.Span(TimeSpan.FromHours(watched))}.";
        if (growth >= LeakLimits.Commit.Noticeable)
        {
            double Grew(LeakSeries series)
            {
                var p = series.Points(from);
                return p.Count > 1 ? p[^1].Value - p[0].Value : 0;
            }
            double byApps = Grew(s.AllProcesses), byPool = Grew(s.Pool), other = growth - byApps - byPool;
            detail += $" Total commit grew {Fmt.Bytes(growth)}: apps {Signed(byApps)}, kernel pools {Signed(byPool)}, everything else {Signed(other)}.";
            if (other > growth / 2) detail += " Growth outside apps and pools usually means a driver or graphics memory.";
            else if (byPool > growth / 2) detail += " Most of it is in the kernel, so look at the Kernel tab.";
        }
        var limit = s.CommitLimit.V.LastOrDefault();
        if (commit.Memory.Verdict >= LeakVerdict.Suspect && commit.Memory.PerHour > 0 && limit > 0)
        {
            double left = (limit - commit.Memory.Last) / commit.Memory.PerHour;
            if (left > 0) detail += $" At this rate memory runs out in about {Fmt.Span(TimeSpan.FromHours(left))}.";
        }
        return (headline, detail, worst);
    }

    static string Label(LeakRow row) => row.Id is "Paged" or "Non-paged" ? $"kernel pool tag {row.Name}" : row.Name;

    public static string Signed(double value) => (value < 0 ? "-" : "+") + Fmt.Bytes(Math.Abs(value));

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var store = JsonSerializer.Deserialize<LeakStore>(File.ReadAllText(FilePath));
            if (store != null) lock (_gate) _store = store;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // History is only a convenience. Starting fresh is fine.
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_gate)
            {
                if (_sample) return;
                json = JsonSerializer.Serialize(_store);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again in a few minutes.
        }
    }

    /// <summary>Dev aid for <c>--snap &lt;dir&gt; leaks sample</c>: made-up history, for checking the page on a PC with no leak.</summary>
    internal void LoadSample()
    {
        const int step = 300, count = 96;
        var store = new LeakStore { Boot = DateTime.UtcNow, Origin = DateTime.UtcNow.AddSeconds(-step * count), LastSample = step * (count - 1), Samples = count };
        var random = new Random(4);
        const long MB = 1024 * 1024;
        void Fill(LeakSeries series, Func<int, double> value)
        {
            for (int i = 0; i < count; i++) series.Add(i * step, (long)value(i));
        }
        LeakProcess Process(string name, int pid, Func<int, double> mb, int handles = 800, double handleGrowth = 0)
        {
            var p = new LeakProcess { Name = name, Pid = pid, LastSeen = store.LastSample };
            Fill(p.Commit, i => mb(i) * MB);
            Fill(p.Handles, i => handles + handleGrowth * i);
            Fill(p.Threads, _ => 24);
            store.Processes[$"{pid}:0"] = p;
            store.Apps[name] = new LeakApp { Name = name, Copies = 1, LastSeen = store.LastSample, Commit = p.Commit, Handles = p.Handles };
            return p;
        }
        Process("ExampleUpdater.exe", 4120, i => 180 + i * 12 + random.Next(-6, 6));
        Process("ExampleSync.exe", 7312, i => 90 + i * 1.4 + random.Next(-3, 3), 900, 28);
        Process("ExampleBrowser.exe", 5540, i => 900 + 120 * Math.Sin(i / 3.0) + random.Next(-20, 20));
        Process("ExampleEditor.exe", 8896, i => 300 + 500 * (1 - Math.Exp(-i / 6.0)));
        Process("ExampleGame.exe", 2204, i => i < 50 ? 400 : 2100);
        var tag = new LeakTag { Tag = "Exmp", NonPaged = true, LastSeen = store.LastSample };
        Fill(tag.Bytes, i => (40 + i * 6) * MB);
        store.Tags["N:Exmp"] = tag;
        var steady = new LeakTag { Tag = "File", LastSeen = store.LastSample };
        Fill(steady.Bytes, i => (210 + random.Next(-4, 4)) * MB);
        store.Tags["P:File"] = steady;
        Fill(store.Commit, i => (14000 + i * 26) * MB);
        Fill(store.CommitLimit, _ => 36000.0 * MB);
        Fill(store.AllProcesses, i => (9000 + i * 19) * MB);
        Fill(store.Pool, i => (900 + i * 6) * MB);
        Fill(store.Zombies, i => 2400 + i * 140);
        lock (_gate)
        {
            _store = store;
            _sample = true;
            _tagsWork = true;
        }
    }
}
