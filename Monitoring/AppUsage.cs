using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Tman.Monitoring;

/// <summary>Usage accumulated for one app (one executable path) across Tman sessions.</summary>
public sealed class AppRecord
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string? Path { get; set; }
    /// <summary>User + kernel CPU time, in 100 ns units.</summary>
    public long CpuTime { get; set; }
    public double GpuSeconds { get; set; }
    public long IoBytes { get; set; }
    public ulong PeakMemory { get; set; }
    public int Launches { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>Instances alive in the latest sample.</summary>
    [JsonIgnore] public int Running { get; set; }
    [JsonIgnore] public ImageSource? Icon { get; set; }
}

/// <summary>
/// Per-app usage history built from the process samples. Windows keeps its own history in a locked
/// database only Task Manager can read, so Tman keeps one itself; it only sees time while Tman runs.
/// Mutated on the UI thread (from <see cref="SystemMonitor.Updated"/>), saved to %APPDATA%\Tman\app-history.json.
/// </summary>
public sealed class AppUsage
{
    public static AppUsage Instance { get; } = new();

    sealed class Store
    {
        public DateTime Since { get; set; }
        public DateTime SavedUtc { get; set; }
        public List<AppRecord> Apps { get; set; } = new();
    }

    static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "app-history.json");

    readonly Dictionary<string, AppRecord> _apps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(int, long), (AppRecord App, long Cpu, long Io)> _live = new();
    DateTime _cutoffUtc;
    DateTime? _lastTick;
    DateTime _nextSave;
    bool _baselined;

    public DateTime Since { get; private set; }
    public IReadOnlyCollection<AppRecord> Apps => _apps.Values;
    /// <summary>Bumped when the history is cleared, so views know to rebuild.</summary>
    public int Version { get; private set; }

    public void Start()
    {
        Load();
        SystemMonitor.Instance.Updated += OnUpdated;
    }

    void Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) is { } store)
            {
                Since = store.Since;
                _cutoffUtc = store.SavedUtc;
                foreach (var a in store.Apps)
                    if (a.Key.Length > 0) _apps[a.Key] = a;
                return;
            }
        }
        catch { }
        // First run: count everything still running since boot.
        Since = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _cutoffUtc = DateTime.MinValue;
    }

    void OnUpdated(Snapshot s)
    {
        double dt = _lastTick is { } last ? Math.Clamp((s.Time - last).TotalSeconds, 0, 10) : 0;
        _lastTick = s.Time;
        foreach (var a in _apps.Values) a.Running = 0;

        var alive = new HashSet<(int, long)>();
        foreach (var p in s.Processes)
        {
            if (p.Pid == 0) continue;
            var key = (p.Pid, p.CreateTime);
            alive.Add(key);
            AppRecord app;
            if (_live.TryGetValue(key, out var prev))
            {
                app = prev.App;
                app.CpuTime += Math.Max(0, p.CpuTime - prev.Cpu);
                app.IoBytes += Math.Max(0, p.IoBytes - prev.Io);
            }
            else
            {
                app = Find(p);
                // Credit what an instance used before we first saw it only when no earlier session could
                // have counted it: it started after our last save, or it appeared while we were watching.
                if (_baselined || DateTime.FromFileTimeUtc(p.CreateTime) > _cutoffUtc)
                {
                    app.CpuTime += p.CpuTime;
                    app.IoBytes += p.IoBytes;
                    app.Launches++;
                }
            }
            _live[key] = (app, p.CpuTime, p.IoBytes);
            app.GpuSeconds += p.Gpu / 100 * dt;
            if (p.PrivateWs > app.PeakMemory) app.PeakMemory = p.PrivateWs;
            app.LastSeen = s.Time;
            app.Running++;
            app.Icon ??= p.Details.Icon;
        }
        _baselined = true;
        if (_live.Count > alive.Count)
            foreach (var k in _live.Keys.Where(k => !alive.Contains(k)).ToList()) _live.Remove(k);

        if (s.Time >= _nextSave)
        {
            if (_nextSave != default) Save();
            _nextSave = s.Time.AddMinutes(1);
        }
    }

    AppRecord Find(ProcSample p)
    {
        var path = p.Details.Path;
        var key = path ?? p.Name;
        if (!_apps.TryGetValue(key, out var app))
            _apps[key] = app = new AppRecord { Key = key, Exe = p.Name, Path = path };
        if (p.Details.Description is { Length: > 0 } desc) app.Name = desc;
        else if (app.Name.Length == 0) app.Name = System.IO.Path.GetFileNameWithoutExtension(p.Name);
        return app;
    }

    /// <summary>Forgets everything and starts counting from now (running apps are not credited again).</summary>
    public void Clear()
    {
        _apps.Clear();
        _live.Clear();
        _baselined = false;
        _cutoffUtc = DateTime.UtcNow;
        Since = DateTime.Now;
        Version++;
        Save();
    }

    public void Save()
    {
        if (MainWindow.SnapshotMode) return;
        try
        {
            var store = new Store { Since = Since, SavedUtc = DateTime.UtcNow, Apps = _apps.Values.ToList() };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(store));
        }
        catch { }
    }
}
