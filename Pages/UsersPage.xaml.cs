using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

public sealed class UserSession : ObservableObject
{
    public int SessionId { get; init; }
    public string User { get; init; } = "";
    public string Station { get; init; } = "";

    string _state = "";
    public string State { get => _state; set => Set(ref _state, value); }

    int _processes;
    public int Processes { get => _processes; set => Set(ref _processes, value); }

    double _cpu;
    public double Cpu { get => _cpu; set { if (Set(ref _cpu, value)) Raise(nameof(CpuText)); } }
    public string CpuText => Fmt.Pct(_cpu);

    ulong _memory;
    public ulong Memory { get => _memory; set { if (Set(ref _memory, value)) Raise(nameof(MemoryText)); } }
    public string MemoryText => Fmt.Bytes(_memory);
}

public partial class UsersPage : UserControl, IPage
{
    const int WTSUserName = 5, WTSWinStationName = 6, WTSDomainName = 7;
    static readonly string[] States = { "Active", "Connected", "Connecting", "Shadowing", "Disconnected", "Idle", "Listening", "Reset", "Down", "Initializing" };

    readonly ObservableCollection<UserSession> _sessions = new();
    bool _visible;
    int _tick;

    public UsersPage()
    {
        InitializeComponent();
        Ui.SetupGrid(List);
        List.ItemsSource = _sessions;
        SystemMonitor.Instance.Updated += OnUpdated;
    }

    public void OnShown()
    {
        _visible = true;
        _tick = 0;
        if (SystemMonitor.Instance.Latest is { } s) OnUpdated(s);
    }

    public void OnHidden() => _visible = false;

    void OnUpdated(Snapshot s)
    {
        if (!_visible) return;
        if (_tick++ % 5 == 0) RefreshSessions();
        foreach (var u in _sessions)
        {
            var procs = s.Processes.Where(p => p.SessionId == u.SessionId && p.Pid != 0).ToList();
            u.Processes = procs.Count;
            u.Cpu = Math.Round(procs.Sum(p => p.Cpu), 1);
            u.Memory = (ulong)procs.Sum(p => (double)p.PrivateWs);
        }
        CountText.Text = _sessions.Count == 1 ? "1 signed-in user" : $"{_sessions.Count} signed-in users";
    }

    void RefreshSessions()
    {
        var current = Enumerate();
        foreach (var gone in _sessions.Where(x => !current.Any(c => c.SessionId == x.SessionId)).ToList()) _sessions.Remove(gone);
        foreach (var c in current)
        {
            var existing = _sessions.FirstOrDefault(x => x.SessionId == c.SessionId);
            if (existing == null) _sessions.Add(c);
            else existing.State = c.State;
        }
    }

    static List<UserSession> Enumerate()
    {
        var list = new List<UserSession>();
        if (!Native.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var info, out var count)) return list;
        try
        {
            int size = Marshal.SizeOf<Native.WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<Native.WTS_SESSION_INFO>(info + i * size);
                var user = Query(si.SessionId, WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var domain = Query(si.SessionId, WTSDomainName);
                list.Add(new UserSession
                {
                    SessionId = si.SessionId,
                    User = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}",
                    Station = Query(si.SessionId, WTSWinStationName) ?? "",
                    State = si.State >= 0 && si.State < States.Length ? States[si.State] : si.State.ToString(),
                });
            }
        }
        finally
        {
            Native.WTSFreeMemory(info);
        }
        return list;
    }

    static string? Query(int session, int infoClass)
    {
        if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { Native.WTSFreeMemory(buf); }
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = List.SelectedItem != null;
        DisconnectButton.IsEnabled = has;
        SignOffButton.IsEnabled = has;
    }

    void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not UserSession u) return;
        if (!Ui.Confirm($"Disconnect {u.User}? Their apps keep running and they can reconnect later.")) return;
        if (!Native.WTSDisconnectSession(IntPtr.Zero, u.SessionId, false))
            Ui.Error($"Couldn't disconnect: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        RefreshSessions();
    }

    void SignOff_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not UserSession u) return;
        if (!Ui.Confirm($"Sign off {u.User}?\n\nAll of their open apps will close and any unsaved work will be lost.")) return;
        if (!Native.WTSLogoffSession(IntPtr.Zero, u.SessionId, false))
            Ui.Error($"Couldn't sign off: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        RefreshSessions();
    }
}
