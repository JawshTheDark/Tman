using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Tman.Core;

public enum UpdateMode { Default, Security, Paused, Custom }

/// <summary>What the live values on this PC say. <see cref="Notes"/> are plain sentences about anything set by someone other than Tman.</summary>
public sealed record UpdateModeState(UpdateMode Mode, string Title, string Summary, bool SetByOthers, DateTime? PausedUntil, IReadOnlyList<string> Notes);

/// <summary>
/// The update mode: Windows' documented update policies for Security-focused, and Windows' own pause setting for Pause. There is deliberately
/// no way to turn updates off here. Tman records when it set the policies, so values it finds that it didn't set can be called someone else's.
/// </summary>
public static class WindowsUpdateMode
{
    public const int MaxPauseWeeks = 5;

    const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    const string PausePath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    const string PauseStatusPath = @"SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\Settings";
    const string MdmPath = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";
    const string TmanPath = @"SOFTWARE\Tman";
    const string MarkerPath = TmanPath + @"\WindowsUpdate";
    const string MarkerValue = "Mode";
    const string SecurityMarker = "Security-focused";

    const int FeatureDays = 365;
    const int QualityDays = 4;

    static readonly (string Name, int Value)[] SecurityPolicy =
    {
        ("DeferFeatureUpdates", 1),
        ("DeferFeatureUpdatesPeriodInDays", FeatureDays),
        ("DeferQualityUpdates", 1),
        ("DeferQualityUpdatesPeriodInDays", QualityDays),
        ("ExcludeWUDriversInQualityUpdate", 1),
    };

