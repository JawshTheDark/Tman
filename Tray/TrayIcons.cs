using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Tray;

/// <summary>
/// The live notification-area icon: one icon of bars with a flyout on hover, or one icon per chosen metric with plain
/// tooltips. Redrawn every sample. Left-click opens Tman, right-click shows a menu. Survives Explorer restarts. UI thread only.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    const int WM_TRAY = 0x8000 + 0x51; // WM_APP + n: our callback message
    const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_POPUPOPEN = 0x0406;
    const string CombinedKey = "combined";
    const int MostBars = 3;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NIIF_INFO = 0x1, NIIF_RESPECT_QUIET_TIME = 0x80;
    const uint NOTIFYICON_VERSION_4 = 4;
    const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll")]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out ScreenRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowW(string? className, string? title);

    sealed class Slot
    {
        public uint Id;
        public IntPtr Icon;
        public string Drawn = "";
        public string Tip = "";
        public bool Added;
    }

    readonly HwndSource _window;
    readonly int _taskbarCreated;
    readonly Dictionary<string, Slot> _slots = new();
    readonly List<string> _order = new();
    readonly Action _open, _settings, _exit;
    uint _nextId = 1;
    DateTime _lastClick;
    TrayFlyout? _flyout;
    bool _combined;

    public TrayIcons(Action open, Action settings, Action exit)
    {
        _open = open;
        _settings = settings;
        _exit = exit;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        // A hidden top-level window receives the icon callbacks and can take the foreground for the menu.
        _window = new HwndSource(new HwndSourceParameters("Tman tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, not visible
            ExtendedWindowStyle = 0x80,                // WS_EX_TOOLWINDOW
        });
        _window.AddHook(Hook);
    }

    /// <summary>How many icons were accepted by the shell (for diagnostics).</summary>
    public int Shown => _slots.Values.Count(s => s.Added);

    /// <summary>Brings the icons in line with the settings and the latest sample.</summary>
    public void Sync(Snapshot? s)
    {
        var settings = Settings.Current;
        _combined = settings.TrayLayout != "separate";
        var wanted = !settings.TrayEnabled ? new List<string>()
            : _combined ? new List<string> { CombinedKey }
            : settings.TrayItems.Where(k => TrayMetrics.Find(k) != null).Distinct().ToList();
        if (!wanted.SequenceEqual(_order))
        {
            // Windows orders icons by when they were added, so re-add all of them to apply a new order.
            RemoveAll();
            _order.AddRange(wanted);
        }

        int size = IconSize();
        if (_combined)
        {
            if (settings.TrayEnabled) SyncCombined(s, size);
            if (s != null && _flyout is { IsVisible: true }) _flyout.Update(s);
            return;
        }

        bool meter = settings.TrayStyle == "meter";
        foreach (var key in _order)
        {
            var metric = TrayMetrics.Find(key)!;
            var reading = s == null ? null : metric.Read(s);
            if (!_slots.TryGetValue(key, out var slot)) _slots[key] = slot = new Slot { Id = _nextId++ };
            var tip = reading?.Tip ?? $"{metric.Name}: not available on this PC";
            if (tip.Length > 127) tip = tip[..127];
            // Only redraw when what's visible changes (text, meter step, size or style).
            string drawn = $"{size}|{meter}|{reading?.Text}|{(int)Math.Round((reading?.Level ?? 0) * 24)}";
            if (drawn != slot.Drawn || !slot.Added)
            {
                var icon = TrayRenderer.ToHIcon(TrayRenderer.Render(reading, metric.Color, meter, size));
                if (icon == IntPtr.Zero) continue;
                var old = slot.Icon;
                slot.Icon = icon;
                slot.Drawn = drawn;
                Apply(slot, tip);
                if (old != IntPtr.Zero) DestroyIcon(old);
            }
            else if (tip != slot.Tip) Apply(slot, tip);
        }
    }

    /// <summary>The bars for the first few chosen metrics, and CPU, memory and GPU if none are chosen.</summary>
    public static List<TrayMetric> BarMetrics()
    {
        var chosen = Settings.Current.TrayItems.Select(TrayMetrics.Find).OfType<TrayMetric>().Distinct().Take(MostBars).ToList();
        return chosen.Count > 0 ? chosen : new[] { "cpu", "mem", "gpu" }.Select(k => TrayMetrics.Find(k)!).ToList();
    }

    void SyncCombined(Snapshot? s, int size)
    {
        var metrics = BarMetrics();
        var readings = metrics.Select(m => (Metric: m, Reading: s == null ? null : m.Read(s))).ToList();
        if (!_slots.TryGetValue(CombinedKey, out var slot)) _slots[CombinedKey] = slot = new Slot { Id = _nextId++ };

        // Without NIF_SHOWTIP Windows shows no tooltip and offers the hover to the flyout. This text is still what screen readers announce.
        var tip = "Tman: " + string.Join(", ", readings.Select(r => $"{r.Metric.Short} {r.Reading?.Text ?? "--"}"));
        if (tip.Length > 127) tip = tip[..127];
        var bars = readings.Select(r => (Level: r.Reading?.Level ?? 0, r.Metric.Color)).ToList();
        string drawn = $"{size}|{string.Join(',', bars.Select(b => $"{b.Color}:{TrayRenderer.BarHeight(b.Level, size)}"))}";
        if (drawn != slot.Drawn || !slot.Added)
        {
            var icon = TrayRenderer.ToHIcon(TrayRenderer.RenderBars(bars, size));
            if (icon == IntPtr.Zero) return;
            var old = slot.Icon;
            slot.Icon = icon;
            slot.Drawn = drawn;
            Apply(slot, tip);
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
        else if (tip != slot.Tip) Apply(slot, tip);
    }

    /// <summary>Where the icon is right now. It moves when other icons come and go, so this is asked for each time.</summary>
    ScreenRect? IconArea()
    {
        if (!_slots.TryGetValue(CombinedKey, out var slot)) return null;
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = _window.Handle, uID = slot.Id };
        return Shell_NotifyIconGetRect(ref id, out var rect) == 0 && rect.Right > rect.Left ? rect : null;
    }

    void Hovered()
    {
        if (!_combined) return;
        _flyout ??= new TrayFlyout(_open, IconArea);
        _flyout.Hover();
    }

    void Apply(Slot slot, string tip)
    {
        var data = Data(slot);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | (_combined ? 0 : NIF_SHOWTIP);
        data.uCallbackMessage = WM_TRAY;
        data.hIcon = slot.Icon;
        data.szTip = tip;
        slot.Tip = tip;
        if (slot.Added && Shell_NotifyIconW(NIM_MODIFY, ref data)) return;
        // Not added yet (or Explorer forgot it): add, then opt into the modern callback format.
        slot.Added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!slot.Added) return;
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    NOTIFYICONDATAW Data(Slot slot) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = slot.Id,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>A one-off notification from the first icon (shown as a toast on Windows 11).</summary>
    public void Notify(string title, string text)
    {
        var slot = _order.Select(k => _slots.GetValueOrDefault(k)).FirstOrDefault(s => s is { Added: true });
        if (slot == null) return;
        var data = Data(slot);
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NIIF_INFO | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    static int IconSize()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        uint dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 96;
        if (dpi == 0) dpi = 96;
        int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        return size > 0 ? size : 16;
    }

    void RemoveAll()
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.Added)
            {
                var data = Data(slot);
                Shell_NotifyIconW(NIM_DELETE, ref data);
            }
            if (slot.Icon != IntPtr.Zero) DestroyIcon(slot.Icon);
        }
        _slots.Clear();
        _order.Clear();
    }

    /// <summary>Drops and re-adds every icon (after pinning, Explorer only applies visibility to newly added icons).</summary>
    public void Rebuild()
    {
        RemoveAll();
        Sync(SystemMonitor.Instance.Latest);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated)
        {
            // Explorer restarted and forgot every icon.
            foreach (var slot in _slots.Values) slot.Added = false;
            Sync(SystemMonitor.Instance.Latest);
            return IntPtr.Zero;
        }
        if (msg != WM_TRAY) return IntPtr.Zero;
        handled = true;
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case NIN_POPUPOPEN:
            case WM_MOUSEMOVE:
                Hovered();
                break;
            case WM_LBUTTONUP:
            case NIN_SELECT:
            case NIN_KEYSELECT:
                // Version 4 can report one click twice (button-up and select); act once.
                if ((DateTime.UtcNow - _lastClick).TotalMilliseconds > 500)
                {
                    _lastClick = DateTime.UtcNow;
                    _flyout?.Dismiss();
                    _open();
                }
                break;
            case WM_CONTEXTMENU:
                _flyout?.Dismiss();
                ShowMenu();
                break;
        }
        return IntPtr.Zero;
    }

    void ShowMenu()
    {
        // Without taking the foreground first, a tray menu won't close when you click elsewhere.
        SetForegroundWindow(_window.Handle);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, StaysOpen = false };
        if (Application.Current.TryFindResource("UiFont") is FontFamily font) menu.FontFamily = font;
        TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
        menu.Items.Add(Item("Open Tman", _open, bold: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Tray icons…", _settings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit Tman", _exit));
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action click, bool bold = false)
    {
        var item = new MenuItem { Header = header, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// Windows 11 keeps each tray icon's "show on the taskbar" switch in HKCU\Control Panel\NotifyIconSettings.
    /// Flips it for every icon belonging to this exe; returns how many entries were changed.
    /// </summary>
    public static int SetAlwaysShow(bool show)
    {
        int changed = 0;
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root == null) return 0;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name, writable: true);
            if (key?.GetValue("ExecutablePath") is not string path || !IsThisExe(path)) continue;
            key.SetValue("IsPromoted", show ? 1 : 0, RegistryValueKind.DWord);
            changed++;
        }
        return changed;
    }

    /// <summary>The shell may store paths with a known-folder GUID prefix; compare what we can.</summary>
    static bool IsThisExe(string stored)
    {
        var self = Environment.ProcessPath ?? "";
        if (string.Equals(stored, self, StringComparison.OrdinalIgnoreCase)) return true;
        if (!stored.StartsWith('{')) return false;
        int close = stored.IndexOf('}');
        return close > 0 && self.EndsWith(stored[(close + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        // A window left open, even hidden, would keep the process alive after the main window closes.
        _flyout?.Close();
        _flyout = null;
        RemoveAll();
        _window.Dispose();
    }
}
