# Tman

A lightweight task manager for Windows 11, built with C# / WPF on .NET 10 and the Windows 11 Fluent dark theme.

## Pages

- **Summary**: an instrument-panel dashboard. LED arc gauges for CPU, memory, GPU (with temperature), disk, network and CPU power; the processor trace with an LED bar per logical processor; the busiest processes; memory, disk and network meters.
- **Performance**: 60-second graphs per device. Includes a per-core CPU view, GPU engine graphs (3D, copy, video), GPU temperature, power, fan and clocks (NVIDIA through NVML, other GPUs through the same driver data Task Manager uses), disk transfer rates and network throughput.
- **Processes**: every process, with CPU, memory, I/O and GPU columns shaded by load, and a Signer column (verified Authenticode signer, including Windows catalog signatures; unsigned programs show in amber). Supports search, end task or end tree, suspend/resume, set priority and CPU affinity, create a full memory dump, open file location and properties. Double-click (or Enter) opens a Details window with threads (state, CPU, name, start address), loaded modules, environment variables and open files. Its Wait chain tab, also reachable from "Analyze wait chain" on the right-click menu, shows what each blocked thread is waiting for, follows the chain into other processes by name, and calls out deadlocks in plain words. Apps that have stopped responding show in red in Status. A Network column shows each process's combined send and receive rate, from a trace that runs only as administrator and only while the column is visible. Right-click a column header to choose which columns show.
- **System info**: OS, board, BIOS, CPU, RAM modules, GPUs, disks and network adapters, with a "Copy all" button.
- **App history**: CPU time, GPU time, disk I/O, peak memory and launch count per app, kept across sessions. Windows keeps its own history in a database only Task Manager can read, so Tman records its own while it runs.
- **Users**: signed-in sessions with their process count, CPU and memory. You can disconnect a session or sign it off.
- **Startup apps**: registry Run keys and Startup folders, enabled or disabled the same way Windows does it (the StartupApproved keys); scheduled tasks that run at sign-in or startup (Windows' own tasks hidden unless you ask); plus RunOnce, Group Policy Run keys, Winlogon shell overrides and AppInit DLLs, which are shown for awareness and can be opened in Registry Editor.
- **Services**: start, stop and restart services, change the startup type, and filter by state.
- **Power & freq**: measured clock for each logical processor, throttling, core parking, CPU package and per-core power (from RAPL energy counters, where the CPU exposes them), and the power plan and Windows 11 power mode, which you can switch from here. It also shows the last wake source, recent sleep and wake history, wake timers, devices allowed to wake the PC, and what's keeping it awake. Wake timers and what's keeping it awake need administrator rights.
- **Flight recorder**: the last hour of CPU, memory, GPU, disk, network and power in memory. Click the timeline to see the top processes at that moment. It also logs process starts and exits and sustained CPU or memory spikes, and exports to CSV.
- **Connections**: TCP/UDP connections by owning process, with optional reverse-DNS host names. Right-click to block a program's outgoing traffic with a Tman-named firewall rule (needs admin).
- **File locks**: find which processes have a file or folder open (or a DLL loaded), the usual cause of "file in use" errors, and end them. As a standard user only your own processes can be searched.
- **Installed apps**: the uninstall registry, with size, version and date, plus an Uninstall button.
- **Drivers**: signed PnP drivers with provider, version, date and signature, and an option to hide Microsoft drivers.
- **Disk space**: drive usage, plus a fast parallel folder-size analyzer you can drill into. The Cleanup tab sizes up temp files, the Recycle Bin, thumbnail cache, crash dumps, error reports, update and Delivery Optimization files and the DirectX shader cache, then cleans what you tick. It skips files in use and reports how much was freed and skipped.
- **Benchmarks**: about 30 seconds of CPU (single and multi-thread), memory (bandwidth and latency) and drive tests (sequential, random 4K at queue depths 32 and 1, unbuffered). Results are saved so you can compare runs. CPU scores are relative, with 1,000 about one Ryzen 9 7950X core. The drive test writes at most about 5 GB to a temp file that deletes itself.

### Diagnose

- **Crash history**: one timeline of blue screens, unexpected shutdowns, app crashes and app hangs from the event logs, with counters, top crashing apps and filters by kind and time range. Stop codes and exception codes are explained in plain words, repeats of one app are merged, and blue screens link to their minidump.
- **Event log**: a fast viewer for any readable log, filtered by level, time range and search text, with an option to group by source and the full message in a details pane. Reads are limited by time and capped at 10,000 events, and the page says when the cap is reached. Reading the Security log needs administrator rights.
- **Disk health**: a card per physical drive with a verdict (Healthy, Check soon, Failing or Unknown) and the reason, plus temperature, life remaining, power-on time, data written and error counts. NVMe health is read without admin; admin adds read and write error totals and SMART data for SATA drives. A drive that doesn't report enough stays Unknown, never Healthy by default.
- **Devices**: Device Manager's list with problem devices first, each problem in plain words with what to try. Filter by class or "Problems only", search, enable or disable a device, scan for hardware changes (those three need admin), and open Device Manager or search online.
- **Battery**: charge, state, charge rate, voltage and time remaining, updated live, plus design against full charge capacity, wear and a plain verdict, and the cycle count where the battery reports one. Desktops show "This PC has no battery".
- **Latency**: finds what's causing stutter and audio crackle. Runs a fixed kernel trace of every DPC and ISR, then shows per driver how many there were and their total, average and longest time in microseconds, with a plain verdict for audio and gaming and the likely culprit. Needs administrator rights.
- **Memory map**: what RAM is really used for. A bar of in use, modified, standby, free, zero and hardware reserved with a plain line for each, standby by priority, commit, paged and non-paged pool, file cache and the top pool tags. It can also empty the standby list (admin), though Windows refills it and it rarely helps.

### Admin

- **Tweaks**: 153 Windows settings for privacy, AI features, ads, Explorer, the right-click menu, the taskbar, notifications, performance, input, Windows Update, security and the system, each showing the live value and applying as soon as you click it. Search them or filter by group. Tman saves the old value first, so turning a tweak off puts back exactly what was there. Minimal, Standard and Advanced presets confirm the exact list and create a restore point first when running as administrator. The set can be exported and imported to apply on another PC. Tweaks that change the whole PC need admin.
- **Context menus**: every entry Explorer can add to its right-click menus, including shell extensions and Windows 11 app entries, with where it shows, who added it and what it runs. Hide an entry and bring it back, keep a command for Shift + right-click, or add your own commands for files, folders, the desktop and drives. Hiding needs no admin and never deletes anything, Tman only deletes commands added here, and anything Windows needs to open items stays locked.
- **Get apps**: a bulk installer built on winget. Browse has 120 popular free apps by category with install status, plus a live search of the whole winget catalog; tick apps to install or uninstall them, or export the list as a JSON file that works on another PC. Updates lists available updates with Update selected and Update all. Built-in apps removes bundled Store apps for your account, keeps anything Windows depends on protected, and has a Select common extras button. Everything runs through a one-at-a-time queue with a live log and per-app results, and the queue can be cancelled between apps. Installing accepts each app's license and the winget source's terms for you.
- **Windows features**: the useful optional features with a plain description, every other feature with search, and OpenSSH client and server with service and firewall state. Turning something on or off runs through DISM with live output and says when a restart is needed.
- **ISO builder**: turns an official Windows 11 ISO into a smaller one. Choose an ISO you already have (Tman doesn't download one) and an edition, tick which Store apps and extras to remove, and optionally add an answer file that skips the Microsoft account step by creating a local administrator account with no password. Setup still asks which disk to install to. It checks for free space first (about 35 GB while it builds) and takes 20 minutes to an hour or more, with live output, and your original ISO isn't changed. The ISO is set up to boot on BIOS and UEFI, and is made with `oscdimg` if the Windows ADK is installed, or with Windows' built-in image writer if it isn't, so there's nothing to install. Stopping a build, closing Tman or ending it in Task Manager discards the mounted image and deletes the temporary files. A second Tman process watches each build to make sure of that, and if the power goes too, Tman finishes the job the next time it starts as administrator. A finished ISO can be written to a USB drive as FAT32, the format PCs can boot from. The confirmation names the drive and its size and warns that it erases the drive, and Tman refuses Windows' own drive and anything that isn't USB. Looking inside an ISO works for anyone; building and writing to USB need admin. Keep the ISO on a local drive, because running as administrator can hide mapped and mounted drives. This page is new and hasn't been run end to end yet: a full build, stopping one, writing a USB drive and booting the result are all still untested.
- **Hyper-V**: virtual machines on this PC with state, CPU, memory and uptime. Start, shut down, turn off, pause, resume and save a VM, open its console, and take, apply and delete checkpoints. New VM makes one from an ISO with a name, generation, memory, processors, disk size and network, plus Secure Boot and a virtual TPM for Generation 2. The defaults suit Windows 11, and if a step fails, what was made so far is removed. Turning off, applying a checkpoint and deleting each say what's lost first, and deleting a VM asks separately about its disk files, leaving out any that another VM still uses. If Hyper-V isn't turned on, the page says so and points to Windows features. It never turns Hyper-V on itself. Needs admin or membership of the Hyper-V Administrators group.
- **Control panels**: a launcher for classic Windows tools that are hard to find now, such as Programs and Features, Network Connections, User Accounts (automatic sign-in), Disk Management, Registry Editor, Group Policy, Reliability Monitor and the Windows Tools folder, grouped by purpose with a line on what each is for.
- **Scheduled tasks**: every task in every folder with its state, last run, last result in plain words, next run, author and action. Filter out Microsoft's tasks, show failed runs, and search. Run, end, enable or disable a task, and select one to see its triggers and full action.
- **Windows Update**: update history with failures explained in plain words, a background search for pending updates, the last check and install times, and an update mode: Default, Security-focused (feature updates wait 365 days, quality updates 4 days, drivers left out) or Pause for up to 5 weeks. It reads live values and says when someone else's policy set them. There is deliberately no option to turn updates off. Switching needs admin.
- **Accounts**: local users and groups with enabled state, administrator status, last sign-in, password required and group memberships. Enable or disable an account and change who is an administrator, with a refusal if that would leave no enabled administrator. Passwords are never shown or set: use the buttons for User Accounts and Local Users and Groups. Changes need admin.
- **Environment**: user and system variables as stored, so %NAME% references and REG_EXPAND_SZ survive. Add, edit and delete, plus a PATH editor that flags missing folders and duplicates and lets you reorder and remove entries. Each change saves a dated backup to `%APPDATA%\Tman\backups\` first and tells running apps. System variables need admin.
- **Shared folders**: shares with admin shares marked, connected sessions, open files, and whether file sharing is on for the network in use. Stop sharing or close a session. Sessions, open files and both actions need admin.
- **Docker**: containers with live CPU and memory, images, volumes, networks and Compose stacks, with start, stop, remove, logs, a shell and open in browser. Quick deploy merges any number of Portainer-format template lists into one gallery of apps with icons, keeps each app's different builds as versions, and shows a form and the exact command before anything runs. Ports stay on this PC unless you open them, and paths from a template author's server become Docker volumes. If Docker isn't installed, the page can install Docker Desktop.

### Network

- **Firewall**: Domain, Private and Public status with default actions, plus every rule with filters for direction, action, state and non-Microsoft rules. Enable, disable or delete rules and turn a profile on or off (needs admin).
- **Network tools**: live ping and traceroute, DNS lookup with reverse names, adapter details, DNS provider switching (Automatic, Cloudflare, Google, Quad9 or custom), flush DNS, restart adapter, and saved Wi-Fi network names. Passwords are never read.
- **Hosts file**: entries as rows plus raw text, with add, edit, remove, enable and disable. Comments, order, encoding and line endings are kept. Each save makes a dated backup you can restore and flushes the DNS cache, and entries that redirect well-known sites are flagged. Saving needs admin.
- **Security**: a read-only board for antivirus, firewall, Secure Boot, TPM, memory integrity, BitLocker, UAC, Smart App Control, SmartScreen and Remote Desktop, with what each state means. Anything Tman can't read shows as unknown, never good.

### Repair

- **System repair**: runs Windows' repair tools one at a time with live output and a plain-language result: scan system files, check or repair the Windows image, scan a drive for errors, reset the network, reset Windows Update (folders are renamed, never deleted) and repair winget. Needs administrator rights.
- **Restore points**: lists restore points with date, description and type, shows whether System Protection is on, creates a new one and explains when Windows skips it, and opens System Restore and System Protection. The list needs administrator rights.

## Tray icons

Tman shows one live icon in the system tray, with a colored bar for each of up to three metrics. Hover over it for a flyout with every reading, or choose separate icons with a number or LED meter each. Set this up in **View > Tray icons…**:

- **Metrics**: CPU usage, memory, GPU usage, GPU temperature, disk activity, network download, network upload, CPU power and CPU clock. CPU, memory and GPU are on by default. The order in the list is the order in the tray.
- **Style**: a number with a level strip, or an LED meter, each in the metric's color.
- **Behavior**: hover for the flyout (a plain tooltip with separate icons), click to open Tman, right-click for a menu (Open, Tray icons…, Exit). By default, closing the window keeps Tman running in the tray. You can turn that off in the same dialog.
- **One copy**: starting Tman again brings the running copy forward instead of adding a second set of icons. If the new start is a newer build, Tman offers to replace the older copy.
- **Visibility**: Windows 11 puts new tray icons in the hidden-icons flyout (^). **Always show on the taskbar** pins them, or use Settings > Personalization > Taskbar > Other system tray icons.

## Build and run

```bash
dotnet run -c Release
```

Single-file exe (needs the .NET 10 Desktop Runtime):

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
```

Tman runs as a normal user. Ending system processes, managing services and changing all-users startup entries need admin: use **File > Restart as administrator**.

## How it gets its data

- Processes come from a single `NtQuerySystemInformation` call. That gives CPU time, memory, I/O and thread state for every process (protected ones included) without opening handles. Image paths for protected processes come from `SystemProcessIdInformation`.
- CPU, GPU and disk use PDH counters. CPU uses `% Processor Utility`, the same counter Windows 11 Task Manager uses. GPU names and memory sizes come from DXGI.
- Per-processor clocks come from the `Actual Frequency` counter, throttling from `% Performance Limit`, and power from the `Energy Meter` counters. None of these need admin.
- Static hardware info is loaded once, in the background, through WMI.

Settings, app history and benchmark results live in `%APPDATA%\Tman\` (`settings.json`, `app-history.json`, `benchmarks.json`). The same folder holds the old values for undoing tweaks (`tweaks-undo.json`), and dated copies of the hosts file and environment variables from before each change (`backups\`). Quick deploy keeps saved lists in `docker-templates\`, icons in `docker-icons\` and each deployed stack in `docker-stacks\`. Unexpected errors are written to `errors.log`. While an ISO is being built, `iso-build.json` names its work folder so that it can be cleaned up even if Tman dies, and `iso-cleanup.log` records any cleanup done after Tman has gone.

## Dev flags

- `--snap <dir> [page] [extra]` renders pages to PNGs off-screen and exits. Scrolling pages also get a `-full.png` capture. `extra` depends on the page:
  - Disk space: a folder to analyze.
  - Benchmarks: any value runs the benchmarks first.
  - Flight recorder: the number of seconds to record first.
  - File locks: a path or name to search for.
  - Performance: a device key prefix to select, e.g. `gpu`.
  - ISO builder: an ISO to read first.
  - Hyper-V: `sample` fills the page with made-up machines, for checking the layout on a PC without Hyper-V, and `sample output` adds the job panel.
- `--size <w>x<h>` after `--snap` sets the window size for the render, e.g. `--size 820x520`.
- `--snap <dir> tray` renders every tray icon in both styles at each tray size into `tray-sheet.png`, briefly registers live icons to check the shell accepts them, and renders the settings dialog.
- `--snap <dir> details [pid]` renders each tab of the process Details window (Tman itself by default) and writes `details-checks.txt` with dump, affinity, signature and GPU sensor checks.
- `--snap <dir> latency [seconds]` runs a capture of that length first. It needs admin.
- `--offscreen [page]` runs normally but out of view, for profiling.
