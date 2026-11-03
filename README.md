# Tman

A lightweight task manager for Windows 11, built with C# / WPF on .NET 10 and the Windows 11 Fluent dark theme.

## Pages

- **Summary**: an instrument-panel dashboard. LED arc gauges for CPU, memory, GPU (with temperature), disk, network and CPU power; the processor trace with an LED bar per logical processor; the busiest processes; memory, disk and network meters.
- **Performance**: 60-second graphs per device. Includes a per-core CPU view, GPU engine graphs (3D, copy, video), GPU temperature, power, fan and clocks (NVIDIA through NVML, other GPUs through the same driver data Task Manager uses), disk transfer rates and network throughput.
- **Processes**: every process, with CPU, memory, I/O and GPU columns shaded by load, and a Signer column (verified Authenticode signer, including Windows catalog signatures; unsigned programs show in amber). Supports search, end task or end tree, suspend/resume, set priority and CPU affinity, create a full memory dump, open file location and properties. Double-click (or Enter) opens a Details window with threads (state, CPU, name, start address), loaded modules, environment variables and open files. Right-click a column header to choose which columns show.
- **System info**: OS, board, BIOS, CPU, RAM modules, GPUs, disks and network adapters, with a "Copy all" button.
- **App history**: CPU time, GPU time, disk I/O, peak memory and launch count per app, kept across sessions. Windows keeps its own history in a database only Task Manager can read, so Tman records its own while it runs.
- **Users**: signed-in sessions with their process count, CPU and memory. You can disconnect a session or sign it off.
- **Startup apps**: registry Run keys and Startup folders, enabled or disabled the same way Windows does it (the StartupApproved keys); scheduled tasks that run at sign-in or startup (Windows' own tasks hidden unless you ask); plus RunOnce, Group Policy Run keys, Winlogon shell overrides and AppInit DLLs, which are shown for awareness and can be opened in Registry Editor.
- **Services**: start, stop and restart services, change the startup type, and filter by state.
- **Power & freq**: measured clock for each logical processor, throttling, core parking, CPU package and per-core power (from RAPL energy counters, where the CPU exposes them), and the power plan and Windows 11 power mode, which you can switch from here.
- **Flight recorder**: the last hour of CPU, memory, GPU, disk, network and power in memory. Click the timeline to see the top processes at that moment. It also logs process starts and exits and sustained CPU or memory spikes, and exports to CSV.
- **Connections**: TCP/UDP connections by owning process, with optional reverse-DNS host names.
- **File locks**: find which processes have a file or folder open (or a DLL loaded), the usual cause of "file in use" errors, and end them. As a standard user only your own processes can be searched.
- **Installed apps**: the uninstall registry, with size, version and date, plus an Uninstall button.
- **Drivers**: signed PnP drivers with provider, version, date and signature, and an option to hide Microsoft drivers.
- **Disk space**: drive usage, plus a fast parallel folder-size analyzer you can drill into.
- **Benchmarks**: about 30 seconds of CPU (single and multi-thread), memory (bandwidth and latency) and drive tests (sequential, random 4K at queue depths 32 and 1, unbuffered). Results are saved so you can compare runs. CPU scores are relative, with 1,000 about one Ryzen 9 7950X core. The drive test writes at most about 5 GB to a temp file that deletes itself.

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

Settings, app history and benchmark results live in `%APPDATA%\Tman\` (`settings.json`, `app-history.json`, `benchmarks.json`).

## Dev flags

- `--snap <dir> [page] [extra]` renders pages to PNGs off-screen and exits. Scrolling pages also get a `-full.png` capture. `extra` depends on the page:
  - Disk space: a folder to analyze.
  - Benchmarks: any value runs the benchmarks first.
  - Flight recorder: the number of seconds to record first.
  - File locks: a path or name to search for.
  - Performance: a device key prefix to select, e.g. `gpu`.
- `--snap <dir> details [pid]` renders each tab of the process Details window (Tman itself by default) and writes `details-checks.txt` with dump, affinity, signature and GPU sensor checks.
- `--offscreen [page]` runs normally but out of view, for profiling.
