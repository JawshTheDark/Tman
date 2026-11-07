# Getting started

## Install

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for x64.
2. Download `Tman.exe` from the [latest release](https://github.com/JawshTheDark/Tman/releases).
3. Run it. It doesn't need installing, so you can keep it in any folder.

Windows SmartScreen may warn you the first time, because Tman isn't code-signed. To check your download, compare its SHA-256 hash with the one in the release notes:

```powershell
Get-FileHash .\Tman.exe -Algorithm SHA256
```

## Administrator rights

Tman runs as a standard user, and most pages work that way. Changing the whole PC needs more, such as firewall rules, latency traces, repairs and system-wide tweaks. Those pages tell you, and offer **Restart as administrator**. You can also use **File > Restart as administrator**.

## Closing and reopening

Closing the window keeps Tman running in the [tray](Tray-Icon.md). To exit fully, right-click the tray icon and choose **Exit Tman**.

Only one copy runs at a time. Starting Tman again brings the running window forward. If you start a newer build while an older one is in the tray, Tman offers to replace it.

## Where your data lives

Everything Tman saves is in `%APPDATA%\Tman\`. Delete the folder and Tman starts fresh.

| File or folder | What it holds |
| --- | --- |
| `settings.json` | Window, columns, tray and page settings |
| `app-history.json`, `benchmarks.json` | App history and saved benchmark runs |
| `leak-history.json`, `leak-scans.txt` | The leak monitor's samples since the last restart, and the scan log |
| `tweaks-undo.json` | The old value of every tweak Tman changed, so Undo puts back exactly what was there |
| `backups\` | Dated copies of the hosts file and environment variables from before each change |
| `docker-templates\`, `docker-icons\` | Saved template lists and app icons for Quick deploy |
| `docker-my-templates.json`, `docker-stacks\`, `docker-map.json` | Your own templates, each deployed stack's Compose file, and where you moved the boxes on the network map |
| `iso-build.json`, `iso-cleanup.log` | The ISO builder's work folder while a build runs, and any cleanup done after Tman was gone |
| `errors.log` | Unexpected errors, worth attaching to a bug report |

Nothing is sent anywhere. The leak scan log names apps and drivers but not your user name, PC name or file paths.
