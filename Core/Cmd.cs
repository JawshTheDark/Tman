using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Tman.Core;

/// <summary>
/// Runs Windows' console tools for the jobs that have no API. Output is decoded whichever way the tool wrote it:
/// most use the OEM code page, but a few (sfc) write UTF-16 when redirected.
/// </summary>
public static class Cmd
{
    public sealed record Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Exit code reported when the tool couldn't be started; the reason arrives as an output line.</summary>
    public const int FailedToStart = -1;

    /// <summary>Exit code reported when the tool was stopped by cancellation or a timeout.</summary>
    public const int Cancelled = -2;

    static Cmd() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>A tool in System32, by file name.</summary>
    public static string System(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>Runs a tool to completion and returns what it printed, without blank lines. Blocks, so call it off the UI thread. Never throws.</summary>
    public static Result Run(string file, string args, int timeoutMs = 30000)
    {
        var output = new StringBuilder();
        using var timeout = new CancellationTokenSource(timeoutMs);
        int code = StreamAsync(file, args, line =>
        {
            lock (output) output.AppendLine(line);
        }, timeout.Token).GetAwaiter().GetResult();
        return new Result(code, output.ToString());
    }

    /// <summary>
    /// Runs a tool and reports each line of stdout and stderr as it appears, on a background thread. A carriage return
    /// also ends a line, so a percentage that a tool redraws in place arrives as a series of updates. Never throws.
    /// </summary>
    public static async Task<int> StreamAsync(string file, string args, Action<string> onLine, CancellationToken cancel = default)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            });
            if (process == null)
            {
                onLine($"Couldn't start {Path.GetFileName(file)}.");
                return FailedToStart;
            }
            process.StandardInput.Close();
            var running = process;
            using var stop = cancel.Register(() =>
            {
                try { running.Kill(entireProcessTree: true); } catch { }
            });
            await Task.WhenAll(
                Pump(process.StandardOutput.BaseStream, onLine),
                Pump(process.StandardError.BaseStream, onLine),
                process.WaitForExitAsync()).ConfigureAwait(false);
            return cancel.IsCancellationRequested ? Cancelled : process.ExitCode;
        }
        catch (Exception ex)
        {
            onLine($"Couldn't run {Path.GetFileName(file)}: {ex.Message}");
            return FailedToStart;
        }
        finally
        {
            process?.Dispose();
        }
    }

    static async Task Pump(Stream stream, Action<string> onLine)
    {
        var bytes = new byte[4096];
        var chars = new char[8192];
        var line = new StringBuilder();
        Decoder? decoder = null;
        int read;
        while ((read = await stream.ReadAsync(bytes).ConfigureAwait(false)) > 0)
        {
            decoder ??= Detect(bytes, read).GetDecoder();
            int count = decoder.GetChars(bytes, 0, read, chars, 0);
            for (int i = 0; i < count; i++)
            {
                char c = chars[i];
                if (c != '\r' && c != '\n')
                {
                    if (c != '\0') line.Append(c);
                    continue;
                }
                if (line.Length > 0) onLine(line.ToString());
                line.Clear();
            }
        }
        if (line.Length > 0) onLine(line.ToString());
    }

    /// <summary>UTF-16 text in a mostly-Latin alphabet is about half zero bytes; OEM text has none.</summary>
    static Encoding Detect(byte[] bytes, int count)
    {
        int zeros = 0;
        for (int i = 0; i < count; i++)
            if (bytes[i] == 0) zeros++;
        return zeros * 4 > count ? Encoding.Unicode : Oem;
    }

    /// <summary>Console tools use the system's OEM code page, whatever the regional format says. A failure here would kill the pump, and the tool would block on a full pipe.</summary>
    static Encoding Oem
    {
        get
        {
            try { return Encoding.GetEncoding((int)GetOEMCP()); }
            catch { return Encoding.Default; }
        }
    }

    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();
}
