namespace Tman.Core;

/// <summary>Plain names for the numbers Windows records when something crashes.</summary>
public static class CrashCodes
{
    /// <summary>The common stop codes, with what each one usually means where a sentence can say it.</summary>
    static readonly Dictionary<uint, (string Name, string Meaning)> Stops = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "A driver or Windows itself touched memory it wasn't allowed to. Usually a faulty driver, sometimes bad RAM."),
        [0x19] = ("BAD_POOL_HEADER", "Windows found its kernel memory damaged. Usually a faulty driver, sometimes bad RAM."),
        [0x1A] = ("MEMORY_MANAGEMENT", "Windows found its memory bookkeeping damaged. Often bad RAM or a faulty driver."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "Code in the kernel hit an error nothing could handle. Usually a faulty driver."),
        [0x22] = ("FILE_SYSTEM", ""),
        [0x24] = ("NTFS_FILE_SYSTEM", "The NTFS file system hit a problem it couldn't handle. Often a failing or damaged drive."),
        [0x2E] = ("DATA_BUS_ERROR", "Hardware reported a memory error. Often bad RAM."),
        [0x34] = ("CACHE_MANAGER", ""),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "A call into the kernel failed. Usually a faulty driver, often graphics or antivirus."),
        [0x3D] = ("INTERRUPT_EXCEPTION_NOT_HANDLED", ""),
        [0x3F] = ("NO_MORE_SYSTEM_PTES", ""),
        [0x44] = ("MULTIPLE_IRP_COMPLETE_REQUESTS", "A driver finished the same request twice. A driver bug."),
        [0x4A] = ("IRQL_GT_ZERO_AT_SYSTEM_SERVICE", ""),
        [0x4E] = ("PFN_LIST_CORRUPT", "Windows found its list of memory pages damaged. Often a faulty driver or bad RAM."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Windows or a driver used a memory address that isn't valid. Often a faulty driver or bad RAM."),
        [0x51] = ("REGISTRY_ERROR", ""),
        [0x5A] = ("CRITICAL_SERVICE_FAILED", ""),
        [0x5C] = ("HAL_INITIALIZATION_FAILED", ""),
        [0x74] = ("BAD_SYSTEM_CONFIG_INFO", "The registry's system settings are damaged or missing."),
        [0x76] = ("PROCESS_HAS_LOCKED_PAGES", ""),
        [0x77] = ("KERNEL_STACK_INPAGE_ERROR", "Windows couldn't read kernel data back from disk. Check the drive and its cable."),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Windows couldn't read kernel data back from disk. Check the drive and its cable."),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", "Windows lost the drive it starts from. Often a storage driver, a changed BIOS storage setting or a failing drive."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A Windows system thread hit an error nothing could handle. Usually a faulty driver."),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "The processor raised a fault the kernel couldn't handle. Often hardware, overclocking or a faulty driver."),
        [0x80] = ("NMI_HARDWARE_FAILURE", "Hardware signaled a failure it couldn't recover from."),
        [0x8E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED", "Code in the kernel hit an error nothing could handle. Usually a faulty driver."),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", "The processor reported a hardware error."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "A driver didn't finish a sleep, wake or power change in time."),
        [0xA0] = ("INTERNAL_POWER_ERROR", "The power manager hit an error it couldn't recover from."),
        [0xA5] = ("ACPI_BIOS_ERROR", "The firmware's power tables are faulty. A BIOS update may help."),
        [0xBE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "A driver tried to write to read-only memory. A driver bug."),
        [0xC1] = ("SPECIAL_POOL_DETECTED_MEMORY_CORRUPTION", "A driver wrote outside its memory. A driver bug."),
        [0xC2] = ("BAD_POOL_CALLER", "A driver made a bad memory request. A driver bug."),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "Driver Verifier caught a driver breaking the rules."),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "A driver damaged kernel memory."),
        [0xC7] = ("TIMER_OR_DPC_INVALID", "A driver left a kernel timer behind in memory it had freed. A driver bug."),
        [0xC9] = ("DRIVER_VERIFIER_IOMANAGER_VIOLATION", "Driver Verifier caught a driver breaking the rules."),
        [0xCA] = ("PNP_DETECTED_FATAL_ERROR", "Plug and Play hit a problem it couldn't recover from, usually caused by a driver."),
        [0xCE] = ("DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS", "A driver unloaded while it still had work going on. A driver bug."),
        [0xD0] = ("DRIVER_CORRUPTED_MMPOOL", "A driver damaged kernel memory."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory it wasn't allowed to. The memory dump usually names it."),
        [0xD5] = ("DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "A driver used memory after freeing it. A driver bug."),
        [0xD8] = ("DRIVER_USED_EXCESSIVE_PTES", ""),
        [0xDA] = ("SYSTEM_PTE_MISUSE", ""),
        [0xE0] = ("ACPI_BIOS_FATAL_ERROR", "The firmware reported a fatal error. A BIOS update may help."),
        [0xE2] = ("MANUALLY_INITIATED_CRASH", "This crash was started on purpose, for example with a keyboard crash setting or a debugging tool."),
        [0xE6] = ("DRIVER_VERIFIER_DMA_VIOLATION", "Driver Verifier caught a driver breaking the rules."),
        [0xE7] = ("INVALID_FLOATING_POINT_STATE", ""),
        [0xEA] = ("THREAD_STUCK_IN_DEVICE_DRIVER", "A device driver got stuck waiting for its hardware. Very often the graphics driver or card."),
        [0xED] = ("UNMOUNTABLE_BOOT_VOLUME", "Windows couldn't mount the drive it starts from. Often a failing drive or a damaged file system."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "A process Windows can't run without stopped. Often damaged system files, a failing drive or a faulty driver."),
        [0xF2] = ("HARDWARE_INTERRUPT_STORM", "A device kept signaling the processor without stopping. A faulty device or driver."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "A process or thread Windows can't run without stopped."),
        [0xF5] = ("FLTMGR_FILE_SYSTEM", ""),
        [0xF7] = ("DRIVER_OVERRAN_STACK_BUFFER", "A driver overran its memory. A driver bug."),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "A driver tried to run code from memory marked as data. Often a driver bug."),
        [0xFE] = ("BUGCODE_USB_DRIVER", "The USB drivers hit an error they couldn't recover from. Often a faulty USB device or driver."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "One processor core stopped responding. Often overclocking, the BIOS or a failing processor."),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "Windows found its kernel code or data changed. Often a faulty driver or bad RAM."),
        [0x10D] = ("WDF_VIOLATION", "A driver misused the Windows driver framework. A driver bug."),
        [0x10E] = ("VIDEO_MEMORY_MANAGEMENT_INTERNAL", "The graphics memory manager hit a fatal error. Often the graphics driver or card."),
        [0x113] = ("VIDEO_DXGKRNL_FATAL_ERROR", "The Windows graphics kernel hit a fatal error. Often the graphics driver."),
        [0x116] = ("VIDEO_TDR_FAILURE", "The graphics driver stopped responding and Windows couldn't restart it. Often the driver, overheating or the card."),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "The graphics driver stopped responding in time."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "The graphics scheduler hit a fatal error. Often the graphics driver or card."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "The hardware reported an error it couldn't recover from. Often the processor, RAM, overclocking or overheating."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "A driver held the processor at high priority for too long. Often a storage, network or graphics driver."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "The kernel found one of its own data structures damaged. Often a faulty driver or bad RAM."),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "A driver damaged kernel memory."),
        [0x144] = ("BUGCODE_USB3_DRIVER", "The USB 3 drivers hit an error they couldn't recover from. Often a faulty USB device or driver."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "The memory compression store hit an error. Often a failing drive or bad RAM."),
        [0x164] = ("WIN32K_CRITICAL_FAILURE", ""),
        [0x18B] = ("SECURE_KERNEL_ERROR", ""),
        [0x1C8] = ("MANUALLY_INITIATED_POWER_BUTTON_HOLD", "The power button was held down to force the PC off, usually because it had frozen."),
        [0xC000021A] = ("WINLOGON_FATAL_ERROR", "A core Windows process failed. Often damaged system files or a bad driver."),
        [0xC0000221] = ("STATUS_IMAGE_CHECKSUM_MISMATCH", "A driver or system file is damaged."),
        [0xDEADDEAD] = ("MANUALLY_INITIATED_CRASH1", "This crash was started on purpose, usually from a debugger."),
    };

    const string UnknownStop = "Windows stopped itself to avoid damage. Search online for this stop code to see its likely causes.";

    /// <summary>Name and meaning of a stop code. The 0x10000000 bit only marks a variant of the same code.</summary>
    public static (string Name, string Meaning) Stop(uint code)
    {
        if (!Stops.TryGetValue(code, out var known) && (code & 0xF0000000) == 0x10000000) Stops.TryGetValue(code & 0x0FFFFFFF, out known);
        if (known.Name == null) return ("Unknown stop code", UnknownStop);
        return (known.Name, known.Meaning.Length > 0 ? known.Meaning : UnknownStop);
    }

    static readonly Dictionary<uint, (string Name, string Meaning)> Exceptions = new()
    {
        [0x40000015] = ("App ended itself", "The app hit an error and called for its own shutdown."),
        [0x80000003] = ("Breakpoint", "The app hit a breakpoint with no debugger attached. Apps often do this on purpose when they find a serious error."),
        [0xC0000005] = ("Access violation", "The app tried to use memory it had no right to. Usually a bug in the app or an add-on, sometimes bad RAM."),
        [0xC0000006] = ("In-page error", "Part of the app couldn't be read from disk. Often a failing drive, or a network or USB drive that went away."),
        [0xC000001D] = ("Illegal instruction", "The app ran an instruction this processor doesn't have. Often a damaged file or an app built for newer processors."),
        [0xC0000025] = ("Noncontinuable exception", "The app tried to carry on after an error it couldn't recover from."),
        [0xC000007B] = ("Bad program file", "A program file is damaged, or is 32-bit where 64-bit is needed, or the other way round. Reinstalling the app usually fixes it."),
        [0xC0000094] = ("Division by zero", "The app divided a number by zero, which is a bug in the app."),
        [0xC0000096] = ("Privileged instruction", "The app ran an instruction only Windows may use."),
        [0xC00000FD] = ("Stack overflow", "The app ran out of stack space, usually from code calling itself without end."),
        [0xC0000135] = ("DLL not found", "A file the app needs is missing. Reinstalling the app usually fixes it."),
        [0xC0000139] = ("Entry point not found", "A DLL doesn't match the version the app expects. Reinstalling the app usually fixes it."),
        [0xC0000142] = ("DLL failed to start", "A library the app needs failed while starting."),
        [0xC000027B] = ("Unhandled app error", "A Windows app hit an error it didn't handle."),
        [0xC0000374] = ("Heap corruption", "The app damaged its own memory. Usually a bug in the app or an add-on, sometimes bad RAM."),
        [0xC0000409] = ("Fail fast", "The app found something wrong and stopped itself at once. It's a deliberate exit, used for damaged memory and for errors the app can't survive."),
        [0xC0000417] = ("Invalid runtime parameter", "The app passed a bad value to the C runtime library, which stopped it."),
        [0xC000041D] = ("Error in a callback", "An error went unhandled in code Windows was calling back into."),
        [0xC0000602] = ("Fail fast", "The app found something wrong and stopped itself at once."),
        [0xC06D007E] = ("DLL not found", "A file the app needs is missing. Reinstalling the app usually fixes it."),
        [0xC06D007F] = ("Function not found in a DLL", "A DLL doesn't match the version the app expects. Reinstalling the app usually fixes it."),
        [0xE0434352] = (".NET exception", "A .NET app hit an error it didn't handle. The Application log usually has a .NET Runtime event at the same time with the details."),
        [0xE06D7363] = ("C++ exception", "The app hit an error it didn't handle."),
    };

    public static (string Name, string Meaning) Exception(uint code) =>
        Exceptions.TryGetValue(code, out var known) ? known : ("Unknown exception", "Windows has no common name for this code. Searching online for it may help.");

    /// <summary>What a faulting module usually is, or "" when there's nothing useful to say. Prefixes catch a graphics driver's many file names.</summary>
    public static string Module(string module, string app)
    {
        var m = module.ToLowerInvariant();
        if (m.Length == 0 || m == "unknown") return "Windows couldn't tell which file was running. The app may have jumped into damaged code.";
        if (m.Equals(app, StringComparison.OrdinalIgnoreCase)) return "The crash was in the app's own program file.";
        return m switch
        {
            "ntdll.dll" or "kernelbase.dll" or "kernel32.dll" => $"{module} is a core part of Windows. Crashes often surface there, but the cause is usually the app or something loaded into it.",
            "ucrtbase.dll" or "msvcrt.dll" or "vcruntime140.dll" or "vcruntime140_1.dll" or "msvcp140.dll" => $"{module} is the C and C++ runtime library. The cause is usually the app that called it.",
            "clr.dll" or "coreclr.dll" or "clrjit.dll" => $"{module} is part of .NET. The cause is usually the app running on it.",
            "d3d9.dll" or "d3d11.dll" or "d3d12.dll" or "d3d12core.dll" or "dxgi.dll" => $"{module} is part of DirectX. The cause is often the graphics driver or the app.",
            _ when m.StartsWith("nvoglv") || m.StartsWith("nvwgf") || m.StartsWith("nvd3d") || m.StartsWith("nvcuda") || m.StartsWith("nvgpucomp") => $"{module} is part of the NVIDIA graphics driver.",
            _ when m.StartsWith("amdxx") || m.StartsWith("atiumd") || m.StartsWith("aticfx") || m.StartsWith("atidxx") || m.StartsWith("atig") || m.StartsWith("amdvlk") => $"{module} is part of the AMD graphics driver.",
            _ when m.StartsWith("igd") || m.StartsWith("igc") => $"{module} is part of the Intel graphics driver.",
            _ => "",
        };
    }
}
