using System.Net.NetworkInformation;
using System.Windows.Threading;
using Microsoft.Win32;
using Tman.Core;

namespace Tman.Monitoring;

public sealed record InfoSection(string Title, List<KeyValuePair<string, string>> Rows);

public sealed record DiskInfo(string Model, ulong Size, string Type);

/// <summary>Hardware/OS facts that don't change while running. Loaded once in the background (WMI is slow).</summary>
public sealed class StaticInfo
{
    public static StaticInfo Current { get; private set; } = new();
    public static bool IsLoaded { get; private set; }
    public static event Action? Loaded;

    public string CpuName { get; private set; } = "";
    public double BaseMhz { get; private set; }
    public int Sockets { get; private set; } = 1;
    public int Cores { get; private set; }
    public int Logical { get; private set; } = Environment.ProcessorCount;
    public string Virtualization { get; private set; } = "";
    public string L1 { get; private set; } = "";
    public string L2 { get; private set; } = "";
    public string L3 { get; private set; } = "";

    public ulong InstalledMemory { get; private set; }
    public string MemorySpeed { get; private set; } = "";
    public string Slots { get; private set; } = "";
    public string FormFactor { get; private set; } = "";

    public Dictionary<int, DiskInfo> Disks { get; } = new();
    public Dictionary<string, (string Version, string Date)> GpuDrivers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<InfoSection> Sections { get; } = new();

    public static void LoadAsync(Dispatcher ui) => Task.Run(() =>
    {
        var s = new StaticInfo();
        try { s.Load(); } catch { }
        Current = s;
        IsLoaded = true;
        ui.BeginInvoke(() => Loaded?.Invoke());
    });

    void Load()
    {
        // ---- Processor ----
        var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        CpuName = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        BaseMhz = cpuKey?.GetValue("~MHz") as int? ?? 0;

        var procs = Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, VirtualizationFirmwareEnabled, L2CacheSize, L3CacheSize FROM Win32_Processor");
        if (procs.Count > 0)
        {
            Sockets = procs.Count;
            Cores = procs.Sum(p => (int)p.U64("NumberOfCores"));
            Logical = Math.Max(1, procs.Sum(p => (int)p.U64("NumberOfLogicalProcessors")));
            if (CpuName == "") CpuName = procs[0].Str("Name");
            if (BaseMhz == 0) BaseMhz = procs[0].U64("MaxClockSpeed");
        }

        var cs = Wmi.Query("SELECT Manufacturer, Model, Domain, PartOfDomain, Workgroup, HypervisorPresent, TotalPhysicalMemory FROM Win32_ComputerSystem").FirstOrDefault();
        bool hypervisor = cs?.Bool("HypervisorPresent") == true;
        var vfe = procs.FirstOrDefault()?.Bool("VirtualizationFirmwareEnabled");
        Virtualization = hypervisor || vfe == true ? "Enabled" : vfe == false ? "Disabled" : "Unknown";

        var caches = Wmi.Query("SELECT Level, InstalledSize FROM Win32_CacheMemory");
        string CacheSize(int level)
        {
            var kb = caches.Where(c => (int)c.U64("Level") == level).Sum(c => (double)c.U64("InstalledSize"));
            return kb > 0 ? Fmt.Bytes(kb * 1024) : "";
        }
        L1 = CacheSize(3);
        L2 = CacheSize(4);
        L3 = CacheSize(5);
        if (L2 == "" && procs.Count > 0 && procs[0].U64("L2CacheSize") > 0) L2 = Fmt.Bytes(procs.Sum(p => (double)p.U64("L2CacheSize")) * 1024);
        if (L3 == "" && procs.Count > 0 && procs[0].U64("L3CacheSize") > 0) L3 = Fmt.Bytes(procs.Sum(p => (double)p.U64("L3CacheSize")) * 1024);

        // ---- Memory ----
        if (Native.GetPhysicallyInstalledSystemMemory(out var kb)) InstalledMemory = kb * 1024;
        var modules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, FormFactor, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");
        var speed = modules.Select(m => Math.Max(m.U64("ConfiguredClockSpeed"), m.U64("Speed"))).DefaultIfEmpty(0UL).Max();
        MemorySpeed = speed > 0 ? $"{speed} MT/s" : "";
        var totalSlots = Wmi.Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => (int)a.U64("MemoryDevices"));
        Slots = modules.Count > 0 ? (totalSlots > 0 ? $"{modules.Count} of {totalSlots}" : modules.Count.ToString()) : "";
        FormFactor = modules.FirstOrDefault()?.U64("FormFactor") switch { 8 => "DIMM", 12 => "SODIMM", null => "", _ => "Other" };

        // ---- Disks ----
        var phys = Wmi.Query("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage")
            .ToDictionary(p => p.Str("DeviceId"), p => (Media: p.U64("MediaType"), Bus: p.U64("BusType")));
        foreach (var d in Wmi.Query("SELECT Index, Model, Size, InterfaceType FROM Win32_DiskDrive"))
        {
            int index = (int)d.U64("Index");
            string type = "";
            if (phys.TryGetValue(index.ToString(), out var p))
            {
                var media = p.Media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                var bus = p.Bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD", _ => "" };
                type = p.Bus == 7 ? "Removable" : media != "" && bus != "" ? $"{media} ({bus})" : media != "" ? media : bus;
            }
            Disks[index] = new DiskInfo(d.Str("Model"), d.U64("Size"), type);
        }

