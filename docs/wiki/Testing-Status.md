# Testing status

Tman 0.2.0 is a pre-release. This page says plainly what has and hasn't been proven, so you can decide what to trust. It is updated with each release.

## Well tested

- Every page renders, as a standard user and as administrator.
- Monitoring and the read-only pages have run on real hardware: an AMD Ryzen desktop with an NVIDIA GPU and NVMe drives.
- Real latency captures, including cleanup after Tman is force-closed.
- The tweak engine, through apply, undo and restore, on a throwaway registry key.
- Template parsing and merging on all nine live lists, and refusal of malformed or hostile entries.
- The Compose formatter: Docker's own parser reads 426 real Compose files the same before and after it.
- One running copy, using real processes.
- The leak detector, on synthetic curves and a process that really leaks.
- The ISO builder: a full build from a Windows 11 build 26300 ISO, a USB write, and an install of the result in a Hyper-V VM under Secure Boot that reached the desktop on a local account with no network.

## Tested only with stand-ins

| Feature | How it was tested | Still needed |
|---|---|---|
| Docker actions | A stand-in `docker.exe`, proving the exact arguments sent, and the real `docker` command for listing | Deploying, editing and updating against real Docker Desktop |
| Hyper-V | Made-up machines for the layout, and the failure paths | Real virtual machines, and the built-in console |
| Tray flyout | Simulated hover signals and pointer | Hovering the real taskbar |
| Battery | Sample data | A real battery |
| Blue screen parsing | Made-up events | A real crash |
| SATA, USB and RAID disk health | Sample data | Real drives |

## Not run for real yet

- **Actions that change Windows:** tweaks on real settings, presets, firewall and DNS changes, account changes, Windows feature changes, Windows Update modes, repairs and cleanup on real folders.
- **ISO builder:** booting a PC from a written USB drive, the answer file on a PC with internet, and ending a build partway, other than stopping it during the mount.
- **Restart as administrator:** not retested with a real UAC prompt since the one-copy change.
- **Non-English Windows.**

Before using these on a PC you care about, create a restore point on the [Repair](Repair.md) page. To report a problem, [open an issue](https://github.com/JawshTheDark/Tman/issues/new/choose) and include `%APPDATA%\Tman\errors.log` if it has an entry.
