using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tman.Core;

public enum SecurityState { Good, Attention, Off, Unknown }

public sealed record SecurityItem(string Section, string Name, SecurityState State, string Value, string Meaning, string? Open = null);

/// <summary>
/// The Security page's readings. Each one reports what Windows actually said: anything that couldn't be read is
/// Unknown, never Good. All of it blocks, so call it off the UI thread.
/// </summary>
public static class SecurityChecks
{
    const string Threat = "THREAT PROTECTION", Firewall = "FIREWALL", Device = "DEVICE SECURITY", Encryption = "DRIVE ENCRYPTION", Apps = "APPS AND SIGN-IN", Remote = "REMOTE ACCESS";
    const string OpenThreat = "windowsdefender://threat", OpenNetwork = "windowsdefender://network", OpenDevice = "windowsdefender://device", OpenApps = "windowsdefender://appbrowser";
    const string OpenBitLocker = "control.exe /name Microsoft.BitLockerDriveEncryption";

    public static readonly Func<IEnumerable<SecurityItem>>[] All =
    {
        Antivirus, Defender, FirewallProfiles, SecureBoot, Tpm, DeviceGuard, BitLocker, Uac, SmartAppControl, SmartScreen, RemoteDesktop,
    };

    static IEnumerable<SecurityItem> Antivirus()
    {
        var products = Wmi.Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2");
        if (products.Count == 0)
        {
            yield return new(Threat, "Antivirus", SecurityState.Unknown, "Not reported", "Windows Security Center didn't list any antivirus, or couldn't be read.", OpenThreat);
            yield break;
        }
        foreach (var p in products)
        {
            // productState packs flags: 0x1000 means scanning is on, 0x10 means its definitions are out of date.
            ulong state = p.U64("productState");
            bool on = (state & 0x1000) != 0, stale = (state & 0x10) != 0;
            var name = p.Str("displayName");
            if (!on) yield return new(Threat, "Antivirus", SecurityState.Attention, $"{name}: off", "Windows reports this antivirus isn't scanning.", OpenThreat);
            else if (stale) yield return new(Threat, "Antivirus", SecurityState.Attention, $"{name}: out of date", "It's running, but its definitions are out of date, so new threats can get past it.", OpenThreat);
            else yield return new(Threat, "Antivirus", SecurityState.Good, name, "Windows reports it's on and up to date.", OpenThreat);
        }
    }

    static IEnumerable<SecurityItem> Defender()
    {
        var mp = Wmi.Query("SELECT * FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender").FirstOrDefault();
        if (mp == null)
        {
            yield return new(Threat, "Microsoft Defender", SecurityState.Unknown, "Not reported", "Tman couldn't read Defender's status. That's normal when another antivirus has replaced it.", OpenThreat);
            yield break;
        }
        var mode = mp.Str("AMRunningMode");
        if (mode.Contains("Passive", StringComparison.OrdinalIgnoreCase))
            yield return new(Threat, "Microsoft Defender", SecurityState.Off, "Passive", "Defender is standing by because another antivirus is the active one.", OpenThreat);

        yield return mp.Bool("RealTimeProtectionEnabled") switch
        {
            true => new(Threat, "Real-time protection", SecurityState.Good, "On", "Defender checks files as they're opened or downloaded.", OpenThreat),
            false => new(Threat, "Real-time protection", SecurityState.Attention, "Off", "Files aren't being checked as they're opened, so malware can run before a scan finds it.", OpenThreat),
            null => new(Threat, "Real-time protection", SecurityState.Unknown, "Not reported", "Defender didn't report this setting.", OpenThreat),
        };

        var updated = mp.Date("AntivirusSignatureLastUpdated");
        ulong age = mp.U64("AntivirusSignatureAge");
        if (updated == null) yield return new(Threat, "Security intelligence", SecurityState.Unknown, "Not reported", "Defender didn't report when its definitions were last updated.", OpenThreat);
        else if (age <= 3) yield return new(Threat, "Security intelligence", SecurityState.Good, age == 0 ? "Updated today" : $"{age} days old", $"Defender's definitions were last updated {updated:g}.", OpenThreat);
        else yield return new(Threat, "Security intelligence", SecurityState.Attention, $"{age} days old", $"Defender's definitions were last updated {updated:g}, so recent threats may be missed.", OpenThreat);

        var quick = mp.Date("QuickScanEndTime");
        var full = mp.Date("FullScanEndTime");
        var last = new[] { quick, full }.Where(d => d != null).Max();
        if (last == null)
        {
            yield return new(Threat, "Last scan", SecurityState.Unknown, "Not reported", "Defender has no record of a finished scan.", OpenThreat);
        }
        else
        {
            var kind = last == full ? "Full scan" : "Quick scan";
            int days = (int)(DateTime.Now - last.Value).TotalDays;
            yield return days <= 14
                ? new(Threat, "Last scan", SecurityState.Good, Fmt.Ago(last.Value), $"{kind} finished {last:g}. Windows runs these by itself when the PC is idle.", OpenThreat)
                : new(Threat, "Last scan", SecurityState.Attention, $"{days} days ago", $"{kind} finished {last:g}. Scans normally run by themselves, so check they aren't being skipped.", OpenThreat);
        }
    }

