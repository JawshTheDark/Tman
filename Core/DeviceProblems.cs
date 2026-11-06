namespace Tman.Core;

/// <summary>Device Manager's problem codes (Win32_PnPEntity.ConfigManagerErrorCode) in plain words, with what to try.</summary>
public static class DeviceProblems
{
    public sealed record Problem(string Short, string Meaning, string Fix);

    const string ReinstallDriver = "Update the driver, or uninstall the device in Device Manager and restart so Windows sets it up again.";
    const string RestartFirst = "Restart the PC. If the problem comes back, update the driver.";

    static readonly Dictionary<int, Problem> Table = new()
    {
        [1] = new("Not set up", "Windows hasn't set this device up correctly.", ReinstallDriver),
        [3] = new("Driver may be damaged", "The driver may be damaged, or Windows may be short of memory.", "Close some apps and restart. If it comes back, reinstall the driver."),
        [9] = new("Not identified", "Windows can't identify this hardware.", "Look for a driver or firmware update from the device's maker."),
        [10] = new("Can't start", "This device won't start.", "Update the driver from Windows Update or the maker's website, then restart. If it plugs in, try another port."),
        [12] = new("Not enough resources", "This device can't get the system resources it needs.", "Disable a device you don't use, or check for a BIOS update."),
        [14] = new("Needs a restart", "This device won't work until you restart.", "Restart the PC."),
        [18] = new("Driver needs reinstalling", "The drivers for this device need to be installed again.", ReinstallDriver),
        [19] = new("Settings damaged", "This device's settings in the registry are damaged or incomplete.", ReinstallDriver),
        [21] = new("Being removed", "Windows is removing this device.", "Wait a moment and refresh. If it stays like this, restart the PC."),
        [22] = new("Disabled", "This device has been turned off.", "Select Enable to turn it back on."),
        [24] = new("Not working", "This device isn't there, isn't working, or is missing a driver.", "Check it's connected and powered, then update the driver."),
        [28] = new("No driver", "The driver for this device isn't installed.", "Install it from Windows Update or the maker's website, then restart."),
        [29] = new("Off in firmware", "The PC's firmware didn't give this device any resources.", "Check the device is turned on in your BIOS or UEFI settings."),
        [31] = new("Driver won't load", "Windows can't load the driver this device needs.", ReinstallDriver),
        [32] = new("Driver service disabled", "The service behind this device's driver is turned off.", "Reinstall the driver, or set its service to start again on the Services page."),
        [33] = new("Hardware problem", "Windows can't work out what resources this device needs, which often means failing hardware.", "Check the connection. If that doesn't help, the device may need replacing."),
        [37] = new("Driver failed to start", "The driver for this device failed when it started.", ReinstallDriver),
        [38] = new("Old driver still loaded", "An earlier copy of the driver is still in memory.", "Restart the PC."),
        [39] = new("Driver missing or damaged", "Windows can't load the driver, because it's missing or damaged.", ReinstallDriver),
        [40] = new("Settings damaged", "The driver's entry in the registry is damaged.", ReinstallDriver),
        [41] = new("Hardware not found", "The driver loaded, but Windows can't find the hardware.", "Check the device is connected, then reinstall the driver."),
        [42] = new("Duplicate device", "Windows thinks this device is already running.", RestartFirst),
        [43] = new("Stopped after an error", "Windows stopped this device because it reported a problem.", "Unplug it and plug it back in, or restart, then update the driver. If it keeps happening, the hardware may be failing."),
        [44] = new("Shut down by an app", "An app or service shut this device down.", RestartFirst),
        [45] = new("Not connected", "This device isn't connected right now.", "Plug it back in."),
        [46] = new("Windows is shutting down", "Windows can't reach this device while it's shutting down.", "This clears by itself after a restart."),
        [47] = new("Ready to unplug", "This device was prepared for safe removal.", "Unplug it and plug it back in."),
        [48] = new("Driver blocked", "Windows blocked this driver because of known problems with it.", "Get a newer driver from the device's maker."),
        [50] = new("Still setting up", "Windows is still applying this device's settings.", "Wait a moment and refresh."),
        [51] = new("Waiting on another device", "This device is waiting for another device to start.", "Fix any other device with a problem first, then restart."),
        [52] = new("Driver not verified", "Windows can't verify the driver's digital signature.", "Get a signed driver from Windows Update or the device's maker."),
        [54] = new("Failed and resetting", "This device failed and is being reset.", "Wait a moment and refresh. If it stays like this, restart the PC."),
    };

    public static Problem For(int code) => Table.TryGetValue(code, out var p) ? p
        : new Problem("Problem", $"Windows reports problem code {code} for this device.", "Update the driver, then search online for this code and device.");
}
