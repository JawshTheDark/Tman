using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Pages;

/// <summary>One row in the Processes grid; updated in place each tick so selection and scroll survive.</summary>
public sealed class ProcessItem : ObservableObject
{
    public ProcessItem(ProcSample s)
    {
        Pid = s.Pid;
        CreateTime = s.CreateTime;
        Name = s.Name;
        ParentPid = s.ParentPid;
        SessionId = s.SessionId;
        Details = s.Details;
        Update(s);
    }

    public int Pid { get; }
    public long CreateTime { get; }
    public string Name { get; }
    public int ParentPid { get; }
    public int SessionId { get; }
    public ProcessDetails Details { get; }

    public ImageSource? Icon => Details.Icon;
    public string Description => Details.Description ?? "";
    public string User => Details.User ?? "";
    public string Path => Details.Path ?? "";
    public string CommandLine => Details.CommandLine ?? "";
    public string Company => Details.Company ?? "";
    public string Started => CreateTime > 0 ? DateTime.FromFileTime(CreateTime).ToString("g") : "";

    double _cpu;
    public double Cpu
    {
        get => _cpu;
        private set { if (Set(ref _cpu, value)) { Raise(nameof(CpuText)); Raise(nameof(CpuHeat)); } }
    }
    public string CpuText => Fmt.Pct(_cpu);
    public Brush? CpuHeat => Heat.Percent(_cpu);

    ulong _memory;
    public ulong Memory
    {
        get => _memory;
        private set { if (Set(ref _memory, value)) { Raise(nameof(MemoryText)); Raise(nameof(MemoryHeat)); } }
    }
    public string MemoryText => Fmt.Mb(_memory);
    public Brush? MemoryHeat => Heat.Memory(_memory);

    double _io;
    public double Io
    {
        get => _io;
        private set { if (Set(ref _io, value)) { Raise(nameof(IoText)); Raise(nameof(IoHeat)); } }
    }
    public string IoText => Fmt.MbRate(_io);
    public Brush? IoHeat => Heat.Rate(_io);

    double _gpu;
    public double Gpu
    {
        get => _gpu;
        private set { if (Set(ref _gpu, value)) { Raise(nameof(GpuText)); Raise(nameof(GpuHeat)); } }
    }
    public string GpuText => Fmt.Pct(_gpu);
    public Brush? GpuHeat => Heat.Percent(_gpu);

    int _threads;
    public int Threads { get => _threads; private set => Set(ref _threads, value); }

    int _handles;
    public int Handles { get => _handles; private set => Set(ref _handles, value); }

    string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    string _priority = "";
    public string Priority { get => _priority; private set => Set(ref _priority, value); }

    static readonly Brush NormalText = Frozen(Color.FromRgb(0xE4, 0xE8, 0xEC));

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    string _signer = "";
    /// <summary>Verified signer, "Not signed", etc.; empty until checked.</summary>
    public string Signer { get => _signer; private set => Set(ref _signer, value); }

    Brush _signerBrush = NormalText;
    public Brush SignerBrush { get => _signerBrush; private set => Set(ref _signerBrush, value); }

    public void SetSignature(SignatureInfo? s)
    {
        Signer = s?.Text ?? (Path.Length > 0 ? "Checking…" : "");
        SignerBrush = s?.State switch
        {
            SignatureState.Unsigned => (Brush)Application.Current.FindResource("WarnBrush"),
            SignatureState.Untrusted => (Brush)Application.Current.FindResource("BadBrush"),
            null or SignatureState.Error => (Brush)Application.Current.FindResource("SubtleBrush"),
            _ => NormalText,
        };
    }

    public void Update(ProcSample s)
    {
        Cpu = Math.Round(s.Cpu, 1);
        Memory = s.PrivateWs;
        Io = Math.Round(s.IoBps / 10240) * 10240;
        Gpu = Math.Round(s.Gpu, 1);
        Threads = s.Threads;
        Handles = s.Handles;
        Status = s.Suspended ? "Suspended" : "";
        Priority = s.BasePriority switch
        {
            <= 4 => "Low",
            <= 6 => "Below normal",
            <= 8 => "Normal",
            <= 10 => "Above normal",
            <= 13 => "High",
            _ => "Realtime",
        };
    }
}

/// <summary>Task-Manager-style amber cell shading; darker means heavier use.</summary>
public static class Heat
{
    static readonly Brush?[] Levels = Enumerable.Range(0, 8).Select(i =>
    {
        if (i == 0) return null;
        var b = new SolidColorBrush(Color.FromArgb((byte)(18 + i * 20), 0xFF, 0xB9, 0x00));
        b.Freeze();
        return (Brush?)b;
    }).ToArray();

    public static Brush? Percent(double pct) => pct < 0.5 ? null : Levels[Math.Clamp((int)Math.Ceiling(pct / 12.5), 1, 7)];

    public static Brush? Memory(ulong bytes)
    {
        double mb = bytes / 1048576.0;
        int level = mb < 50 ? 0 : mb < 100 ? 1 : mb < 250 ? 2 : mb < 500 ? 3 : mb < 1024 ? 4 : mb < 2048 ? 5 : mb < 4096 ? 6 : 7;
        return Levels[level];
    }

    public static Brush? Rate(double bytesPerSec)
    {
        double mb = bytesPerSec / 1048576.0;
        int level = mb < 0.05 ? 0 : mb < 0.5 ? 1 : mb < 2 ? 2 : mb < 8 ? 3 : mb < 25 ? 4 : mb < 60 ? 5 : mb < 150 ? 6 : 7;
        return Levels[level];
    }
}

public static class ProcessActions
{
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "smss.exe", "csrss.exe",
        "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "LsaIso.exe",
    };

    public static bool IsCritical(string name) => Critical.Contains(name);

    /// <summary>Asks where to save, then writes a full memory dump in the background and offers to show it.</summary>
    public static async void CreateDump(Window owner, int pid, string name)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Save memory dump of {name} (it pauses briefly while the dump is written)",
            Filter = "Memory dump (*.dmp)|*.dmp",
            FileName = $"{Path.GetFileNameWithoutExtension(name)}-{pid}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(owner) != true) return;
        var path = dlg.FileName;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.AppStarting;
        string? error = null;
        bool ok;
        try { ok = await Task.Run(() => ProcessInspector.WriteDump(pid, path, out error)); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        if (!ok)
        {
            var tip = MainWindow.IsAdmin ? "" : "\n\nTip: File > Restart as administrator to dump system processes.";
            Ui.Error($"Couldn't create the dump of {name}: {error}{tip}");
            return;
        }
        long size = new FileInfo(path).Length;
        if (MessageBox.Show(owner, $"Saved {Fmt.Bytes(size)} dump of {name} to:\n{path}\n\nShow it in File Explorer?", "Memory dump",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Ui.OpenFileLocation(path);
    }

    public static bool Terminate(int pid, out string? error)
    {
        var h = Native.OpenProcess(Native.PROCESS_TERMINATE, false, pid);
        if (h == IntPtr.Zero)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        try
        {
            if (Native.TerminateProcess(h, 1))
            {
                error = null;
                return true;
            }
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static bool Suspend(int pid, bool resume, out string? error)
    {
        var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        try
        {
            int status = resume ? Native.NtResumeProcess(h) : Native.NtSuspendProcess(h);
            error = status == 0 ? null : $"NTSTATUS 0x{status:X8}";
            return status == 0;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static bool SetPriority(int pid, ProcessPriorityClass priority, out string? error)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priority;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
