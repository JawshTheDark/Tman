using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using Microsoft.Win32;

namespace Tman.Core;

/// <summary>A scheduled task as Task Scheduler reports it. The COM API never hands out stored passwords, so none can end up here.</summary>
public sealed record ScheduledTaskInfo(
    string Name, string Folder, string TaskPath, string State, DateTime? LastRun, int LastResult, DateTime? NextRun, string Author,
    string Action, string Description, string RunsAs, IReadOnlyList<string> Triggers, IReadOnlyList<string> Actions)
{
    public string LastRunText => LastRun?.ToString("g") ?? "Never";
    public string NextRunText => State == "Disabled" ? "Disabled" : NextRun?.ToString("g") ?? "Not scheduled";
    public string ResultText => ScheduledTaskList.ExplainResult(LastResult, LastRun != null);
    public bool Failed => LastRun != null && !ScheduledTaskList.IsFine(LastResult);
    public bool IsMicrosoft => TaskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || Author.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    public Brush ResultBrush => (Brush)Application.Current.FindResource(Failed ? "BadBrush" : LastRun != null && LastResult == 0 ? "GoodBrush" : "SubtleBrush");
    public Brush StateBrush => (Brush)Application.Current.FindResource(State == "Running" ? "GoodBrush" : State == "Disabled" ? "SubtleBrush" : State == "Ready" ? "LabelBrush" : "WarnBrush");
}

