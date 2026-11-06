using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tman.Core;

namespace Tman.Monitoring;

/// <summary>
/// Per-process network rates from the Microsoft-Windows-Kernel-Network provider. No API gives these without a trace, so
/// this runs a session only while something wants the numbers; when it's off, the sampler pays for one field read.
/// </summary>
internal static unsafe class ProcessNetwork
{
    static readonly Guid Provider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;

    // Sent and received over TCP and UDP, each on IPv4 and IPv6. In all eight, the payload starts with PID, then size, both 32-bit.
    const ushort TcpSend4 = 10, TcpRecv4 = 11, TcpSend6 = 26, TcpRecv6 = 27, UdpSend4 = 42, UdpRecv4 = 43, UdpSend6 = 58, UdpRecv6 = 59;
    const int MinPayload = 8;

    static readonly object Gate = new(), DataGate = new();
    static volatile EtwTrace? _trace;
    static volatile bool _wanted, _running;
    static volatile string? _error;

    // Written by the trace's thread and swapped out by the sampler, so both sides take DataGate briefly.
    static Dictionary<int, long> _bytes = new();
    static Dictionary<int, long> _previous = new();
    static double _previousSeconds;

    /// <summary>Why the trace couldn't start, in plain words; null when it's fine.</summary>
    public static string? Error => _error;

    /// <summary>Starts or stops the trace in the background. Returns at once, so it's safe on the UI thread.</summary>
    public static void SetWanted(bool wanted)
    {
        if (_wanted == wanted) return;
        _wanted = wanted;
        Task.Run(Apply);
    }

    static void Apply()
    {
        lock (Gate)
        {
            try
            {
                if (_wanted && _trace == null)
                {
                    _error = null;
                    _trace = EtwTrace.StartProvider("Network", Provider, KeywordIpv4 | KeywordIpv6, OnEvent);
                    _running = true;
                }
                else if (!_wanted && _trace != null)
                {
                    _running = false;
                    _trace.Stop();
                    _trace = null;
                    Reset();
                }
            }
            catch (EtwFailure ex)
            {
                _error = ex.Message;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                ErrorLog.Write(ex);
            }
        }
    }

    static void Reset()
    {
        lock (DataGate)
        {
            _bytes.Clear();
            _previous.Clear();
            _previousSeconds = 0;
        }
    }

    /// <summary>Runs on the trace's own thread, once per event.</summary>
    internal static void OnEvent(EtwEvent* e)
    {
        switch (e->Id)
        {
            case TcpSend4 or TcpRecv4 or TcpSend6 or TcpRecv6 or UdpSend4 or UdpRecv4 or UdpSend6 or UdpRecv6: break;
            default: return;
        }
        if (e->UserDataLength < MinPayload || e->ProviderId != Provider) return;
        int pid = Unsafe.ReadUnaligned<int>(e->UserData);
        uint size = Unsafe.ReadUnaligned<uint>(e->UserData + 4);
        lock (DataGate) CollectionsMarshal.GetValueRefOrAddDefault(_bytes, pid, out _) += size;
    }

    /// <summary>
    /// Bytes per second per process since the last call, or null when the trace isn't running. ETW hands events over only
    /// when its buffers flush, once a second, so one sample can catch two flushes and the next none. Averaging the last
    /// two windows hides that. Called from the sampler thread only.
    /// </summary>
    public static Dictionary<int, double>? TakeRates(double seconds)
    {
        if (!_running) return null;
        if (_trace is { Running: false })
        {
            // Something else ended the session. An empty column is honest; a row of zeros isn't.
            _error ??= "The network trace stopped unexpectedly. Reopen this page to start it again.";
            return null;
        }
        lock (DataGate)
        {
            var rates = new Dictionary<int, double>(_bytes.Count + _previous.Count);
            double window = seconds + _previousSeconds;
            if (window > 0)
            {
                foreach (var (pid, bytes) in _bytes) rates[pid] = bytes / window;
                foreach (var (pid, bytes) in _previous) rates[pid] = rates.GetValueOrDefault(pid) + bytes / window;
            }
            (_bytes, _previous) = (_previous, _bytes);
            _bytes.Clear();
            _previousSeconds = seconds;
            return rates;
        }
    }
}