    static IEnumerable<SecurityItem> FirewallProfiles()
    {
        List<FirewallProfile> profiles;
        try
        {
            profiles = FirewallStore.ReadProfiles();
        }
        catch (Exception ex)
        {
            return new[] { new SecurityItem(Firewall, "Windows Firewall", SecurityState.Unknown, "Can't read", $"The firewall didn't answer, so its service may be stopped. {ex.Message}", OpenNetwork) };
        }
        return profiles.Select(p => new SecurityItem(Firewall, $"{p.Name} network" + (p.Active ? " (active)" : ""),
            !p.Enabled ? SecurityState.Attention : p.BlockInbound ? SecurityState.Good : SecurityState.Attention,
            !p.Enabled ? "Off" : p.BlockInbound ? "On" : "On, inbound allowed",
            !p.Enabled ? "Other devices on networks of this type can reach any service on this PC."
                : p.BlockInbound ? "Incoming connections are blocked unless a rule allows them."
                : "The firewall is on, but it lets in incoming connections that no rule blocks.", OpenNetwork));
    }

    static IEnumerable<SecurityItem> SecureBoot()
    {
        yield return Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") switch
        {
            1 => new(Device, "Secure Boot", SecurityState.Good, "On", "Only trusted software can load while the PC starts.", OpenDevice),
            0 => new(Device, "Secure Boot", SecurityState.Off, "Off", "It can be turned on in the PC's firmware (BIOS) settings, if the PC starts in UEFI mode.", OpenDevice),
            _ => new(Device, "Secure Boot", SecurityState.Unknown, "Not reported", "Windows has no Secure Boot state here, which usually means the PC starts in legacy BIOS mode.", OpenDevice),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TpmDeviceInfo
    {
        public uint StructVersion, TpmVersion, InterfaceType, ImpRevision;
    }

    [DllImport("tbs.dll")]
    static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    static IEnumerable<SecurityItem> Tpm()
    {
        const uint NotFound = 0x8028400F;
        uint result;
        TpmDeviceInfo info = default;
        try
        {
            result = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out info);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            result = 1;
        }
        yield return (result, info.TpmVersion) switch
        {
            (0, 2) => new(Device, "TPM", SecurityState.Good, "Version 2.0", "This security chip protects keys for BitLocker and Windows Hello.", OpenDevice),
            (0, 1) => new(Device, "TPM", SecurityState.Attention, "Version 1.2", "It works, but Windows 11 expects version 2.0.", OpenDevice),
            (NotFound, _) => new(Device, "TPM", SecurityState.Off, "Not found", "Windows sees no TPM. It may be turned off in the PC's firmware (BIOS) settings.", OpenDevice),
            _ => new(Device, "TPM", SecurityState.Unknown, "Can't read", $"Windows couldn't report the TPM (code {result:X8}).", OpenDevice),
        };
    }

    static bool Has(ManagementBaseObject o, string property, uint value)
    {
        try { return o[property] is Array list && list.Cast<object>().Any(v => Convert.ToUInt32(v) == value); }
        catch { return false; }
    }

    static IEnumerable<SecurityItem> DeviceGuard()
    {
        const uint Hvci = 2;
        var guard = Wmi.Query("SELECT * FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard").FirstOrDefault();
        if (guard == null)
        {
            yield return new(Device, "Memory integrity", SecurityState.Unknown, "Not reported", "Windows didn't report this.", OpenDevice);
            yield return new(Device, "Virtualization-based security", SecurityState.Unknown, "Not reported", "Windows didn't report this.", OpenDevice);
            yield break;
        }
        if (Has(guard, "SecurityServicesRunning", Hvci))
            yield return new(Device, "Memory integrity", SecurityState.Good, "On", "Drivers and other kernel code must pass a check before they can run.", OpenDevice);
        else if (Has(guard, "SecurityServicesConfigured", Hvci))
            yield return new(Device, "Memory integrity", SecurityState.Attention, "Set but not running", "It's turned on but isn't active. A restart or an incompatible driver may be the cause.", OpenDevice);
        else
            yield return new(Device, "Memory integrity", SecurityState.Off, "Off", "Without it, a bad driver has an easier way into Windows.", OpenDevice);

        yield return guard.U64("VirtualizationBasedSecurityStatus") switch
        {
            2 => new(Device, "Virtualization-based security", SecurityState.Good, "Running", "Windows keeps sensitive data in a protected area that the rest of the system can't reach.", OpenDevice),
            1 => new(Device, "Virtualization-based security", SecurityState.Attention, "Set but not running", "It's turned on but isn't active. It may need virtualization turned on in the firmware (BIOS).", OpenDevice),
            _ => new(Device, "Virtualization-based security", SecurityState.Off, "Off", "Memory integrity and Credential Guard depend on it.", OpenDevice),
        };
    }

    static IEnumerable<SecurityItem> BitLocker()
    {
        var volumes = Wmi.Query("SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume", @"root\CIMV2\Security\MicrosoftVolumeEncryption")
            .Where(v => v.Str("DriveLetter").Length > 0)
            .OrderBy(v => v.Str("DriveLetter"))
            .ToList();
        if (volumes.Count > 0) return volumes.Select(FromWmi).ToList();

        var fromShell = FromShell();
        if (fromShell.Count > 0) return fromShell;
        return new[] { new SecurityItem(Encryption, "BitLocker", SecurityState.Unknown, "Can't read", "Reading this needs administrator rights.", OpenBitLocker) };
    }

    static SecurityItem FromWmi(ManagementBaseObject v)
    {
        var name = $"BitLocker on {v.Str("DriveLetter")}";
        ulong conversion = v.U64("ConversionStatus");
        return (conversion, v.U64("ProtectionStatus")) switch
        {
            (2, _) => new(Encryption, name, SecurityState.Attention, "Encrypting", "Windows is encrypting this drive now.", OpenBitLocker),
            (3, _) => new(Encryption, name, SecurityState.Attention, "Decrypting", "Windows is removing encryption from this drive now.", OpenBitLocker),
            (4 or 5, _) => new(Encryption, name, SecurityState.Attention, "Paused", "Windows has paused changing this drive's encryption.", OpenBitLocker),
            (1, 1) => new(Encryption, name, SecurityState.Good, "On", "Data on this drive can't be read without unlocking it.", OpenBitLocker),
            (1, 0) => new(Encryption, name, SecurityState.Attention, "Suspended", "The drive is encrypted, but its protection is paused.", OpenBitLocker),
            (0, 0) => new(Encryption, name, SecurityState.Off, "Off", "Anyone who takes this drive can read it.", OpenBitLocker),
            _ => new(Encryption, name, SecurityState.Unknown, "Not reported", "Windows gave a state Tman doesn't recognise.", OpenBitLocker),
        };
    }

    /// <summary>File Explorer's own per-drive value, which a standard user can read. Drives BitLocker can't cover report 0.</summary>
    static List<SecurityItem> FromShell()
    {
        var items = new List<SecurityItem>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return items;
            dynamic shell = Activator.CreateInstance(type)!;
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat is "NTFS" or "ReFS" or "FAT32" or "exFAT");
            foreach (var d in drives)
            {
                object? value = shell.NameSpace(d.Name)?.Self.ExtendedProperty("System.Volume.BitLockerProtection");
                var name = $"BitLocker on {d.Name.TrimEnd('\\')}";
                items.Add((value is int n ? n : -1) switch
                {
                    1 or 6 => new(Encryption, name, SecurityState.Good, "On", "Data on this drive can't be read without unlocking it.", OpenBitLocker),
                    2 => new(Encryption, name, SecurityState.Off, "Off", "Anyone who takes this drive can read it.", OpenBitLocker),
                    3 => new(Encryption, name, SecurityState.Attention, "Encrypting", "Windows is encrypting this drive now.", OpenBitLocker),
                    4 => new(Encryption, name, SecurityState.Attention, "Decrypting", "Windows is removing encryption from this drive now.", OpenBitLocker),
                    5 => new(Encryption, name, SecurityState.Attention, "Suspended", "The drive is encrypted, but its protection is paused.", OpenBitLocker),
                    0 => new(Encryption, name, SecurityState.Unknown, "Not available", "BitLocker isn't available for this drive.", OpenBitLocker),
                    _ => new(Encryption, name, SecurityState.Unknown, "Not reported", "File Explorer gave a state Tman doesn't recognise.", OpenBitLocker),
                });
            }
        }
        catch
        {
            items.Clear();
        }
        return items;
    }

    static IEnumerable<SecurityItem> Uac()
    {
        const string OpenUac = "UserAccountControlSettings.exe";
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        int? lua = Dword(Registry.LocalMachine, key, "EnableLUA"), consent = Dword(Registry.LocalMachine, key, "ConsentPromptBehaviorAdmin"), secure = Dword(Registry.LocalMachine, key, "PromptOnSecureDesktop");
        const string name = "User Account Control";
        yield return (lua, consent, secure) switch
        {
            (null, _, _) or (_, null, _) => new(Apps, name, SecurityState.Unknown, "Not reported", "Tman couldn't read these settings.", OpenUac),
            (0, _, _) => new(Apps, name, SecurityState.Attention, "Off", "Every program runs with full rights and nothing asks before it changes Windows.", OpenUac),
            (_, 0, _) => new(Apps, name, SecurityState.Attention, "Never notify", "Programs can get administrator rights without asking you.", OpenUac),
            (_, 2, _) => new(Apps, name, SecurityState.Good, "Always notify", "Windows asks before any change that needs administrator rights.", OpenUac),
            (_, 5, 0) => new(Apps, name, SecurityState.Attention, "Notify, without dimming", "Windows asks, but on the normal desktop where other programs could interfere.", OpenUac),
            (_, 5, _) => new(Apps, name, SecurityState.Good, "Default", "Windows asks before apps make changes that need administrator rights.", OpenUac),
            _ => new(Apps, name, SecurityState.Good, "Custom", "Windows asks for consent or a password before giving administrator rights.", OpenUac),
        };
    }

    static IEnumerable<SecurityItem> SmartAppControl()
    {
        yield return Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState") switch
        {
            1 => new(Apps, "Smart App Control", SecurityState.Good, "On", "Windows blocks apps that it can't show to be safe.", OpenApps),
            2 => new(Apps, "Smart App Control", SecurityState.Attention, "Evaluation", "Windows is watching how this PC is used to decide whether to turn it on. It blocks nothing yet.", OpenApps),
            0 => new(Apps, "Smart App Control", SecurityState.Off, "Off", "This extra app check isn't in use. Your antivirus still works.", OpenApps),
            _ => new(Apps, "Smart App Control", SecurityState.Unknown, "Not reported", "This version of Windows doesn't report it.", OpenApps),
        };
    }

    static IEnumerable<SecurityItem> SmartScreen()
    {
        const string name = "SmartScreen for apps and files";
        var policy = Dword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");
        var setting = Text(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");
        if (policy == 0 || (policy == null && string.Equals(setting, "Off", StringComparison.OrdinalIgnoreCase)))
            yield return new(Apps, name, SecurityState.Attention, "Off", "Windows won't warn you before running unrecognized downloads.", OpenApps);
        else if (policy != null || !string.IsNullOrEmpty(setting))
            yield return new(Apps, name, SecurityState.Good, "On", "Windows warns you before running unrecognized downloads.", OpenApps);
        else
            yield return new(Apps, name, SecurityState.Unknown, "No setting stored", "Windows normally uses its default in this case, which is on. Check it in Windows Security.", OpenApps);

        var edgePolicy = Dword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "SmartScreenEnabled");
        var edge = edgePolicy ?? Dword(Registry.CurrentUser, @"Software\Microsoft\Edge\SmartScreenEnabled", "");
        if (edge == 1) yield return new(Apps, "SmartScreen in Microsoft Edge", SecurityState.Good, "On", "Edge warns you about dangerous sites and downloads.", OpenApps);
        else if (edge == 0) yield return new(Apps, "SmartScreen in Microsoft Edge", SecurityState.Attention, "Off", "Edge won't warn you about dangerous sites and downloads.", OpenApps);
    }

    static IEnumerable<SecurityItem> RemoteDesktop()
    {
        const string OpenRemote = "ms-settings:remotedesktop", name = "Remote Desktop";
        var deny = Dword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", "fDenyTSConnections")
                   ?? Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
        var nla = Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication");
        yield return (deny, nla) switch
        {
            (null, _) => new(Remote, name, SecurityState.Unknown, "Not reported", "Tman couldn't read this setting.", OpenRemote),
            (1, _) => new(Remote, name, SecurityState.Good, "Off", "Nobody can connect to this PC with Remote Desktop.", OpenRemote),
            (_, 0) => new(Remote, name, SecurityState.Attention, "On, without sign-in first", "Anyone who can reach this PC gets a sign-in screen. Turn on Network Level Authentication.", OpenRemote),
            _ => new(Remote, name, SecurityState.Attention, "On", "Accounts on this PC can sign in from other devices, so use strong passwords.", OpenRemote),
        };
    }

    static int? Dword(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    static string? Text(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }
}