    static readonly string[] PauseValues =
    {
        "PauseUpdatesStartTime", "PauseUpdatesExpiryTime",
        "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    };

    static readonly string[] PauseStatusValues = { "PausedFeatureStatus", "PausedQualityStatus" };

    static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static UpdateModeState Read()
    {
        using var hklm = Hklm();
        using var policy = hklm.OpenSubKey(PolicyPath);
        var ours = SecurityPolicy.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var policyNames = policy?.GetValueNames() ?? Array.Empty<string>();
        bool anyOurs = policyNames.Any(ours.Contains);
        bool isSecurity = SecurityPolicy.All(p => policy?.GetValue(p.Name) is int v && v == p.Value);
        using var marker = hklm.OpenSubKey(MarkerPath);
        bool setByTman = marker?.GetValue(MarkerValue) as string == SecurityMarker;
        var pausedUntil = ReadPausedUntil(hklm);
        bool pausedElsewhere = pausedUntil == null && PausedWithoutWindowsSetting(hklm);

        var notes = new List<string>();
        var mdm = ReadMdm(hklm);
        bool mdmControls = mdm.Any(n => n.StartsWith("Defer", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Exclude", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Pause", StringComparison.OrdinalIgnoreCase));
        if (mdm.Count > 0) notes.Add($"Device management (MDM) sets Windows Update policies here, and Tman can't change those: {string.Join(", ", mdm)}.");
        if (LocalGroupPolicySetsUpdates()) notes.Add("Local Group Policy also sets Windows Update policies. It puts its values back at its next refresh, so remove them in gpedit.msc.");
        var others = policyNames.Where(n => !ours.Contains(n)).Concat(AuPolicyNames(hklm)).ToList();
        if (others.Count > 0) notes.Add($"Other Windows Update policies are set, which Tman didn't set and won't touch: {string.Join(", ", others)}.");
        if ((anyOurs && !setByTman || others.Count > 0) && IsDomainJoined()) notes.Add("This PC is joined to a domain, so its Group Policy can put these back.");
        if (IsHomeEdition()) notes.Add("Windows Home doesn't support the deferral policies, so Security-focused has no effect on this edition.");

        bool foreignPolicy = anyOurs && (!isSecurity || !setByTman) || mdmControls;
        const string foreign = "Set by someone else's policy, not by Tman.";
        if (pausedUntil is { } until)
        {
            if (anyOurs) notes.Add(isSecurity && setByTman ? "The Security-focused policies are still set too." : "Deferral policies are set too. " + foreign);
            return new UpdateModeState(UpdateMode.Paused, $"Paused until {until:d}", $"Windows won't install updates until {until:f}, security updates included.", foreignPolicy, until, notes);
        }
        if (pausedElsewhere)
            return new UpdateModeState(UpdateMode.Paused, "Paused", $"Windows reports updates as paused, but not through its own pause setting. {foreign}", true, null, notes);
        if (isSecurity)
            return new UpdateModeState(UpdateMode.Security, "Security-focused",
                $"Feature updates wait {FeatureDays} days, quality updates wait {QualityDays} days, and drivers are left out of updates. " + (setByTman ? "Set by Tman." : foreign),
                !setByTman, null, notes);
        if (anyOurs || mdmControls)
            return new UpdateModeState(UpdateMode.Custom, "Custom", $"{Describe(policy)} {foreign}", true, null, notes);
        return new UpdateModeState(UpdateMode.Default, "Default", "No update deferrals or pause are set, so Windows offers updates as soon as they're ready.", false, null, notes);
    }

    static string Describe(RegistryKey? policy)
    {
        var parts = new List<string>();
        if (policy?.GetValue("DeferFeatureUpdates") is 1) parts.Add($"Feature updates deferred {policy.GetValue("DeferFeatureUpdatesPeriodInDays") ?? 0} days.");
        if (policy?.GetValue("DeferQualityUpdates") is 1) parts.Add($"Quality updates deferred {policy.GetValue("DeferQualityUpdatesPeriodInDays") ?? 0} days.");
        if (policy?.GetValue("ExcludeWUDriversInQualityUpdate") is 1) parts.Add("Drivers left out of updates.");
        return parts.Count > 0 ? string.Join(" ", parts) : "Windows Update policies are set that don't match any Tman mode.";
    }

    static DateTime? ReadPausedUntil(RegistryKey hklm)
    {
        using var ux = hklm.OpenSubKey(PausePath);
        var expiry = ParseTime(ux?.GetValue("PauseUpdatesExpiryTime"));
        return expiry > DateTime.Now ? expiry : null;
    }

    /// <summary>Windows says it's paused and no pause date is set, which is how a pause by policy looks.</summary>
    static bool PausedWithoutWindowsSetting(RegistryKey hklm)
    {
        using var ux = hklm.OpenSubKey(PausePath);
        if (ux?.GetValue("PauseUpdatesExpiryTime") != null) return false;
        using var status = hklm.OpenSubKey(PauseStatusPath);
        return PauseStatusValues.Any(n => status?.GetValue(n) is 1);
    }

    static DateTime? ParseTime(object? value) =>
        value is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime() : null;

    static List<string> ReadMdm(RegistryKey hklm)
    {
        using var key = hklm.OpenSubKey(MdmPath);
        return key?.GetValueNames().Where(n => !n.EndsWith("_ProviderSet", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("_WinningProvider", StringComparison.OrdinalIgnoreCase)).ToList()
               ?? new List<string>();
    }

    static IEnumerable<string> AuPolicyNames(RegistryKey hklm)
    {
        using var key = hklm.OpenSubKey(PolicyPath + @"\AU");
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    static bool LocalGroupPolicySetsUpdates()
    {
        try
        {
            var file = Path.Combine(Environment.SystemDirectory, "GroupPolicy", "Machine", "Registry.pol");
            return File.Exists(file) && Encoding.Unicode.GetString(File.ReadAllBytes(file)).Contains(@"Microsoft\Windows\WindowsUpdate", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static bool IsDomainJoined() => Wmi.Query("SELECT PartOfDomain FROM Win32_ComputerSystem").FirstOrDefault()?.Bool("PartOfDomain") == true;

    static bool IsHomeEdition()
    {
        using var hklm = Hklm();
        using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return (key?.GetValue("EditionID") as string)?.StartsWith("Core", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Removes everything the other two modes set. Throws if Windows refuses, which it does without admin.</summary>
    public static void SetDefault()
    {
        using var hklm = Hklm();
        ClearPolicy(hklm);
        ClearPause(hklm);
    }

    public static void SetSecurityFocused()
    {
        using var hklm = Hklm();
        ClearPause(hklm);
        using var policy = hklm.CreateSubKey(PolicyPath, writable: true);
        foreach (var (name, value) in SecurityPolicy) policy.SetValue(name, value, RegistryValueKind.DWord);
        using var marker = hklm.CreateSubKey(MarkerPath, writable: true);
        marker.SetValue(MarkerValue, SecurityMarker, RegistryValueKind.String);
    }

    /// <summary>The date a pause of this many weeks would end. A pause already running can't be stretched past five weeks from when it began.</summary>
    public static DateTime PauseEnd(int weeks)
    {
        using var hklm = Hklm();
        var now = DateTime.UtcNow;
        var end = now.AddDays(7 * Math.Clamp(weeks, 1, MaxPauseWeeks));
        if (RunningPauseStart(hklm) is { } start && start.AddDays(7 * MaxPauseWeeks) < end) end = start.AddDays(7 * MaxPauseWeeks);
        return end.ToLocalTime();
    }

    /// <summary>Uses Windows' own pause, which ends by itself. Throws if Windows refuses, which it does without admin.</summary>
    public static void Pause(int weeks)
    {
        using var hklm = Hklm();
        var now = DateTime.UtcNow;
        var start = RunningPauseStart(hklm) ?? now;
        var end = PauseEnd(weeks).ToUniversalTime();
        ClearPolicy(hklm);

        string startText = start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), endText = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        using (var ux = hklm.CreateSubKey(PausePath, writable: true))
            foreach (var name in PauseValues)
                ux.SetValue(name, name.EndsWith("StartTime", StringComparison.Ordinal) ? startText : endText, RegistryValueKind.String);
        using var status = hklm.CreateSubKey(PauseStatusPath, writable: true);
        foreach (var name in PauseStatusValues) status.SetValue(name, 1, RegistryValueKind.DWord);
    }

    static DateTime? RunningPauseStart(RegistryKey hklm)
    {
        using var ux = hklm.OpenSubKey(PausePath);
        if (ReadPausedUntil(hklm) == null) return null;
        return ParseTime(ux?.GetValue("PauseUpdatesStartTime"))?.ToUniversalTime();
    }

    static void ClearPolicy(RegistryKey hklm)
    {
        using (var policy = hklm.OpenSubKey(PolicyPath, writable: true))
        {
            if (policy != null)
                foreach (var (name, _) in SecurityPolicy) policy.DeleteValue(name, throwOnMissingValue: false);
        }
        using (var left = hklm.OpenSubKey(PolicyPath))
        {
            if (left is { ValueCount: 0, SubKeyCount: 0 }) hklm.DeleteSubKey(PolicyPath, throwOnMissingSubKey: false);
        }
        hklm.DeleteSubKeyTree(MarkerPath, throwOnMissingSubKey: false);
        using (var tman = hklm.OpenSubKey(TmanPath))
        {
            if (tman is { ValueCount: 0, SubKeyCount: 0 }) hklm.DeleteSubKey(TmanPath, throwOnMissingSubKey: false);
        }
    }

    static void ClearPause(RegistryKey hklm)
    {
        using (var ux = hklm.OpenSubKey(PausePath, writable: true))
        {
            if (ux != null)
                foreach (var name in PauseValues) ux.DeleteValue(name, throwOnMissingValue: false);
        }
        using var status = hklm.OpenSubKey(PauseStatusPath, writable: true);
        if (status == null) return;
        foreach (var name in PauseStatusValues)
            if (status.GetValue(name) is not (null or 0)) status.SetValue(name, 0, RegistryValueKind.DWord);
    }
}