/// <summary>Reads and controls scheduled tasks through the Task Scheduler COM API. Everything here blocks, so call it off the UI thread.</summary>
public static class ScheduledTaskList
{
    /// <summary>Every task this user can see, in every folder, hidden ones included. Throws if Task Scheduler can't be reached.</summary>
    public static List<ScheduledTaskInfo> Read()
    {
        var list = new List<ScheduledTaskInfo>();
        dynamic service = Connect();
        try
        {
            Walk(list, service.GetFolder("\\"));
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
        return list;
    }

    public static void Run(string taskPath) => WithTask(taskPath, task => task.Run(null));
    public static void End(string taskPath) => WithTask(taskPath, task => task.Stop(0));
    public static void SetEnabled(string taskPath, bool enabled) => WithTask(taskPath, task => task.Enabled = enabled);

    /// <summary>True when the task is turned on, false when it's off, null when this PC doesn't have it or won't show it.</summary>
    public static bool? IsEnabled(string taskPath)
    {
        bool? enabled = null;
        try
        {
            WithTask(taskPath, task => enabled = (bool)task.Enabled);
        }
        catch { }
        return enabled;
    }

    static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available on this PC.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    static void WithTask(string taskPath, Action<dynamic> act)
    {
        dynamic service = Connect();
        try
        {
            int cut = taskPath.LastIndexOf('\\');
            dynamic folder = service.GetFolder(cut <= 0 ? "\\" : taskPath[..cut]);
            act(folder.GetTask(taskPath[(cut + 1)..]));
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    static void Walk(List<ScheduledTaskInfo> list, dynamic folder)
    {
        try
        {
            foreach (dynamic task in folder.GetTasks(1 /* include hidden */))
            {
                try { list.Add(ReadTask(task)); } catch { }
            }
        }
        catch { }
        try
        {
            foreach (dynamic sub in folder.GetFolders(0)) Walk(list, sub);
        }
        catch { }
    }

    static ScheduledTaskInfo ReadTask(dynamic task)
    {
        string path = task.Path, name = task.Name;
        int cut = path.LastIndexOf('\\');
        string folder = cut <= 0 ? "\\" : path[..cut];

        string author = "", description = "", runsAs = "", action = "";
        var triggers = new List<string>();
        var actions = new List<string>();
        try
        {
            dynamic def = task.Definition;
            author = Resource((string?)def.RegistrationInfo.Author);
            description = Resource((string?)def.RegistrationInfo.Description);
            runsAs = RunsAs(def.Principal);
            foreach (dynamic t in def.Triggers)
            {
                try { triggers.Add(DescribeTrigger(t)); } catch { triggers.Add("A trigger Tman can't describe"); }
            }
            foreach (dynamic a in def.Actions)
            {
                try
                {
                    (string summary, string detail) = ((string, string))DescribeAction(a);
                    if (action.Length == 0) action = summary;
                    actions.Add(detail);
                }
                catch { actions.Add("An action Tman can't describe"); }
            }
        }
        catch
        {
            // Windows lets some users see that a task exists without reading its definition.
            action = "Details need administrator rights";
        }

        return new ScheduledTaskInfo(name, folder, path, StateName((int)task.State), Date((DateTime)task.LastRunTime), (int)task.LastTaskResult,
            Date((DateTime)task.NextRunTime), author, action, description, runsAs, triggers, actions);
    }

    /// <summary>Task Scheduler reports "never" as a date long before it existed (1899 or 1999).</summary>
    static DateTime? Date(DateTime d) => d.Year < 2001 ? null : d;

    static string StateName(int state) => state switch
    {
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => "Unknown",
    };

    static string RunsAs(dynamic principal)
    {
        string user = (string?)principal.UserId ?? "", group = (string?)principal.GroupId ?? "";
        string who = user.Length > 0 ? user : group;
        if ((int)principal.RunLevel == 1 && who.Length > 0) who += " (with highest privileges)";
        return who;
    }

    static string Resource(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Windows' own tasks store text as "$(@%SystemRoot%\system32\x.dll,-101)" or "@%SystemRoot%\system32\x.dll,-101".
        var indirect = text.StartsWith("$(@", StringComparison.Ordinal) && text.EndsWith(')') ? text[2..^1] : text.StartsWith('@') ? text : null;
        if (indirect == null) return text;
        var buffer = new StringBuilder(1024);
        return SHLoadIndirectString(indirect, buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : "";
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);

    static (string Summary, string Detail) DescribeAction(dynamic a)
    {
        int type = a.Type;
        if (type == 0)
        {
            string path = (string?)a.Path ?? "", args = (string?)a.Arguments ?? "", dir = (string?)a.WorkingDirectory ?? "";
            var line = args.Length > 0 ? $"{path} {args}" : path;
            return (line, dir.Length > 0 ? $"{line}\nStart in: {dir}" : line);
        }
        if (type == 5)
        {
            string id = (string?)a.ClassId ?? "", data = (string?)a.Data ?? "";
            var known = ComName(id);
            var line = known.Length > 0 ? $"Runs a built-in handler: {known} {id}" : $"Runs a built-in handler: {id}";
            return (known.Length > 0 ? known : "Runs a built-in handler", data.Length > 0 ? $"{line}\nData: {data}" : line);
        }
        return type == 6 ? ("Sends an email", "Sends an email") : type == 7 ? ("Shows a message", "Shows a message") : ("Unknown action", "Unknown action");
    }

    static string ComName(string classId)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{classId}");
            return key?.GetValue(null) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    static readonly string[] WeekDays = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
    static readonly string[] WeekNames = { "first", "second", "third", "fourth" };

    static string DescribeTrigger(dynamic t)
    {
        int type = t.Type;
        DateTimeOffset? start = DateTimeOffset.TryParse((string?)t.StartBoundary, out DateTimeOffset parsed) ? parsed : null;
        string at = start is { } s ? " at " + s.ToString("t") : "";
        string text;
        switch (type)
        {
            case 0: text = "When a specific event is logged"; break;
            case 1: text = start is { } once ? "Once on " + once.ToString("g") : "Once"; break;
            case 2:
                int days = t.DaysInterval;
                text = (days > 1 ? $"Every {days} days" : "Every day") + at;
                break;
            case 3:
                int weeks = t.WeeksInterval;
                text = (weeks > 1 ? $"Every {weeks} weeks on " : "Every week on ") + DaysOfWeek((int)t.DaysOfWeek) + at;
                break;
            case 4:
                text = "Monthly on day " + string.Join(", ", Bits((int)t.DaysOfMonth).Select(b => b + 1)) + at;
                break;
            case 5:
                var which = Bits((int)t.WeeksOfMonth).Where(b => b < 4).Select(b => WeekNames[b]).ToList();
                if ((bool)t.RunOnLastWeekOfMonth) which.Add("last");
                text = $"Monthly on the {string.Join(", ", which)} {DaysOfWeek((int)t.DaysOfWeek)}{at}";
                break;
            case 6: text = "When the PC is idle"; break;
            case 7: text = "When the task is created or changed"; break;
            case 8: text = "At startup"; break;
            case 9:
                string user = (string?)t.UserId ?? "";
                text = user.Length > 0 ? $"When {user} signs in" : "When any user signs in";
                break;
            case 11: text = SessionChange((int)t.StateChange); break;
            default: text = "On a system condition"; break;
        }

        try
        {
            string every = Every((string?)t.Repetition.Interval);
            if (every.Length > 0) text += $", repeating every {every}";
        }
        catch { }
        if (!(bool)t.Enabled) text += " (turned off)";
        return text;
    }

    static string SessionChange(int change) => change switch
    {
        1 => "When a user connects at the PC",
        2 => "When a user disconnects at the PC",
        3 => "When a remote user connects",
        4 => "When a remote user disconnects",
        7 => "When the PC is locked",
        8 => "When the PC is unlocked",
        _ => "When a user session changes",
    };

    static string Every(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return "";
        try
        {
            var t = XmlConvert.ToTimeSpan(duration);
            return t.TotalDays >= 1 ? Unit(t.TotalDays, "day") : t.TotalHours >= 1 ? Unit(t.TotalHours, "hour") : Unit(t.TotalMinutes, "minute");
        }
        catch
        {
            return "";
        }
    }

    static string Unit(double n, string unit) => n == 1 ? unit : $"{n:0.#} {unit}s";

    static string DaysOfWeek(int mask) => string.Join(", ", Bits(mask).Where(b => b < 7).Select(b => WeekDays[b]));

    static IEnumerable<int> Bits(int mask) => Enumerable.Range(0, 32).Where(b => (mask & (1 << b)) != 0);

    /// <summary>Results that mean the task itself didn't fail: success, or a status such as "still running" or "ended by you".</summary>
    public static bool IsFine(int code) => code == 0 || (uint)code is >= 0x41300 and <= 0x41308 or 0x41325 or 0x4131B or 0x4131C or 0x40010004 or 0xC000013A;

    public static string ExplainResult(int code, bool hasRun)
    {
        if (!hasRun) return "Hasn't run yet";
        return (uint)code switch
        {
            0 => "OK",
            0x41300 => "Ready to run",
            0x41301 => "Running now",
            0x41302 => "Task is disabled",
            0x41303 => "Hasn't run yet",
            0x41304 => "No more runs scheduled",
            0x41305 => "No schedule set",
            0x41306 => "Ended by a user",
            0x41307 => "No valid triggers",
            0x41325 => "Queued to run",
            0x8004130F => "Failed: the saved account details are missing",
            0x80041310 => "Failed: the account it runs as no longer exists",
            0x80041315 => "Failed: Task Scheduler wasn't running",
            0x8004131F => "Skipped: it was already running",
            0x80041320 => "Failed: the user wasn't signed in",
            0x80041321 => "Failed: the task file was changed outside Task Scheduler",
            0x80041322 => "Failed: Task Scheduler wasn't available",
            0x80041323 => "Failed: Task Scheduler was too busy",
            0x80041326 => "Failed: the task is disabled",
            0x800704DD => "Failed: the user wasn't signed in",
            0x800710E0 => "Failed: Windows refused to start it (a condition wasn't met or it was already running)",
            0x8007010B => "Failed: the Start in folder is wrong or missing",
            0x80040154 or 0x80040111 => "Failed: a component it needs isn't installed",
            0x40010004 => "Stopped: its program was ended, usually by a sign-out or shutdown",
            0xC000013A => "Stopped: its program was closed",
            0xC0000005 => "Failed: its program crashed",
            0xC0000142 => "Failed: its program couldn't start",
            0xE0434352 => "Failed: its .NET program crashed",
            _ => Generic(code),
        };
    }

    static string Generic(int code)
    {
        if (code > 0) return code < 0x10000 ? $"Failed: its program exited with code {code}" : $"Failed: its program exited with code 0x{code:X}";
        if (((uint)code & 0xFFFF0000) == 0x80070000)
            return $"Failed: {Marshal.GetPInvokeErrorMessage(code & 0xFFFF).TrimEnd('.', ' ')} (0x{code:X8})";
        return $"Failed with code 0x{code:X8}";
    }
}
