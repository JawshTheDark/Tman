using System.Collections.ObjectModel;
using System.Windows.Controls;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public partial class PerformancePage : UserControl, IPage
{
    readonly ObservableCollection<PerfDevice> _devices = new();
    readonly Dictionary<string, PerfDevice> _byKey = new();
    bool _visible;

    public PerformancePage()
    {
        InitializeComponent();
        Devices.ItemsSource = _devices;
        SystemMonitor.Instance.Updated += OnUpdated;
        StaticInfo.Loaded += () =>
        {
            if (SystemMonitor.Instance.Latest is { } s) OnUpdated(s);
        };
    }

    public void OnShown()
    {
        _visible = true;
        if (SystemMonitor.Instance.Latest is { } s) OnUpdated(s);
        if (Devices.SelectedItem == null && _devices.Count > 0) Devices.SelectedIndex = 0;
    }

    public void OnHidden() => _visible = false;

    /// <summary>Dev aid for --snap: select the first device whose key starts with the prefix (e.g. "gpu").</summary>
    internal void SelectDevice(string keyPrefix) =>
        Devices.SelectedItem = _devices.FirstOrDefault(d => d.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)) ?? Devices.SelectedItem;

    void OnUpdated(Snapshot s)
    {
        if (!_visible) return;
        var wanted = new List<(string Key, Func<PerfDevice> Create)>
        {
            ("cpu", () => new CpuDevice()),
            ("mem", () => new MemoryDevice()),
        };
        foreach (var d in s.Disks) wanted.Add(($"disk{d.Index}", () => new DiskDevice(d.Index)));
        foreach (var n in s.Nets) wanted.Add(("net" + n.Id, () => new NetDevice(n.Id)));
        for (int i = 0; i < s.Gpus.Count; i++)
        {
            var g = s.Gpus[i];
            int number = i;
            wanted.Add(("gpu" + g.Luid, () => new GpuDevice(g.Luid, number)));
        }

        if (!wanted.Select(w => w.Key).SequenceEqual(_devices.Select(d => d.Key)))
        {
            var selected = Devices.SelectedItem;
            _devices.Clear();
            foreach (var (key, create) in wanted)
            {
                if (!_byKey.TryGetValue(key, out var dev)) _byKey[key] = dev = create();
                _devices.Add(dev);
            }
            Devices.SelectedItem = selected is PerfDevice p && _devices.Contains(p) ? p : _devices[0];
        }

        foreach (var d in _devices) d.Update(s);
    }
}
