using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;
using Tman.Core;
using WinForms = System.Windows.Forms;

namespace Tman.Pages;

/// <summary>The events Tman needs from Windows' Remote Desktop control. The rest are left out, which COM allows.</summary>
[ComImport, Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IRdpEvents
{
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int reason);
    [DispId(10)] void OnFatalError(int code);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink : IRdpEvents
{
    readonly VmConsoleWindow _owner;
    public RdpEventSink(VmConsoleWindow owner) => _owner = owner;
    public void OnLoginComplete() => _owner.OnConnected();
    public void OnDisconnected(int reason) => _owner.OnDisconnected(reason);
    public void OnFatalError(int code) => _owner.OnDisconnected(code);
    public void OnRemoteDesktopSizeChange(int width, int height) => _owner.OnScreenSize(width, height);
}

/// <summary>Windows' Remote Desktop control, which is also what Hyper-V's own console window is built on.</summary>
public sealed class RdpControl : WinForms.AxHost
{
    const string MsRdpClient10 = "a0c63c30-f08d-4ab4-907c-34905d770c7d";

    readonly RdpEventSink _sink;
    ConnectionPointCookie? _cookie;

    public RdpControl(RdpEventSink sink) : base(MsRdpClient10) => _sink = sink;

    public dynamic Client => GetOcx()!;

    protected override void CreateSink()
    {
        try { _cookie = new ConnectionPointCookie(GetOcx()!, _sink, typeof(IRdpEvents)); }
        catch { }
    }

    protected override void DetachSink() => _cookie?.Disconnect();
}

/// <summary>
/// A VM's screen, keyboard and mouse. This is the same connection Hyper-V's console window makes: Remote Desktop to the host's
/// console port, with the VM's ID saying which screen to show. It works from the moment the VM powers on, with nothing installed
/// inside it, and it needs the same rights as managing the VM.
/// </summary>
public sealed class VmConsoleWindow : Window
{
    const int ConsolePort = 2179;
    const int MaxRetries = 40;

    static readonly Dictionary<string, VmConsoleWindow> Open = new(StringComparer.OrdinalIgnoreCase);

    readonly HyperVm _vm;
    readonly RdpControl _rdp;
    readonly WindowsFormsHost _host = new();
    readonly Grid _screen = new() { Background = System.Windows.Media.Brushes.Black };
    readonly StackPanel _message = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
    readonly TextBlock _messageText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    readonly Button _keysButton = new() { Content = "Ctrl+Alt+Delete", Margin = new Thickness(0, 0, 6, 0), IsEnabled = false, ToolTip = "Sends Ctrl+Alt+Delete to the VM. Pressing Ctrl+Alt+End does the same." };
    readonly Button _fullButton = new() { Content = "Full screen", Margin = new Thickness(0, 0, 6, 0) };
    readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(3) };

    int _screenWidth, _screenHeight, _retries;
    bool _connected, _closing, _resizing;
    WindowState _windowedState;

    /// <summary>The console needs the same rights as Hyper-V's management, which a standard Tman doesn't have.</summary>
    public static bool CanConnect
    {
        get
        {
            if (MainWindow.IsAdmin) return true;
            using var me = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(me).IsInRole(new SecurityIdentifier("S-1-5-32-578")); // Hyper-V Administrators
        }
    }

    /// <summary>Opens the console for a VM, or brings its window forward if it's already open.</summary>
    public static void Show(HyperVm vm, Window? owner)
    {
        if (Open.TryGetValue(vm.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new VmConsoleWindow(vm);
        if (owner != null) window.Left = owner.Left + 40;
        if (owner != null) window.Top = owner.Top + 40;
        Open[vm.Id] = window;
        window.Show();
    }

    public VmConsoleWindow(HyperVm vm)
    {
        _vm = vm;
        _rdp = new RdpControl(new RdpEventSink(this)) { Dock = WinForms.DockStyle.Fill };
        Title = $"{vm.Name} - Tman console";
        Width = 1064;
        Height = 840;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Ui.Dress(this);

        _fullButton.Click += (_, _) => ToggleFullScreen();
        _keysButton.Click += (_, _) => SendCtrlAltDelete();
        var other = new Button { Content = "Hyper-V's window", ToolTip = "Opens this VM in Hyper-V's own console window instead." };
        other.Click += (_, _) => { if (HyperV.OpenConsole(_vm) is { } failed) Ui.Error($"The console didn't open.\n\n{failed}"); };

        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        DockPanel.SetDock(_keysButton, Dock.Left);
        DockPanel.SetDock(_fullButton, Dock.Left);
        DockPanel.SetDock(other, Dock.Right);
        bar.Children.Add(_keysButton);
        bar.Children.Add(_fullButton);
        bar.Children.Add(other);
        bar.Children.Add(_status);

        var again = new Button { Content = "Try again", HorizontalAlignment = HorizontalAlignment.Center };
        again.Click += (_, _) => { _retries = 0; Connect(); };
        _message.Children.Add(_messageText);
        _message.Children.Add(again);

        _host.HorizontalAlignment = HorizontalAlignment.Center;
        _host.VerticalAlignment = VerticalAlignment.Center;
        _screen.Children.Add(_host);
        _screen.Children.Add(_message);
        _screen.SizeChanged += (_, _) => FitScreen();

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_screen);
        Content = root;

        _retry.Tick += (_, _) => { _retry.Stop(); Connect(); };
        Loaded += (_, _) => Start();
        Activated += (_, _) => { if (_connected) _rdp.Focus(); };
        Closed += (_, _) => Shutdown();
    }

    void Start()
    {
        _host.Child = _rdp;
        _rdp.CreateControl();
        try
        {
            Configure();
        }
        catch (Exception ex)
        {
            ShowMessage($"Windows' Remote Desktop control wouldn't start: {ex.Message}", canRetry: false);
            return;
        }
        Connect();
    }

    void Configure()
    {
        var client = _rdp.Client;
        client.Server = HostName();
        client.ColorDepth = 32;
        client.DesktopWidth = 1024;
        client.DesktopHeight = 768;
        var settings = client.AdvancedSettings9;
        settings.RDPPort = ConsolePort;
        settings.PCB = _vm.Id;                                   // which VM's screen to show
        settings.AuthenticationServiceClass = "Microsoft Virtual Console Service";
        settings.EnableCredSspSupport = true;                    // signs in as the person running Tman
        settings.NegotiateSecurityLayer = false;
        settings.AuthenticationLevel = 0;
        settings.SmartSizing = true;                             // the picture scales with the window
        settings.DisplayConnectionBar = false;
        settings.GrabFocusOnConnect = true;
        settings.BitmapPersistence = 0;
        client.SecuredSettings2.KeyboardHookMode = 2;            // Windows key combinations reach the VM in full screen, as in Hyper-V's window

        // Hyper-V checks who you are but has no use for your password, and Windows refuses the connection unless told not to send it.
        SetExtendedSetting("DisableCredentialsDelegation", true);
    }

    /// <summary>Windows checks the console service's identity against the PC's full name, so "localhost" won't do.</summary>
    static string HostName()
    {
        try { return System.Net.Dns.GetHostEntry(Environment.MachineName).HostName; }
        catch { return Environment.MachineName; }
    }

    unsafe void SetExtendedSetting(string name, object value)
    {
        const int PutPropertySlot = 3;
        var iid = new Guid("302d8188-0052-4807-806a-362b628f9ac5"); // IMsRdpExtendedSettings, which only has a binary interface
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Client);
        IntPtr settings = IntPtr.Zero, nativeName = Marshal.StringToBSTR(name), variant = Marshal.AllocCoTaskMem(32);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out settings));
            Marshal.GetNativeVariantForObject(value, variant);
            var put = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)(*(IntPtr**)settings)[PutPropertySlot];
            Marshal.ThrowExceptionForHR(put(settings, nativeName, variant));
        }
        finally
        {
            if (settings != IntPtr.Zero) Marshal.Release(settings);
            Marshal.Release(unknown);
            Marshal.FreeBSTR(nativeName);
            Marshal.FreeCoTaskMem(variant);
        }
    }

    void Connect()
    {
        if (_closing) return;
        _status.Text = "Connecting…";
        _message.Visibility = Visibility.Collapsed;
        try
        {
            if ((int)_rdp.Client.Connected == 0) _rdp.Client.Connect();
        }
        catch (Exception ex)
        {
            ShowMessage($"Couldn't connect: {ex.Message}");
        }
    }

    internal void OnConnected() => Dispatcher.BeginInvoke(() =>
    {
        _connected = true;
        _retries = 0;
        _retry.Stop();
        _keysButton.IsEnabled = true;
        _message.Visibility = Visibility.Collapsed;
        _host.Visibility = Visibility.Visible;
        _status.Text = "Connected. Click the screen to type. Ctrl+Alt+Left lets go of the mouse if the VM captures it.";
        FitScreen();
        _rdp.Focus();
    });

    internal void OnDisconnected(int reason) => Dispatcher.BeginInvoke(() =>
    {
        _connected = false;
        _keysButton.IsEnabled = false;
        if (_closing) return;
        if (_resizing)
        {
            _resizing = false;
            try { ApplyScreenSize(); } catch { }
            Connect();
            return;
        }
        string detail;
        try
        {
            detail = ((string)_rdp.Client.GetErrorDescription((uint)reason, (uint)(int)_rdp.Client.ExtendedDisconnectReason)).Trim();
        }
        catch
        {
            detail = "";
        }

        // A VM that's off, restarting or still starting has no screen yet, so keep trying for a couple of minutes.
        if (++_retries <= MaxRetries)
        {
            ShowMessage($"Waiting for {_vm.Name}. It may be off or still starting.", canRetry: false);
            _retry.Start();
            return;
        }
        ShowMessage($"Couldn't reach {_vm.Name}. Check that it's running." + (detail.Length > 0 ? $"\n\n{detail}" : ""));
    });

    /// <summary>
    /// The control scales the picture relative to the size it asked for when it connected, and can't change that while connected.
    /// So when the VM changes resolution, as it does while booting, the console reconnects at the new size.
    /// </summary>
    internal void OnScreenSize(int width, int height) => Dispatcher.BeginInvoke(() =>
    {
        _screenWidth = width;
        _screenHeight = height;
        GrowToFit();
        FitScreen();
        try
        {
            if ((int)_rdp.Client.DesktopWidth == width && (int)_rdp.Client.DesktopHeight == height) return;
            if ((int)_rdp.Client.Connected == 0)
            {
                ApplyScreenSize();
                return;
            }
            _resizing = true;
            _rdp.Client.Disconnect();
        }
        catch
        {
            _resizing = false;
        }
    });

    void ApplyScreenSize()
    {
        if (_screenWidth <= 0 || _screenHeight <= 0) return;
        _rdp.Client.DesktopWidth = _screenWidth;
        _rdp.Client.DesktopHeight = _screenHeight;
    }

    /// <summary>Shows the VM at its true size, and shrinks the picture without stretching it when the window is smaller.</summary>
    void FitScreen()
    {
        double room = _screen.ActualWidth, height = _screen.ActualHeight;
        if (room <= 0 || height <= 0) return;
        if (_screenWidth <= 0 || _screenHeight <= 0)
        {
            _host.Width = room;
            _host.Height = height;
            return;
        }
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double width = _screenWidth / dpi.DpiScaleX, tall = _screenHeight / dpi.DpiScaleY;
        double scale = Math.Min(1, Math.Min(room / width, height / tall));
        _host.Width = Math.Max(1, width * scale);
        _host.Height = Math.Max(1, tall * scale);
    }

    /// <summary>Grows the window when the VM's screen gets bigger, as far as the screen allows.</summary>
    void GrowToFit()
    {
        if (_screenWidth <= 0 || WindowState != WindowState.Normal || WindowStyle == WindowStyle.None) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double extraWidth = ActualWidth - _screen.ActualWidth, extraHeight = ActualHeight - _screen.ActualHeight;
        double wanted = _screenWidth / dpi.DpiScaleX + extraWidth, wantedHeight = _screenHeight / dpi.DpiScaleY + extraHeight;
        var work = SystemParameters.WorkArea;
        if (wanted > ActualWidth) Width = Math.Min(wanted, work.Width);
        if (wantedHeight > ActualHeight) Height = Math.Min(wantedHeight, work.Height);
        if (Left + Width > work.Right) Left = Math.Max(work.Left, work.Right - Width);
        if (Top + Height > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
    }

    void ShowMessage(string text, bool canRetry = true)
    {
        _status.Text = text.Split('\n')[0];
        _messageText.Text = text;
        _message.Children[1].Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = Visibility.Visible;
        // Windows draws the control above anything in this window, so it has to go for the message to show.
        _host.Visibility = Visibility.Hidden;
    }

    void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _windowedState;
            _fullButton.Content = "Full screen";
        }
        else
        {
            _windowedState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _fullButton.Content = "Leave full screen";
        }
        if (_connected) _rdp.Focus();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr window);

    /// <summary>The control's input window, which is the one that has to have focus for sent keys to arrive.</summary>
    static IntPtr FindInputWindow(IntPtr parent)
    {
        for (var child = FindWindowExW(parent, IntPtr.Zero, null, null); child != IntPtr.Zero; child = FindWindowExW(parent, child, null, null))
        {
            var found = FindWindowExW(child, IntPtr.Zero, "IHWindowClass", null);
            if (found != IntPtr.Zero) return found;
            found = FindInputWindow(child);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    unsafe void SendCtrlAltDelete()
    {
        const int SendKeysSlot = 14;
        const int ScanCtrl = 0x1D, ScanAlt = 0x38, ScanDelete = 0x53;
        var input = FindInputWindow(_rdp.Handle);
        if (input != IntPtr.Zero) SetFocus(input);

        var iid = new Guid("2f079c4c-87b2-4afd-97ab-20cdb43038ae"); // IMsRdpClientNonScriptable, which only has a binary interface
        var unknown = Marshal.GetIUnknownForObject((object)_rdp.Client);
        IntPtr client = IntPtr.Zero;
        try
        {
            if (Marshal.QueryInterface(unknown, in iid, out client) != 0) return;
            var send = (delegate* unmanaged[Stdcall]<IntPtr, int, short*, int*, int>)(*(IntPtr**)client)[SendKeysSlot];
            short* up = stackalloc short[] { 0, 0, 0, -1, -1, -1 };
            int* keys = stackalloc int[] { ScanCtrl, ScanAlt, ScanDelete, ScanDelete, ScanAlt, ScanCtrl };
            send(client, 6, up, keys);
        }
        finally
        {
            if (client != IntPtr.Zero) Marshal.Release(client);
            Marshal.Release(unknown);
        }
    }

    void Shutdown()
    {
        _closing = true;
        _retry.Stop();
        Open.Remove(_vm.Id);
        try
        {
            if ((int)_rdp.Client.Connected != 0) _rdp.Client.Disconnect();
        }
        catch { }
        _host.Dispose();
    }
}