        // ---- GPU drivers ----
        foreach (var v in Wmi.Query("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
            GpuDrivers[v.Str("Name")] = (v.Str("DriverVersion"), v.Date("DriverDate")?.ToString("d") ?? "");

        // ---- Sections for the System Info page ----
        var os = Wmi.Query("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, RegisteredUser FROM Win32_OperatingSystem").FirstOrDefault();
        var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var displayVersion = cv?.GetValue("DisplayVersion") as string ?? "";
        var ubr = cv?.GetValue("UBR") is int u ? u.ToString() : "";
        var build = os?.Str("BuildNumber") ?? Environment.OSVersion.Version.Build.ToString();

        var osRows = new List<KeyValuePair<string, string>>();
        Add(osRows, "Edition", os?.Str("Caption").Replace("Microsoft ", "") ?? "");
        Add(osRows, "Version", displayVersion);
        Add(osRows, "OS build", ubr != "" ? $"{build}.{ubr}" : build);
        Add(osRows, "Architecture", os?.Str("OSArchitecture") ?? "");
        Add(osRows, "Installed on", os?.Date("InstallDate")?.ToString("g") ?? "");
        Add(osRows, "Last boot", os?.Date("LastBootUpTime")?.ToString("g") ?? "");
        Add(osRows, "Computer name", Environment.MachineName);
        Add(osRows, "User", $@"{Environment.UserDomainName}\{Environment.UserName}");
        if (cs != null)
            Add(osRows, cs.Bool("PartOfDomain") == true ? "Domain" : "Workgroup", cs.Bool("PartOfDomain") == true ? cs.Str("Domain") : cs.Str("Workgroup"));
        Sections.Add(new InfoSection("Operating system", osRows));

        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        var devRows = new List<KeyValuePair<string, string>>();
        Add(devRows, "Manufacturer", cs?.Str("Manufacturer") ?? "");
        Add(devRows, "Model", cs?.Str("Model") ?? "");
        Add(devRows, "Motherboard", board != null ? $"{board.Str("Manufacturer")} {board.Str("Product")}".Trim() : "");
        Add(devRows, "BIOS", bios != null ? $"{bios.Str("Manufacturer")} {bios.Str("SMBIOSBIOSVersion")}".Trim() : "");
        Add(devRows, "BIOS date", bios?.Date("ReleaseDate")?.ToString("d") ?? "");
        if (Native.GetFirmwareType(out var fw)) Add(devRows, "Firmware", fw == 2 ? "UEFI" : fw == 1 ? "Legacy BIOS" : "Unknown");
        var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State")?.GetValue("UEFISecureBootEnabled");
        if (sb is int sbv) Add(devRows, "Secure Boot", sbv == 1 ? "On" : "Off");
        Sections.Add(new InfoSection("Device", devRows));

        var cpuRows = new List<KeyValuePair<string, string>>();
        Add(cpuRows, "Name", CpuName);
        Add(cpuRows, "Base speed", BaseMhz > 0 ? Fmt.Mhz(BaseMhz) : "");
        Add(cpuRows, "Sockets", Sockets.ToString());
        Add(cpuRows, "Cores", Cores > 0 ? Cores.ToString() : "");
        Add(cpuRows, "Logical processors", Logical.ToString());
        Add(cpuRows, "Virtualization", Virtualization);
        Add(cpuRows, "L1 cache", L1);
        Add(cpuRows, "L2 cache", L2);
        Add(cpuRows, "L3 cache", L3);
        Sections.Add(new InfoSection("Processor", cpuRows));

        var memRows = new List<KeyValuePair<string, string>>();
        Add(memRows, "Installed", InstalledMemory > 0 ? Fmt.Bytes(InstalledMemory) : "");
        Add(memRows, "Usable", cs != null ? Fmt.Bytes(cs.U64("TotalPhysicalMemory")) : "");
        Add(memRows, "Speed", MemorySpeed);
        Add(memRows, "Slots used", Slots);
        Add(memRows, "Form factor", FormFactor);
        foreach (var m in modules)
        {
            var parts = new[] { Fmt.Bytes(m.U64("Capacity")), m.Str("Manufacturer"), m.Str("PartNumber") }.Where(x => x != "" && x != "Unknown");
            Add(memRows, m.Str("DeviceLocator") is { Length: > 0 } loc ? loc : "Module", string.Join(" · ", parts));
        }
        Sections.Add(new InfoSection("Memory", memRows));

        var gpuRows = new List<KeyValuePair<string, string>>();
        foreach (var a in Dxgi.Enumerate())
        {
            var parts = new List<string> { $"{Fmt.Bytes(a.Dedicated)} dedicated" };
            if (GpuDrivers.TryGetValue(a.Name, out var drv) && drv.Version != "")
                parts.Add($"driver {drv.Version}" + (drv.Date != "" ? $" ({drv.Date})" : ""));
            Add(gpuRows, a.Name, string.Join(" · ", parts));
        }
        if (gpuRows.Count > 0) Sections.Add(new InfoSection("Graphics", gpuRows));

        var diskRows = new List<KeyValuePair<string, string>>();
        foreach (var (index, d) in Disks.OrderBy(kv => kv.Key))
            Add(diskRows, $"Disk {index}", string.Join(" · ", new[] { d.Model, Fmt.Bytes(d.Size), d.Type }.Where(x => x != "")));
        if (diskRows.Count > 0) Sections.Add(new InfoSection("Storage", diskRows));

        var netRows = new List<KeyValuePair<string, string>>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                Add(netRows, ni.Name, string.Join(" · ", new[] { ni.Description, mac }.Where(x => x != "")));
            }
        }
        catch { }
        if (netRows.Count > 0) Sections.Add(new InfoSection("Network adapters", netRows));
    }

    static void Add(List<KeyValuePair<string, string>> rows, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) rows.Add(new(key, value.Trim()));
    }
}
