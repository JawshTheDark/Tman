# Building Tman

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
