using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tman.Core;

/// <summary>What has to happen before a tweak is visible.</summary>
public enum TweakEffect { None, Explorer, SignOut, Restart }

/// <summary>The smallest preset a tweak belongs to. Each preset includes the ones before it.</summary>
public enum TweakPreset { None, Minimal, Standard, Advanced }

public enum TweakState { Off, On, Partly, Locked, Unknown }

/// <summary>One registry value a tweak controls. A null On or Off means the value doesn't exist in that state.</summary>
public sealed record RegStep(string Key, string Name, object? On, object? Off);

public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required string About { get; init; }
    public TweakPreset Preset { get; init; }
    public TweakEffect Effect { get; init; }
    public RegStep[] Values { get; init; } = Array.Empty<RegStep>();

    /// <summary>For tweaks that aren't plain registry values. Read returns null when the state can't be determined.</summary>
    public Func<bool?>? Read { get; init; }

    /// <summary>Returns an error message, or null on success.</summary>
    public Func<bool, string?>? Apply { get; init; }

    public bool CustomNeedsAdmin { get; init; }

    public bool NeedsAdmin => CustomNeedsAdmin || Values.Any(v => v.Key.StartsWith(@"HKLM\") || v.Key.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads and applies the settings on the Tweaks page. Before the first change to a tweak its old values are saved to
/// <c>tweaks-undo.json</c>, so turning it off puts back exactly what was there, not just Windows' default.
/// </summary>
public static class Tweaks
{
    /// <summary>A registry value as it was before Tman changed it. A null Kind means it didn't exist.</summary>
    public sealed record Saved(string Key, string Name, RegistryValueKind? Kind, string? Data);

    /// <summary>Redirects registry paths, so tests can run against a throwaway key.</summary>
    internal static Func<string, string> KeyMap { get; set; } = k => k;

    internal static string UndoPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "tweaks-undo.json");

    static readonly object Gate = new();
    static Dictionary<string, List<Saved>>? _undo;

    static Dictionary<string, List<Saved>> Undo
    {
        get
        {
            if (_undo != null) return _undo;
            try
            {
                if (File.Exists(UndoPath))
                    _undo = JsonSerializer.Deserialize<Dictionary<string, List<Saved>>>(File.ReadAllText(UndoPath));
            }
            catch { }
            return _undo ??= new();
        }
    }

    /// <summary>True when Tman changed this tweak and can put it back.</summary>
    public static bool HasUndo(Tweak t)
    {
        lock (Gate) return Undo.ContainsKey(t.Id);
    }

    public static TweakState Read(Tweak t)
    {
        try
        {
            if (t.Read != null)
                return t.Read() switch { true => TweakState.On, false => TweakState.Off, _ => TweakState.Unknown };

            bool allOn = true, allOff = true, locked = false;
            foreach (var step in t.Values)
            {
                var now = Get(step.Key, step.Name);
                bool on = Same(now, step.On);
                // A missing value means Windows is using its default, which is what Off describes.
                bool off = now == null || Same(now, step.Off);
                allOn &= on;
                allOff &= off;
                if (!on && !off && step.Key.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase)) locked = true;
            }
            return locked ? TweakState.Locked : allOn ? TweakState.On : allOff ? TweakState.Off : TweakState.Partly;
        }
        catch
        {
            return TweakState.Unknown;
        }
    }

    /// <summary>Turns a tweak on or off. Returns an error message, or null on success. Blocks.</summary>
    public static string? Set(Tweak t, bool on)
    {
        lock (Gate)
        {
            try
            {
                if (t.Apply != null) return SetCustom(t, on);
                if (on)
                {
                    bool first = !Undo.ContainsKey(t.Id);
                    if (first)
                    {
                        Undo[t.Id] = t.Values.Select(Capture).ToList();
                        Save();
                    }
                    try
                    {
                        Write(t, step => (step.Key, step.Name, step.On));
                    }
                    catch when (first)
                    {
                        // Nothing changed, so there's nothing to undo.
                        Undo.Remove(t.Id);
                        Save();
                        throw;
                    }
                }
                else if (Undo.TryGetValue(t.Id, out var saved))
                {
                    foreach (var value in saved) Restore(value);
                    Undo.Remove(t.Id);
                    Save();
                }
                else
                {
                    Write(t, step => (step.Key, step.Name, step.Off));
                }
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return "Windows refused the change. It may need administrator rights, or Windows may be protecting this setting.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    static string? SetCustom(Tweak t, bool on)
    {
        var error = t.Apply!(on);
        if (error != null) return error;
        if (on) Undo.TryAdd(t.Id, new List<Saved>());
        else Undo.Remove(t.Id);
        Save();
        return null;
    }

    /// <summary>Writes every value, putting back the ones already written if one fails, so a tweak never ends up half applied.</summary>
    static void Write(Tweak t, Func<RegStep, (string Key, string Name, object? Value)> pick)
    {
        var before = t.Values.Select(Capture).ToList();
        try
        {
            foreach (var step in t.Values)
            {
                var (key, name, value) = pick(step);
                Put(key, name, value);
            }
        }
        catch
        {
            foreach (var value in before)
            {
                try { Restore(value); } catch { }
            }
            throw;
        }
    }

    // ---- Registry ----

    static RegistryKey Hive(string key) => key.StartsWith(@"HKLM\") ? Registry.LocalMachine : Registry.CurrentUser;

    static object? Get(string key, string name)
    {
        var mapped = KeyMap(key);
        using var k = Hive(mapped).OpenSubKey(mapped[5..]);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    static void Put(string key, string name, object? value)
    {
        var mapped = KeyMap(key);
        if (value == null)
        {
            using var existing = Hive(mapped).OpenSubKey(mapped[5..], writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        using var k = Hive(mapped).CreateSubKey(mapped[5..], writable: true);
        k.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    static bool Same(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is string || b is string) return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
        try { return Convert.ToInt64(a) == Convert.ToInt64(b); }
        catch { return false; }
    }

    static Saved Capture(RegStep step)
    {
        var mapped = KeyMap(step.Key);
        using var k = Hive(mapped).OpenSubKey(mapped[5..]);
        var value = k?.GetValue(step.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (k == null || value == null) return new Saved(step.Key, step.Name, null, null);
        var kind = k.GetValueKind(step.Name);
        string data = value switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            string[] lines => string.Join('\0', lines),
            _ => value.ToString() ?? "",
        };
        return new Saved(step.Key, step.Name, kind, data);
    }

    static void Restore(Saved value)
    {
        if (value.Kind == null)
        {
            Put(value.Key, value.Name, null);
            return;
        }
        var mapped = KeyMap(value.Key);
        object data = value.Kind switch
        {
            RegistryValueKind.DWord => unchecked((int)long.Parse(value.Data!)), // large DWORDs come back from the registry negative
            RegistryValueKind.QWord => long.Parse(value.Data!),
            RegistryValueKind.Binary => Convert.FromBase64String(value.Data!),
            RegistryValueKind.MultiString => value.Data!.Split('\0'),
            _ => value.Data!,
        };
        using var k = Hive(mapped).CreateSubKey(mapped[5..], writable: true);
        k.SetValue(value.Name, data, value.Kind.Value);
    }

    static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UndoPath)!);
        File.WriteAllText(UndoPath, JsonSerializer.Serialize(Undo, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- System ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint message, IntPtr wParam, string? lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Tells running apps and Explorer that settings changed, which applies many tweaks straight away. Blocks briefly.</summary>
    public static void Broadcast()
    {
        const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 2;
        IntPtr everyone = new(0xFFFF);
        SendMessageTimeoutW(everyone, WM_SETTINGCHANGE, IntPtr.Zero, null, SMTO_ABORTIFHUNG, 1000, out _);
        SendMessageTimeoutW(everyone, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _);
        SendMessageTimeoutW(everyone, WM_SETTINGCHANGE, IntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 1000, out _);
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Returns an error message, or null on success. Blocks for up to a minute or so. Needs admin.</summary>
    public static string? CreateRestorePoint(string description)
    {
        const uint ModifySettings = 12, BeginSystemChange = 100;
        try
        {
            ulong newest = NewestRestorePoint();
            using var restore = new ManagementClass(@"\\.\root\default:SystemRestore");
            var code = Convert.ToUInt32(restore.InvokeMethod("CreateRestorePoint", new object[] { description, ModifySettings, BeginSystemChange }));
            if (code != 0) return $"System Restore returned error {code}. It may be turned off for this drive.";
            // Windows also reports success when it skips one because the last is too recent (by default, within 24 hours).
            return NewestRestorePoint() > newest ? null : "Windows didn't make a new one. It allows one every 24 hours by default, so the latest restore point may be recent, or System Protection may be off.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    static ulong NewestRestorePoint() =>
        Wmi.Query("SELECT SequenceNumber FROM SystemRestore", @"root\default").Select(o => o.U64("SequenceNumber")).DefaultIfEmpty().Max();

    // ---- Catalog ----

    const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string Suggestions = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string Policies = @"SOFTWARE\Policies\Microsoft\Windows";
    const string ClassicMenuRoot = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    const string UltimatePlanTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    /// <summary>Tman's copy of the plan. Windows translates the plan's name, so a fixed ID is how to recognize it in any language.</summary>
    const string UltimatePlanCopy = "b1a7c0de-54a4-4d6e-8f0e-746d616e0001";

    const string PrivacyGroup = "Privacy";
    const string AdsGroup = "Ads and suggestions";
    const string ExplorerGroup = "File Explorer";
    const string TaskbarGroup = "Taskbar and Start";
    const string PerformanceGroup = "Performance and gaming";
    const string InputGroup = "Mouse, keyboard and look";
    const string SystemGroup = "System";
    const string AiGroup = "AI features";
    const string MenuGroup = "Right-click menu";
    const string NotificationsGroup = "Notifications";
    const string UpdateGroup = "Windows Update";
    const string SecurityGroup = "Security";

    const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";
    const string SearchSettings = $@"{CurrentVersion}\SearchSettings";
    const string NotificationSettings = $@"{CurrentVersion}\Notifications\Settings";
    const string ExplorerKey = $@"{CurrentVersion}\Explorer";
    const string BlockedMenus = $@"{CurrentVersion}\Shell Extensions\Blocked";
    const string ConsentStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    const string SystemPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const string ExplorerPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    const string Multimedia = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string CrashControl = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    const string Services = @"SYSTEM\CurrentControlSet\Services";
    const string Desktop = @"Control Panel\Desktop";
    const string GameBar = @"Software\Microsoft\GameBar";

    static RegStep User(string key, string name, object? on, object? off) => new($@"HKCU\{key}", name, on, off);
    static RegStep Machine(string key, string name, object? on, object? off) => new($@"HKLM\{key}", name, on, off);

    /// <summary>Every suggestion switch shares one key, and each is 1 when Windows' default is on.</summary>
    static RegStep[] AdSwitches(params string[] names) => names.Select(n => User(Suggestions, n, 0, 1)).ToArray();

    /// <summary>The PC-wide switch for each kind of data apps can ask for.</summary>
    static RegStep[] AppAccess(params string[] names) => names.Select(n => Machine($@"{ConsentStore}\{n}", "Value", "Deny", "Allow")).ToArray();

    /// <summary>Windows skips any menu handler listed here. The handlers come from Windows and from each app's manifest.</summary>
    static Tweak HideMenuItem(string id, string name, string about, TweakPreset preset, params string[] handlers) => new()
    {
        Id = id, Group = MenuGroup, Preset = preset, Effect = TweakEffect.Explorer, Name = name, About = about,
        Values = handlers.Select(h => User(BlockedMenus, h, "", null)).ToArray(),
    };

    /// <summary>
    /// Each accessibility shortcut is one bit of a number stored as text, beside the feature's other settings. Only that bit
    /// changes, and any value with it clear counts as on, since Windows writes several forms of "shortcut off".
    /// </summary>
    static Tweak ShortcutOff(string id, string name, string about, string key, int windowsDefault)
    {
        const int ShortcutBit = 4;
        int Flags() => int.TryParse(Get($@"HKCU\{key}", "Flags") as string, out var flags) ? flags : windowsDefault;
        return new()
        {
            Id = id, Group = InputGroup, Effect = TweakEffect.SignOut, Name = name, About = about,
            Read = () => (Flags() & ShortcutBit) == 0,
            Apply = on =>
            {
                Put($@"HKCU\{key}", "Flags", (on ? Flags() & ~ShortcutBit : Flags() | ShortcutBit).ToString());
                return null;
            },
        };
    }

    /// <summary>The background jobs that gather compatibility, feedback and error data. Some PCs don't have them all.</summary>
    static readonly string[] TelemetryTasks =
    {
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Autochk\Proxy",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Feedback\Siuf\DmClient",
        @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
    };

    public static readonly Tweak[] All =
    {
        // Privacy
        new()
        {
            Id = "telemetry", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Send only required diagnostic data",
            About = "Sets diagnostic data to the lowest level your edition allows. On Home and Pro that's Required.",
            Values = new[] { Machine($@"{Policies}\DataCollection", "AllowTelemetry", 0, null) },
        },
        new()
        {
            Id = "activity", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off activity history",
            About = "Stops Windows recording what you do in apps and files.",
            Values = new[]
            {
                Machine($@"{Policies}\System", "EnableActivityFeed", 0, null),
                Machine($@"{Policies}\System", "PublishUserActivities", 0, null),
                Machine($@"{Policies}\System", "UploadUserActivities", 0, null),
            },
        },
        new()
        {
            Id = "advertising-id", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off advertising ID",
            About = "Stops apps using an ID that tracks you for ads.",
            Values = new[] { User(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1) },
        },
        new()
        {
            Id = "tailored", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off tailored experiences",
            About = "Stops Microsoft using your diagnostic data for tips and offers.",
            Values = new[] { User(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1) },
        },
        new()
        {
            Id = "feedback", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off feedback requests",
            About = "Stops Windows asking for your feedback.",
            Values = new[] { User(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null) },
        },
        new()
        {
            Id = "powershell-telemetry", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off PowerShell 7 telemetry",
            About = "Sets the variable that stops PowerShell 7 sending usage data. New PowerShell windows pick it up.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "POWERSHELL_TELEMETRY_OPTOUT", "1", null) },
        },
        new()
        {
            Id = "app-tracking", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off app launch tracking",
            About = "Stops Windows tracking which apps you open. Start and search won't show your most-used apps.",
            Values = new[] { User(Advanced, "Start_TrackProgs", 0, 1) },
        },
        new()
        {
            Id = "online-speech", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off online speech recognition",
            About = "Stops your voice being sent to Microsoft. Voice typing stops working.",
            Values = new[] { User(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0, 1) },
        },
        new()
        {
            Id = "inking", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off inking and typing personalization",
            About = "Stops Windows building a personal dictionary from what you type and write.",
            Values = new[]
            {
                User(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0),
                User(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),
                User(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, 1),
                User(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, 1),
            },
        },
        new()
        {
            Id = "delivery-optimization", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Stop sharing updates with other PCs",
            About = "Windows downloads updates only from Microsoft and doesn't upload them to other PCs.",
            Values = new[] { Machine($@"{Policies}\DeliveryOptimization", "DODownloadMode", 0, null) },
        },
        new()
        {
            Id = "location", Group = PrivacyGroup, Preset = TweakPreset.Advanced,
            Name = "Turn off location for all apps",
            About = "Maps, weather and Find my device can't see where you are.",
            Values = new[] { Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", "Allow") },
        },
        new()
        {
            Id = "diagtrack", Group = PrivacyGroup, Preset = TweakPreset.Advanced, Effect = TweakEffect.Restart,
            Name = "Turn off the diagnostics tracking service",
            About = "Disables Connected User Experiences and Telemetry. Some troubleshooters may report less.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4, 2) },
        },
        new()
        {
            Id = "language-list", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Hide your language list from websites",
            About = "Websites can't read which languages you have installed.",
            Values = new[] { User(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, 0) },
        },
        new()
        {
            Id = "ceip", Group = PrivacyGroup, Preset = TweakPreset.Minimal,
            Name = "Leave the Customer Experience Improvement Program",
            About = "Stops Windows sending this program's usage reports.",
            Values = new[] { Machine(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0, null) },
        },
        new()
        {
            Id = "app-telemetry", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off application telemetry",
            About = "Stops Windows reporting how apps are used and sending Microsoft a list of installed programs and drivers.",
            Values = new[]
            {
                Machine($@"{Policies}\AppCompat", "AITEnable", 0, null),
                Machine($@"{Policies}\AppCompat", "DisableInventory", 1, null),
            },
        },
        new()
        {
            Id = "telemetry-tasks", Group = PrivacyGroup, Preset = TweakPreset.Standard, CustomNeedsAdmin = true,
            Name = "Turn off telemetry scheduled tasks",
            About = "Stops the background jobs that collect compatibility, feedback and error data. Tasks this PC doesn't have are skipped, and turning this off restarts only the ones Tman stopped.",
            Read = TelemetryTasksOff,
            Apply = SetTelemetryTasks,
        },
        new()
        {
            Id = "typing-insights", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off typing insights",
            About = "Stops Windows keeping statistics on how you type.",
            Values = new[] { User(@"Software\Microsoft\input\Settings", "InsightsEnabled", 0, 1) },
        },
        new()
        {
            Id = "search-history", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off search history on this PC",
            About = "Start search stops using your past searches.",
            Values = new[] { User(SearchSettings, "IsDeviceSearchHistoryEnabled", 0, 1) },
        },
        new()
        {
            Id = "cloud-search", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off cloud content search",
            About = "Start search stops including content from your Microsoft and work accounts.",
            Values = new[]
            {
                User(SearchSettings, "IsMSACloudSearchEnabled", 0, 1),
                User(SearchSettings, "IsAADCloudSearchEnabled", 0, 1),
            },
        },
        new()
        {
            Id = "clipboard-sync", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Stop syncing the clipboard between devices",
            About = "What you copy stays on this PC.",
            Values = new[] { Machine($@"{Policies}\System", "AllowCrossDeviceClipboard", 0, null) },
        },
        new()
        {
            Id = "sign-in-info", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Don't sign in automatically after updates",
            About = "Windows stops using your sign-in to finish setting up after an update restart.",
            Values = new[] { Machine(SystemPolicies, "DisableAutomaticRestartSignOn", 1, 0) },
        },
        new()
        {
            Id = "resume", Group = PrivacyGroup, Preset = TweakPreset.Standard,
            Name = "Turn off Resume",
            About = "Stops offers to continue on this PC what you were doing on your phone.",
            Values = new[] { User($@"{CurrentVersion}\CrossDeviceResume\Configuration", "IsResumeAllowed", 0, 1) },
        },
        new()
        {
            Id = "phone-link", Group = PrivacyGroup,
            Name = "Turn off phone linking",
            About = "Stops Phone Link connecting this PC to your phone.",
            Values = new[] { Machine($@"{Policies}\System", "EnableMmx", 0, null) },
        },
        new()
        {
            Id = "shared-experiences", Group = PrivacyGroup,
            Name = "Turn off shared experiences",
            About = "Stops apps continuing across your devices. Nearby sharing stops working.",
            Values = new[] { Machine($@"{Policies}\System", "EnableCdp", 0, null) },
        },
        new()
        {
            Id = "find-my-device", Group = PrivacyGroup,
            Name = "Turn off Find my device",
            About = "You won't be able to locate or lock this PC from your Microsoft account.",
            Values = new[] { Machine(@"SOFTWARE\Policies\Microsoft\FindMyDevice", "AllowFindMyDevice", 0, null) },
        },
        new()
        {
            Id = "error-reporting", Group = PrivacyGroup, Preset = TweakPreset.Advanced,
            Name = "Turn off Windows Error Reporting",
            About = "Stops crash reports going to Microsoft. Crash history in Tman may show fewer app crashes.",
            Values = new[] { Machine($@"{Policies}\Windows Error Reporting", "Disabled", 1, null) },
        },
        new()
        {
            Id = "settings-sync", Group = PrivacyGroup,
            Name = "Stop syncing Windows settings",
            About = "Windows stops copying settings like your theme to your Microsoft account.",
            Values = new[]
            {
                Machine($@"{Policies}\SettingSync", "DisableSettingSync", 2, null),
                Machine($@"{Policies}\SettingSync", "DisableSettingSyncUserOverride", 1, null),
            },
        },
        new()
        {
            Id = "app-personal-data", Group = PrivacyGroup, Preset = TweakPreset.Advanced,
            Name = "Block apps from your contacts, calendar, email and messages",
            About = "Store apps lose access to contacts, calendar, call history, email, tasks and messages, including Microsoft's own.",
            Values = AppAccess("contacts", "appointments", "phoneCallHistory", "email", "userDataTasks", "chat"),
        },
        new()
        {
            Id = "app-diagnostics", Group = PrivacyGroup, Preset = TweakPreset.Advanced,
            Name = "Stop apps reading information about other apps",
            About = "Store apps can't see what else is running.",
            Values = AppAccess("appDiagnostics"),
        },

        // AI features
        new()
        {
            Id = "recall", Group = AiGroup, Preset = TweakPreset.Minimal, Effect = TweakEffect.Restart,
            Name = "Turn off Recall",
            About = "Stops Windows saving snapshots of your screen. Only Copilot+ PCs have Recall.",
            Values = new[]
            {
                Machine($@"{Policies}\WindowsAI", "DisableAIDataAnalysis", 1, null),
                Machine($@"{Policies}\WindowsAI", "AllowRecallEnablement", 0, null),
            },
        },
        new()
        {
            Id = "copilot", Group = AiGroup, Preset = TweakPreset.Standard, Effect = TweakEffect.Explorer,
            Name = "Turn off Copilot in Windows",
            About = "Hides the Copilot button and sets Windows' policy. To remove the app itself, use Get apps.",
            Values = new[]
            {
                User(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                Machine($@"{Policies}\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                User(Advanced, "ShowCopilotButton", 0, 1),
            },
        },
        new()
        {
            Id = "click-to-do", Group = AiGroup, Preset = TweakPreset.Standard,
            Name = "Turn off Click to Do",
            About = "Removes the shortcut that reads your screen and offers actions.",
            Values = new[] { Machine($@"{Policies}\WindowsAI", "DisableClickToDo", 1, null) },
        },
        new()
        {
            Id = "paint-ai", Group = AiGroup, Preset = TweakPreset.Standard,
            Name = "Turn off AI tools in Paint",
            About = "Removes Cocreator, Generative fill and Image Creator. The Copilot button may stay.",
            Values = new[]
            {
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1, null),
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1, null),
                Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1, null),
            },
        },
        new()
        {
            Id = "notepad-ai", Group = AiGroup, Preset = TweakPreset.Standard,
            Name = "Turn off AI tools in Notepad",
            About = "Removes rewrite, summarize and the other AI features.",
            Values = new[] { Machine(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1, null) },
        },
        new()
        {
            Id = "ai-actions", Group = AiGroup, Preset = TweakPreset.Standard, Effect = TweakEffect.Explorer,
            Name = "Hide AI actions in the right-click menu",
            About = "Newer Windows builds only. Older ones ignore this.",
            Values = new[] { Machine($@"{Policies}\Explorer", "HideAIActionsMenu", 1, null) },
        },

        // Ads and suggestions
        new()
        {
            Id = "suggestions", Group = AdsGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off suggestions and silent app installs",
            About = "Removes tips and promoted apps in Start, Settings and the lock screen, and stops Windows adding suggested apps.",
            Values = AdSwitches("SilentInstalledAppsEnabled", "SystemPaneSuggestionsEnabled", "SoftLandingEnabled", "OemPreInstalledAppsEnabled",
                "PreInstalledAppsEnabled", "RotatingLockScreenOverlayEnabled", "SubscribedContent-338387Enabled", "SubscribedContent-338388Enabled",
                "SubscribedContent-338389Enabled", "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled"),
        },
        new()
        {
            Id = "finish-setup", Group = AdsGroup, Preset = TweakPreset.Minimal,
            Name = "Stop \"finish setting up\" prompts",
            About = "Stops the full-screen prompts that push Microsoft services after updates.",
            Values = new[] { User(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, 1) },
        },
        new()
        {
            Id = "web-search", Group = AdsGroup, Preset = TweakPreset.Minimal, Effect = TweakEffect.Explorer,
            Name = "Turn off web results in Start search",
            About = "Start searches only your PC, not Bing.",
            Values = new[]
            {
                User(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
                User(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, 1),
            },
        },
        new()
        {
            Id = "edge", Group = AdsGroup, Preset = TweakPreset.Advanced,
            Name = "Trim Microsoft Edge",
            About = "Turns off Edge's sidebar, shopping prompts, first-run pages and background running. Edge will say it's managed by your organization.",
            Values = new[]
            {
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "HideFirstRunExperience", 1, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, null),
            },
        },
        new()
        {
            Id = "explorer-ads", Group = AdsGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off ads in File Explorer",
            About = "Stops sync provider notices, such as OneDrive offers, at the top of Explorer.",
            Values = new[] { User(Advanced, "ShowSyncProviderNotifications", 0, 1) },
        },
        new()
        {
            Id = "account-notices", Group = AdsGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off account notices in Start and Settings",
            About = "Stops prompts to back up, finish your account and try Microsoft 365.",
            Values = new[]
            {
                User(Advanced, "Start_AccountNotifications", 0, 1),
                User($@"{CurrentVersion}\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0, 1),
            },
        },
        new()
        {
            Id = "welcome-experience", Group = AdsGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off the welcome screen after updates",
            About = "Stops the \"what's new\" page Windows shows after big updates.",
            Values = new[] { User(Suggestions, "SubscribedContent-310093Enabled", 0, 1) },
        },
        new()
        {
            Id = "search-highlights", Group = AdsGroup, Preset = TweakPreset.Standard, Effect = TweakEffect.Explorer,
            Name = "Turn off search highlights",
            About = "Removes daily trivia and pictures from Start search and the taskbar search box.",
            Values = new[] { User(SearchSettings, "IsDynamicSearchBoxEnabled", 0, 1) },
        },

        // File Explorer
        new()
        {
            Id = "file-extensions", Group = ExplorerGroup, Preset = TweakPreset.Standard,
            Name = "Show file extensions",
            About = "Shows .exe, .txt and the rest, which also makes disguised files easier to spot.",
            Values = new[] { User(Advanced, "HideFileExt", 0, 1) },
        },
        new()
        {
            Id = "hidden-files", Group = ExplorerGroup, Preset = TweakPreset.Standard,
            Name = "Show hidden files",
            About = "Shows files and folders marked as hidden.",
            Values = new[] { User(Advanced, "Hidden", 1, 2) },
        },
        new()
        {
            Id = "classic-menu", Group = ExplorerGroup, Preset = TweakPreset.Standard, Effect = TweakEffect.Explorer,
            Name = "Use the classic right-click menu",
            About = "Skips Windows 11's short menu and its \"Show more options\" step.",
            Read = () => HasKey(ClassicMenuRoot + @"\InprocServer32"),
            Apply = SetClassicMenu,
        },
        new()
        {
            Id = "folder-discovery", Group = ExplorerGroup, Preset = TweakPreset.Standard, Effect = TweakEffect.Explorer,
            Name = "Turn off automatic folder type discovery",
            About = "Stops Explorer scanning folders to choose a view, which speeds up big folders.",
            Values = new[] { User(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", "NotSpecified", null) },
        },
        new()
        {
            Id = "open-this-pc", Group = ExplorerGroup,
            Name = "Open File Explorer to This PC",
            About = "New Explorer windows start at your drives instead of Home.",
            Values = new[] { User(Advanced, "LaunchTo", 1, 2) },
        },
        new()
        {
            Id = "compact-view", Group = ExplorerGroup,
            Name = "Use compact view",
            About = "Tightens the spacing in Explorer lists.",
            Values = new[] { User(Advanced, "UseCompactMode", 1, 0) },
        },
        new()
        {
            Id = "hide-home-gallery", Group = ExplorerGroup, Effect = TweakEffect.Explorer,
            Name = "Remove Home and Gallery from the sidebar",
            About = "Cleans up Explorer's left pane. Pair it with Open File Explorer to This PC.",
            Values = new[]
            {
                User(@"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree", 0, 1),
                User(@"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree", 0, 1),
            },
        },
        new()
        {
            Id = "show-system-files", Group = ExplorerGroup,
            Name = "Show protected system files",
            About = "Shows files Windows normally hides to protect them. Deleting the wrong one can stop Windows working.",
            Values = new[] { User(Advanced, "ShowSuperHidden", 1, 0) },
        },
        new()
        {
            Id = "item-checkboxes", Group = ExplorerGroup,
            Name = "Use check boxes to select files",
            About = "Handy on a touch screen, or to pick files without holding Ctrl.",
            Values = new[] { User(Advanced, "AutoCheckSelect", 1, 0) },
        },
        new()
        {
            Id = "nav-expand", Group = ExplorerGroup,
            Name = "Expand the sidebar to the open folder",
            About = "The left pane follows where you are.",
            Values = new[] { User(Advanced, "NavPaneExpandToCurrentFolder", 1, 0) },
        },
        new()
        {
            Id = "nav-all-folders", Group = ExplorerGroup,
            Name = "Show all folders in the sidebar",
            About = "Adds Control Panel, Recycle Bin and your user folders.",
            Values = new[] { User(Advanced, "NavPaneShowAllFolders", 1, 0) },
        },
        new()
        {
            Id = "full-path", Group = ExplorerGroup, Effect = TweakEffect.Explorer,
            Name = "Show the full path in the title bar",
            About = "Shows the whole folder path instead of just its name.",
            Values = new[] { User($@"{ExplorerKey}\CabinetState", "FullPath", 1, 0) },
        },
        new()
        {
            Id = "hide-recent", Group = ExplorerGroup,
            Name = "Hide recent files and frequent folders",
            About = "Home, Start and jump lists stop listing what you've opened.",
            Values = new[]
            {
                User(ExplorerKey, "ShowRecent", 0, 1),
                User(ExplorerKey, "ShowFrequent", 0, 1),
                User(Advanced, "Start_TrackDocs", 0, 1),
            },
        },
        new()
        {
            Id = "this-pc-desktop", Group = ExplorerGroup, Effect = TweakEffect.Explorer,
            Name = "Show This PC on the desktop",
            About = "Brings back the desktop icon.",
            Values = new[] { User($@"{ExplorerKey}\HideDesktopIcons\NewStartPanel", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 0, 1) },
        },
        new()
        {
            Id = "separate-process", Group = ExplorerGroup, Effect = TweakEffect.Explorer,
            Name = "Open folders in a separate process",
            About = "A crash in a folder window won't take the taskbar with it. Uses a little more memory.",
            Values = new[] { User(Advanced, "SeparateProcess", 1, 0) },
        },
        new()
        {
            Id = "restore-folders", Group = ExplorerGroup,
            Name = "Reopen folder windows at sign-in",
            About = "Explorer windows you left open come back.",
            Values = new[] { User(Advanced, "PersistBrowsers", 1, 0) },
        },
        new()
        {
            Id = "confirm-delete", Group = ExplorerGroup,
            Name = "Ask before moving files to the Recycle Bin",
            About = "Adds a confirmation when you press Delete.",
            Values = new[] { User($@"{CurrentVersion}\Policies\Explorer", "ConfirmFileDelete", 1, null) },
        },
        new()
        {
            Id = "icons-only", Group = ExplorerGroup,
            Name = "Show icons instead of thumbnails",
            About = "Speeds up folders full of pictures and videos.",
            Values = new[] { User(Advanced, "IconsOnly", 1, 0) },
        },

        // Right-click menu
        HideMenuItem("menu-copilot", "Remove Ask Copilot", "Takes the Copilot entries out of the menu for files.", TweakPreset.Standard,
            "{CB3B0003-8088-4EDE-8769-8B354AB2FF8C}", "{DB6BACB1-FC31-4B28-9C53-7240A4F98404}"),
        HideMenuItem("menu-clipchamp", "Remove Edit with Clipchamp", "Takes the entry off video files.", TweakPreset.Standard, "{8AB635F8-9A67-4698-AB99-784AD929F3B4}"),
        HideMenuItem("menu-cast", "Remove Cast to Device", "Takes the entry off media files.", TweakPreset.Standard, "{7AD84985-87B4-4a16-BE58-8B72A5B390F7}"),
        HideMenuItem("menu-library", "Remove Include in library", "Takes the entry off folders.", TweakPreset.Standard, "{3dad6c5d-2167-4cae-9914-f99e41c12cfa}"),
        HideMenuItem("menu-give-access", "Remove Give access to", "Takes the entry off files and folders. The Sharing tab in Properties may go too.", TweakPreset.None, "{f81e9010-6ea4-11ce-a7ff-00aa003ca9f6}"),
        HideMenuItem("menu-versions", "Remove Restore previous versions", "Also removes the Previous Versions tab in Properties.", TweakPreset.None, "{596AB062-B4D2-4215-9F74-E9109B0A8153}"),
        HideMenuItem("menu-share", "Remove Share", "Takes the entry off files.", TweakPreset.None, "{e2bf9676-5f8f-435c-97eb-11607a5bedf7}"),
        HideMenuItem("menu-compat", "Remove Troubleshoot compatibility", "Takes the entry off programs. The Compatibility tab in Properties stays.", TweakPreset.None, "{1d27f844-3a1f-4410-85ac-14651078412d}"),
        HideMenuItem("menu-paint", "Remove Edit with Paint", "Takes the entry off pictures.", TweakPreset.None, "{2430F218-B743-4FD6-97BF-5C76541B4AE9}"),
        HideMenuItem("menu-photos", "Remove Edit with Photos", "Takes the entry off pictures.", TweakPreset.None, "{BFE0E2A4-C70C-4AD7-AC3D-10D1ECEBB5B4}"),
        HideMenuItem("menu-notepad", "Remove Edit in Notepad", "Takes the entry off text files.", TweakPreset.None, "{CA6CC9F1-867A-481E-951E-A28C5E4F01EA}"),

        // Taskbar and Start
        new()
        {
            Id = "end-task", Group = TaskbarGroup, Preset = TweakPreset.Standard,
            Name = "Add End task to the taskbar's right-click menu",
            About = "Lets you force-close an app from its taskbar button.",
            Values = new[] { User($@"{Advanced}\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0) },
        },
        new()
        {
            Id = "recommendations", Group = TaskbarGroup, Preset = TweakPreset.Standard,
            Name = "Hide recommendations in Start",
            About = "Removes tips, shortcuts and new app promotions from Start.",
            Values = new[] { User(Advanced, "Start_IrisRecommendations", 0, 1) },
        },
        new()
        {
            Id = "widgets", Group = TaskbarGroup, Preset = TweakPreset.Standard, Effect = TweakEffect.Explorer,
            Name = "Turn off Widgets",
            About = "Removes the Widgets board and its taskbar button.",
            Values = new[] { Machine(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, null) },
        },
        new()
        {
            Id = "taskbar-left", Group = TaskbarGroup,
            Name = "Align the taskbar to the left",
            About = "Puts Start back in the corner.",
            Values = new[] { User(Advanced, "TaskbarAl", 0, 1) },
        },
        new()
        {
            Id = "hide-search", Group = TaskbarGroup,
            Name = "Hide Search on the taskbar",
            About = "Removes the search box. Pressing the Windows key and typing still searches.",
            Values = new[] { User(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, 2) },
        },
        new()
        {
            Id = "hide-task-view", Group = TaskbarGroup,
            Name = "Hide the Task view button",
            About = "Windows key + Tab still opens Task view.",
            Values = new[] { User(Advanced, "ShowTaskViewButton", 0, 1) },
        },
        new()
        {
            Id = "clock-seconds", Group = TaskbarGroup,
            Name = "Show seconds in the taskbar clock",
            About = "Uses slightly more power on laptops.",
            Values = new[] { User(Advanced, "ShowSecondsInSystemClock", 1, 0) },
        },
        new()
        {
            Id = "never-combine", Group = TaskbarGroup, Effect = TweakEffect.Explorer,
            Name = "Never combine taskbar buttons",
            About = "Gives every window its own labeled button.",
            Values = new[]
            {
                User(Advanced, "TaskbarGlomLevel", 2, 0),
                User(Advanced, "MMTaskbarGlomLevel", 2, 0),
            },
        },
        new()
        {
            Id = "last-active-click", Group = TaskbarGroup, Effect = TweakEffect.Explorer,
            Name = "Click a taskbar group to open its last window",
            About = "Saves picking from the thumbnails.",
            Values = new[] { User(Advanced, "LastActiveClick", 1, 0) },
        },
        new()
        {
            Id = "taskbar-all-displays", Group = TaskbarGroup, Effect = TweakEffect.Explorer,
            Name = "Show the taskbar on all displays",
            About = "Applies on PCs with more than one screen.",
            Values = new[] { User(Advanced, "MMTaskbarEnabled", 1, 0) },
        },
        new()
        {
            Id = "desktop-corner", Group = TaskbarGroup,
            Name = "Hide the Show desktop corner",
            About = "Removes the sliver at the far end of the taskbar. Windows key + D still works.",
            Values = new[] { User(Advanced, "TaskbarSd", 0, 1) },
        },
        new()
        {
            Id = "recently-added", Group = TaskbarGroup, Effect = TweakEffect.Explorer,
            Name = "Hide recently added apps in Start",
            About = "New apps stop appearing at the top of Start.",
            Values = new[] { Machine($@"{Policies}\Explorer", "HideRecentlyAddedApps", 1, null) },
        },

        // Notifications
        new()
        {
            Id = "lock-notifications", Group = NotificationsGroup, Preset = TweakPreset.Standard,
            Name = "Hide notifications on the lock screen",
            About = "Message previews can't be read while the PC is locked.",
            Values = new[] { User(NotificationSettings, "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK", 0, 1) },
        },
        new()
        {
            Id = "notification-sounds", Group = NotificationsGroup,
            Name = "Turn off notification sounds",
            About = "Notifications still appear, silently.",
            Values = new[] { User(NotificationSettings, "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", 0, 1) },
        },
        new()
        {
            Id = "no-notifications", Group = NotificationsGroup, Effect = TweakEffect.Explorer,
            Name = "Turn off all notifications",
            About = "Nothing pops up from any app. Security alerts can be missed too.",
            Values = new[] { User($@"{CurrentVersion}\PushNotifications", "ToastEnabled", 0, 1) },
        },
        new()
        {
            Id = "taskbar-badges", Group = NotificationsGroup,
            Name = "Turn off taskbar badges",
            About = "Removes unread counts from taskbar buttons.",
            Values = new[] { User(Advanced, "TaskbarBadges", 0, 1) },
        },
        new()
        {
            Id = "taskbar-flashing", Group = NotificationsGroup,
            Name = "Stop taskbar buttons flashing",
            About = "Apps that want attention no longer flash.",
            Values = new[] { User(Advanced, "TaskbarFlashing", 0, 1) },
        },

        // Performance and gaming
        new()
        {
            Id = "game-dvr", Group = PerformanceGroup, Preset = TweakPreset.Minimal,
            Name = "Turn off Game DVR",
            About = "Stops background game recording. Xbox Game Bar's clip recording won't work.",
            Values = new[]
            {
                User(@"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                User(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
            },
        },
        new()
        {
            Id = "reduce-animations", Group = PerformanceGroup, Preset = TweakPreset.Advanced, Effect = TweakEffect.SignOut,
            Name = "Reduce animations",
            About = "Turns off window and taskbar animations and removes the delay on menus.",
            Values = new[]
            {
                User(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1"),
                User(@"Control Panel\Desktop", "MenuShowDelay", "0", "400"),
                User(Advanced, "TaskbarAnimations", 0, 1),
            },
        },
        new()
        {
            Id = "background-apps", Group = PerformanceGroup, Preset = TweakPreset.Advanced,
            Name = "Stop Store apps running in the background",
            About = "Saves some memory and battery. Store apps may stop sending notifications.",
            Values = new[] { Machine($@"{Policies}\AppPrivacy", "LetAppsRunInBackground", 2, null) },
        },
        new()
        {
            Id = "hibernation", Group = PerformanceGroup, Preset = TweakPreset.Advanced, CustomNeedsAdmin = true,
            Name = "Turn off hibernation",
            About = "Frees the disk space hiberfil.sys uses. Also turns off Fast Startup. Not a good idea on most laptops.",
            Read = () => ReadDword(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is { } v ? v == 0 : null,
            Apply = on => Tool("powercfg.exe", on ? "/hibernate off" : "/hibernate on"),
        },
        new()
        {
            Id = "transparency", Group = PerformanceGroup,
            Name = "Turn off transparency",
            About = "Makes the taskbar and Start solid.",
            Values = new[] { User(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1) },
        },
        new()
        {
            Id = "gpu-scheduling", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Turn on hardware-accelerated GPU scheduling",
            About = "Lets the graphics card manage its own memory. Helps some games and hurts others.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1) },
        },
        new()
        {
            Id = "fast-startup", Group = PerformanceGroup,
            Name = "Turn off Fast Startup",
            About = "Makes Shut down a true shutdown, which can fix driver and dual-boot problems. Startup gets a little slower.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1) },
        },
        new()
        {
            Id = "power-throttling", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Turn off power throttling",
            About = "Stops Windows slowing background apps. Uses more battery.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null) },
        },
        new()
        {
            Id = "ultimate-performance", Group = PerformanceGroup, CustomNeedsAdmin = true,
            Name = "Add the Ultimate Performance power plan",
            About = "Adds Windows' hidden high-performance plan. Choose it on Power & freq. Turning this off removes the plan.",
            Read = () => FindUltimatePlans().Count > 0,
            Apply = SetUltimatePlan,
        },
        new()
        {
            Id = "storage-sense", Group = PerformanceGroup,
            Name = "Turn off Storage Sense",
            About = "Stops Windows cleaning up files on its own schedule.",
            Values = new[] { User(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0, 1) },
        },
        new()
        {
            Id = "network-throttling", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Turn off network throttling for media",
            About = "Windows normally limits network traffic while media plays. Most PCs see little change.",
            Values = new[] { Machine(Multimedia, "NetworkThrottlingIndex", -1, 10) },
        },
        new()
        {
            Id = "background-cpu", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Reserve less CPU for background tasks",
            About = "Lowers the share kept for background work from 20% to 10%, the lowest Windows accepts, while games and media run.",
            Values = new[] { Machine(Multimedia, "SystemResponsiveness", 10, 20) },
        },
        new()
        {
            Id = "game-mode", Group = PerformanceGroup,
            Name = "Turn off Game Mode",
            About = "Windows stops favoring the game in front. Can help if a game stutters or streams badly.",
            Values = new[] { User(GameBar, "AutoGameModeEnabled", 0, 1) },
        },
        new()
        {
            Id = "game-bar-controller", Group = PerformanceGroup,
            Name = "Stop the controller button opening Game Bar",
            About = "The Xbox button on a controller no longer brings up Game Bar.",
            Values = new[] { User(GameBar, "UseNexusForGameBarEnabled", 0, 1) },
        },
        new()
        {
            Id = "fullscreen-optimizations", Group = PerformanceGroup,
            Name = "Turn off fullscreen optimizations",
            About = "Games use true exclusive fullscreen where they can. Helps some games and makes Alt+Tab slower.",
            Values = new[]
            {
                User(@"System\GameConfigStore", "GameDVR_FSEBehavior", 2, null),
                User(@"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, 0),
                User(@"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, 0),
            },
        },
        new()
        {
            Id = "startup-delay", Group = PerformanceGroup, Effect = TweakEffect.SignOut,
            Name = "Remove the startup app delay",
            About = "Startup apps launch right after sign-in instead of waiting. Sign-in can feel busier.",
            Values = new[]
            {
                User($@"{ExplorerKey}\Serialize", "StartupDelayInMSec", 0, null),
                User($@"{ExplorerKey}\Serialize", "WaitforIdleState", 0, null),
            },
        },
        new()
        {
            Id = "faster-shutdown", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Shorten the shutdown wait",
            About = "Gives apps and services 2 seconds to close. Slow ones may be stopped before they finish saving.",
            Values = new[]
            {
                Machine(@"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "2000", "5000"),
                User(Desktop, "WaitToKillAppTimeout", "2000", null),
                User(Desktop, "HungAppTimeout", "2000", null),
            },
        },
        new()
        {
            Id = "auto-end-tasks", Group = PerformanceGroup, Effect = TweakEffect.SignOut,
            Name = "Close apps automatically at shutdown",
            About = "Skips the screen listing apps that are blocking shutdown. You can lose unsaved work.",
            Values = new[] { User(Desktop, "AutoEndTasks", "1", "0") },
        },
        new()
        {
            Id = "last-access", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Turn off last-access timestamps",
            About = "Stops Windows writing the time each file was last read. Tools that sort by last use lose that date.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", unchecked((int)0x80000001), unchecked((int)0x80000002)) },
        },
        new()
        {
            Id = "sysmain", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Turn off SysMain",
            About = "Stops Windows preloading apps into memory. Apps may start more slowly, especially on a hard drive.",
            Values = new[] { Machine($@"{Services}\SysMain", "Start", 4, 2) },
        },
        new()
        {
            Id = "search-indexing", Group = PerformanceGroup, Effect = TweakEffect.Restart,
            Name = "Turn off search indexing",
            About = "Saves some disk activity. Start and Explorer search get slower, and Outlook search may stop working.",
            Values = new[] { Machine($@"{Services}\WSearch", "Start", 4, 2) },
        },

        // Mouse, keyboard and look
        new()
        {
            Id = "mouse-acceleration", Group = InputGroup, Effect = TweakEffect.SignOut,
            Name = "Turn off mouse acceleration",
            About = "The pointer moves the same distance however fast you move the mouse.",
            Values = new[]
            {
                User(@"Control Panel\Mouse", "MouseSpeed", "0", "1"),
                User(@"Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                User(@"Control Panel\Mouse", "MouseThreshold2", "0", "10"),
            },
        },
        new()
        {
            Id = "numlock", Group = InputGroup, Effect = TweakEffect.SignOut,
            Name = "Turn on NumLock at sign-in",
            About = "The number pad works straight away.",
            Values = new[] { User(@"Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0") },
        },
        new()
        {
            Id = "snap-flyout", Group = InputGroup,
            Name = "Turn off the Snap layouts popup",
            About = "Hovering over the maximize button no longer shows layouts.",
            Values = new[] { User(Advanced, "EnableSnapAssistFlyout", 0, 1) },
        },
        new()
        {
            Id = "dark-mode", Group = InputGroup,
            Name = "Use dark mode",
            About = "For both Windows and apps.",
            Values = new[]
            {
                User(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0, 1),
                User(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0, 1),
            },
        },
        ShortcutOff("sticky-keys", "Turn off the Sticky Keys shortcut", "Pressing Shift five times no longer pops up Sticky Keys.", @"Control Panel\Accessibility\StickyKeys", 510),
        ShortcutOff("filter-keys", "Turn off the Filter Keys shortcut", "Holding the right Shift key no longer pops up Filter Keys.", @"Control Panel\Accessibility\Keyboard Response", 126),
        ShortcutOff("toggle-keys", "Turn off the Toggle Keys shortcut", "Holding NumLock no longer pops up Toggle Keys.", @"Control Panel\Accessibility\ToggleKeys", 62),
        ShortcutOff("mouse-keys", "Turn off the Mouse Keys shortcut", "Left Alt + left Shift + NumLock no longer pops up Mouse Keys.", @"Control Panel\Accessibility\MouseKeys", 62),
        new()
        {
            Id = "narrator-shortcut", Group = InputGroup,
            Name = "Turn off the Narrator shortcut",
            About = "Windows key + Ctrl + Enter no longer starts Narrator.",
            Values = new[] { User(@"Software\Microsoft\Narrator\NoRoam", "WinEnterLaunchEnabled", 0, 1) },
        },
        new()
        {
            Id = "print-screen", Group = InputGroup, Effect = TweakEffect.SignOut,
            Name = "Stop Print Screen opening the Snipping Tool",
            About = "The key copies the screen to the clipboard again.",
            Values = new[] { User(@"Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", 0, 1) },
        },
        new()
        {
            Id = "hover-time", Group = InputGroup, Effect = TweakEffect.SignOut,
            Name = "Show tooltips and previews faster",
            About = "Cuts the hover delay from 400 ms to 100 ms.",
            Values = new[] { User(@"Control Panel\Mouse", "MouseHoverTime", "100", "400") },
        },
        new()
        {
            Id = "keyboard-delay", Group = InputGroup, Effect = TweakEffect.SignOut,
            Name = "Shorten the key repeat delay",
            About = "A held key starts repeating sooner.",
            Values = new[] { User(@"Control Panel\Keyboard", "KeyboardDelay", "0", "1") },
        },
        new()
        {
            Id = "clipboard-history", Group = InputGroup,
            Name = "Turn on clipboard history",
            About = "Windows key + V shows what you've copied.",
            Values = new[] { User(@"Software\Microsoft\Clipboard", "EnableClipboardHistory", 1, 0) },
        },
        new()
        {
            Id = "autoplay", Group = InputGroup,
            Name = "Turn off AutoPlay",
            About = "Nothing pops up when you plug in a drive, phone or disc.",
            Values = new[] { User($@"{ExplorerKey}\AutoplayHandlers", "DisableAutoplay", 1, 0) },
        },
        new()
        {
            Id = "accent-title-bars", Group = InputGroup,
            Name = "Show the accent color on title bars",
            About = "Colors the top of windows.",
            Values = new[] { User(@"Software\Microsoft\Windows\DWM", "ColorPrevalence", 1, 0) },
        },
        new()
        {
            Id = "accent-taskbar", Group = InputGroup,
            Name = "Show the accent color on Start and the taskbar",
            About = "Works in dark mode only.",
            Values = new[] { User($@"{CurrentVersion}\Themes\Personalize", "ColorPrevalence", 1, 0) },
        },
        new()
        {
            Id = "wallpaper-quality", Group = InputGroup,
            Name = "Keep wallpapers at full quality",
            About = "Stops Windows compressing JPEG wallpapers. Set your wallpaper again afterward.",
            Values = new[] { User(Desktop, "JPEGImportQuality", 100, null) },
        },
        new()
        {
            Id = "peek", Group = InputGroup,
            Name = "Turn off Peek",
            About = "Hovering over a taskbar preview no longer shows that window full size.",
            Values = new[] { User(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0, 1) },
        },

        // Windows Update
        new()
        {
            Id = "update-no-restart", Group = UpdateGroup, Preset = TweakPreset.Standard,
            Name = "Don't restart for updates while you're signed in",
            About = "Updates wait for you to restart. Security fixes take effect later if you leave it too long.",
            Values = new[] { Machine($@"{Policies}\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1, null) },
        },
        new()
        {
            Id = "update-restart-notice", Group = UpdateGroup, Preset = TweakPreset.Standard,
            Name = "Warn before restarting for updates",
            About = "Shows a notice when a restart is needed to finish updating.",
            Values = new[] { Machine(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "RestartNotificationsAllowed2", 1, 0) },
        },
        new()
        {
            Id = "store-updates", Group = UpdateGroup,
            Name = "Stop Store apps updating themselves",
            About = "You'll need to update them yourself in the Store or on Get apps.",
            Values = new[] { Machine(@"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2, null) },
        },
        new()
        {
            Id = "device-software", Group = UpdateGroup,
            Name = "Stop Windows downloading device software",
            About = "New hardware won't pull in manufacturer apps and custom icons. Windows Update can still offer drivers.",
            Values = new[] { Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0, 1) },
        },

        // Security
        new()
        {
            Id = "block-autorun", Group = SecurityGroup, Preset = TweakPreset.Standard,
            Name = "Block AutoRun on all drives",
            About = "Programs on USB drives and discs can't start by themselves.",
            Values = new[]
            {
                Machine(ExplorerPolicies, "NoDriveTypeAutoRun", 255, 145),
                Machine(ExplorerPolicies, "NoAutorun", 1, null),
            },
        },
        new()
        {
            Id = "block-pua", Group = SecurityGroup, Preset = TweakPreset.Standard,
            Name = "Block potentially unwanted apps",
            About = "Microsoft Defender stops adware and bundled offers. Some free tools get flagged.",
            Values = new[] { Machine(@"SOFTWARE\Policies\Microsoft\Windows Defender", "PUAProtection", 1, null) },
        },
        new()
        {
            Id = "remote-assistance", Group = SecurityGroup, Preset = TweakPreset.Standard,
            Name = "Turn off Remote Assistance",
            About = "Nobody can be invited to connect and help. Remote Desktop isn't affected.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0, 1) },
        },
        new()
        {
            Id = "llmnr", Group = SecurityGroup, Preset = TweakPreset.Advanced,
            Name = "Turn off LLMNR",
            About = "Closes a name lookup that attackers on your network can answer. Some older devices may stop being found by name.",
            Values = new[] { Machine(@"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0, null) },
        },
        new()
        {
            Id = "script-host", Group = SecurityGroup,
            Name = "Turn off Windows Script Host",
            About = "Blocks .vbs and .js scripts, a common way for malware to start. Some installers and sign-in scripts need them.",
            Values = new[] { Machine(@"SOFTWARE\Microsoft\Windows Script Host\Settings", "Enabled", 0, 1) },
        },
        new()
        {
            Id = "admin-shares", Group = SecurityGroup, Effect = TweakEffect.Restart,
            Name = "Turn off the hidden admin shares",
            About = "Removes C$, ADMIN$ and the like. Some remote management and backup tools need them.",
            Values = new[] { Machine($@"{Services}\LanmanServer\Parameters", "AutoShareWks", 0, 1) },
        },
        new()
        {
            Id = "print-spooler", Group = SecurityGroup, Effect = TweakEffect.Restart,
            Name = "Turn off the Print Spooler",
            About = "Shuts a service with a history of security holes. Nothing can print, including to PDF.",
            Values = new[] { Machine($@"{Services}\Spooler", "Start", 4, 2) },
        },
        new()
        {
            Id = "ctrl-alt-del", Group = SecurityGroup,
            Name = "Require Ctrl+Alt+Delete at sign-in",
            About = "Makes sure you are typing into Windows and not a fake sign-in screen.",
            Values = new[] { Machine(SystemPolicies, "DisableCAD", 0, 1) },
        },
        new()
        {
            Id = "hide-last-user", Group = SecurityGroup,
            Name = "Hide the last user's name at sign-in",
            About = "You type your user name each time.",
            Values = new[] { Machine(SystemPolicies, "DontDisplayLastUserName", 1, 0) },
        },
        new()
        {
            Id = "clear-pagefile", Group = SecurityGroup, Effect = TweakEffect.Restart,
            Name = "Clear the page file at shutdown",
            About = "Wipes data left in virtual memory. Shutdown can take much longer.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown", 1, 0) },
        },

        // System
        new()
        {
            Id = "ipv4-first", Group = SystemGroup, Preset = TweakPreset.Advanced, Effect = TweakEffect.Restart,
            Name = "Prefer IPv4 over IPv6",
            About = "Can fix slow connections on networks with poor IPv6. IPv6 stays on.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 32, 0) },
        },
        new()
        {
            Id = "long-paths", Group = SystemGroup, Effect = TweakEffect.Restart,
            Name = "Allow long file paths",
            About = "Lets apps that support it go past the 260-character limit.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0) },
        },
        new()
        {
            Id = "detailed-bsod", Group = SystemGroup, Effect = TweakEffect.Restart,
            Name = "Show details on blue screens",
            About = "Adds the technical error details to blue screens.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\CrashControl", "DisplayParameters", 1, 0) },
        },
        new()
        {
            Id = "verbose-status", Group = SystemGroup, Effect = TweakEffect.Restart,
            Name = "Show detailed startup and shutdown messages",
            About = "Windows says what it's doing instead of \"Please wait\".",
            Values = new[] { Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "VerboseStatus", 1, 0) },
        },
        new()
        {
            Id = "utc-clock", Group = SystemGroup, Effect = TweakEffect.Restart,
            Name = "Keep the hardware clock in UTC",
            About = "Stops the clock jumping when you dual-boot with Linux.",
            Values = new[] { Machine(@"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "RealTimeIsUniversal", 1, null) },
        },
        new()
        {
            Id = "sudo", Group = SystemGroup,
            Name = "Turn on sudo",
            About = "Lets you type sudo in a terminal to run a command as administrator, in its own window. It still asks for approval.",
            Values = new[] { Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Sudo", "Enabled", 1, 0) },
        },
        new()
        {
            Id = "developer-mode", Group = SystemGroup,
            Name = "Turn on Developer Mode",
            About = "Allows installing apps from any source and making symbolic links without admin. Only turn this on if you need it.",
            Values = new[] { Machine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", 1, 0) },
        },
        new()
        {
            Id = "stay-on-crash", Group = SystemGroup, Effect = TweakEffect.Restart,
            Name = "Stay on the blue screen after a crash",
            About = "Gives you time to read the stop code. The PC won't restart by itself.",
            Values = new[] { Machine(CrashControl, "AutoReboot", 0, 1) },
        },
        new()
        {
            Id = "keep-dumps", Group = SystemGroup, Effect = TweakEffect.Restart,
            Name = "Keep crash dumps when disk space is low",
            About = "Windows stops deleting the memory dump it saved after a crash.",
            Values = new[] { Machine(CrashControl, "AlwaysKeepMemoryDump", 1, 0) },
        },
        new()
        {
            Id = "first-sign-in", Group = SystemGroup,
            Name = "Turn off the first sign-in animation",
            About = "New accounts skip the \"Hi\" screens.",
            Values = new[] { Machine(SystemPolicies, "EnableFirstLogonAnimation", 0, 1) },
        },
        new()
        {
            Id = "onedrive-sync", Group = SystemGroup,
            Name = "Stop OneDrive syncing",
            About = "OneDrive stops running and syncing. Your files stay in the cloud, and files stored only online can't be opened from Explorer.",
            Values = new[] { Machine($@"{Policies}\OneDrive", "DisableFileSyncNGSC", 1, null) },
        },
    };

    public static IEnumerable<Tweak> InPreset(TweakPreset preset) =>
        All.Where(t => t.Preset != TweakPreset.None && t.Preset <= preset);

    // ---- Tweaks that aren't plain registry values ----

    static bool HasKey(string key)
    {
        var mapped = KeyMap($@"HKCU\{key}");
        using var k = Hive(mapped).OpenSubKey(mapped[5..]);
        return k != null;
    }

    static string? SetClassicMenu(bool on)
    {
        var inproc = KeyMap($@"HKCU\{ClassicMenuRoot}\InprocServer32");
        var root = KeyMap($@"HKCU\{ClassicMenuRoot}");
        if (on)
        {
            // Windows looks for this key with an empty default value. It has to be set, not merely absent.
            using var key = Hive(inproc).CreateSubKey(inproc[5..], writable: true);
            key.SetValue("", "", RegistryValueKind.String);
        }
        else
        {
            Hive(root).DeleteSubKeyTree(root[5..], throwOnMissingSubKey: false);
        }
        return null;
    }

    static long? ReadDword(string key, string name) => Get(key, name) is { } v ? Convert.ToInt64(v) : null;

    static string? Tool(string exe, string args)
    {
        var result = Cmd.Run(Cmd.System(exe), args);
        return result.Ok ? null : result.Output.Trim() is { Length: > 0 } text ? text : $"{exe} failed with code {result.ExitCode}.";
    }

    static bool? TelemetryTasksOff()
    {
        var states = TelemetryTasks.Select(ScheduledTaskList.IsEnabled).Where(s => s != null).ToList();
        return states.Count == 0 ? null : states.All(s => s == false);
    }

    /// <summary>Remembers which tasks were running, so turning this off leaves alone any that were already off.</summary>
    static string? SetTelemetryTasks(bool on)
    {
        const string Id = "telemetry-tasks";
        if (!on)
        {
            var stopped = Undo.TryGetValue(Id, out var saved) ? saved.Select(s => s.Key) : TelemetryTasks;
            foreach (var task in stopped.Where(t => ScheduledTaskList.IsEnabled(t) != null)) ScheduledTaskList.SetEnabled(task, true);
            return null;
        }

        var done = new List<string>();
        try
        {
            foreach (var task in TelemetryTasks.Where(t => ScheduledTaskList.IsEnabled(t) == true))
            {
                ScheduledTaskList.SetEnabled(task, false);
                done.Add(task);
            }
        }
        catch
        {
            foreach (var task in done)
            {
                try { ScheduledTaskList.SetEnabled(task, true); } catch { }
            }
            throw;
        }
        // The first list wins, so a second apply doesn't forget what Tman stopped the first time.
        Undo.TryAdd(Id, done.Select(t => new Saved(t, "task", null, null)).ToList());
        return null;
    }

    /// <summary>Tman's copy, the plan itself if a PC lists it, and any copy made elsewhere, which keeps the English name.</summary>
    static List<(string Id, bool Active)> FindUltimatePlans()
    {
        var plans = new List<(string, bool)>();
        var list = Cmd.Run(Cmd.System("powercfg.exe"), "/list").Output;
        foreach (Match m in Regex.Matches(list, @"([0-9a-fA-F-]{36})\s+\((.+?)\)(\s*\*)?"))
        {
            var id = m.Groups[1].Value;
            if (id.Equals(UltimatePlanCopy, StringComparison.OrdinalIgnoreCase) || id.Equals(UltimatePlanTemplate, StringComparison.OrdinalIgnoreCase)
                || m.Groups[2].Value == "Ultimate Performance")
                plans.Add((id, m.Groups[3].Success));
        }
        return plans;
    }

    static string? SetUltimatePlan(bool on)
    {
        if (on) return Tool("powercfg.exe", $"/duplicatescheme {UltimatePlanTemplate} {UltimatePlanCopy}");
        var plans = FindUltimatePlans();
        if (plans.Any(p => p.Id.Equals(UltimatePlanTemplate, StringComparison.OrdinalIgnoreCase)))
            return "This plan is built into this PC's power settings, so it can't be removed.";
        // Windows won't delete the plan in use.
        if (plans.Any(p => p.Active) && Tool("powercfg.exe", $"/setactive {PowerPlans.Balanced}") is { } error) return error;
        foreach (var plan in plans)
            if (Tool("powercfg.exe", $"/delete {plan.Id}") is { } failed) return failed;
        return null;
    }
}
