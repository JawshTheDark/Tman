namespace Tman.Core;

/// <summary>The Get apps page's data: popular free apps by winget ID, and what's known about Windows' bundled Store apps.</summary>
public static class GetAppsCatalog
{
    public sealed record App(string Category, int Order, string Name, string Id, string Description);

    static readonly (string Category, (string Name, string Id, string Description)[] Apps)[] Table =
    {
        ("Browsers", new[]
        {
            ("Firefox", "Mozilla.Firefox", "Open source browser from Mozilla."),
            ("Google Chrome", "Google.Chrome", "Google's web browser."),
            ("Brave", "Brave.Brave", "Chromium browser that blocks ads and trackers."),
            ("Vivaldi", "Vivaldi.Vivaldi", "Highly customizable Chromium browser."),
            ("Opera", "Opera.Opera", "Chromium browser with a built-in VPN."),
            ("LibreWolf", "LibreWolf.LibreWolf", "Firefox build tuned for privacy."),
            ("Waterfox", "Waterfox.Waterfox", "Independent Firefox-based browser."),
            ("Tor Browser", "TorProject.TorBrowser", "Browses through the Tor network for anonymity."),
        }),
        ("Development", new[]
        {
            ("Visual Studio Code", "Microsoft.VisualStudioCode", "Popular code editor with extensions."),
            ("Git", "Git.Git", "Version control, with Git Bash."),
            ("GitHub Desktop", "GitHub.GitHubDesktop", "Simple desktop app for Git and GitHub."),
            ("GitHub CLI", "GitHub.cli", "GitHub from the command line."),
            ("Notepad++", "Notepad++.Notepad++", "Fast text and code editor."),
            ("Neovim", "Neovim.Neovim", "Modern terminal text editor."),
            ("Python 3.14", "Python.Python.3.14", "Python programming language."),
            ("Node.js LTS", "OpenJS.NodeJS.LTS", "JavaScript runtime, long-term support release."),
            ("Go", "GoLang.Go", "Go programming language."),
            ("Rustup", "Rustlang.Rustup", "Installs and manages the Rust toolchain."),
            (".NET 10 SDK", "Microsoft.DotNet.SDK.10", "Build .NET apps."),
            ("Temurin JDK 21", "EclipseAdoptium.Temurin.21.JDK", "Free OpenJDK Java development kit."),
            ("CMake", "Kitware.CMake", "Cross-platform build system."),
            ("PowerShell", "Microsoft.PowerShell", "The latest PowerShell, alongside the built-in one."),
            ("Docker Desktop", "Docker.DockerDesktop", "Run containers. Free for personal use."),
            ("Postman", "Postman.Postman", "Build and test web APIs."),
            ("Insomnia", "Insomnia.Insomnia", "Open source API client."),
            ("DB Browser for SQLite", "DBBrowserForSQLite.DBBrowserForSQLite", "Open and edit SQLite databases."),
            ("WinMerge", "WinMerge.WinMerge", "Compare and merge files and folders."),
        }),
        ("Utilities", new[]
        {
            ("7-Zip", "7zip.7zip", "Open and create zip, 7z, rar and other archives."),
            ("PeaZip", "Giorgiotani.Peazip", "Archive manager with a friendly interface."),
            ("NanaZip", "M2Team.NanaZip", "7-Zip fork that fits the Windows 11 context menu."),
            ("PowerToys", "Microsoft.PowerToys", "Microsoft's extra tools for power users."),
            ("Everything", "voidtools.Everything", "Finds files by name instantly."),
            ("Flow Launcher", "Flow-Launcher.Flow-Launcher", "Quick launcher for apps, files and searches."),
            ("Files", "FilesCommunity.Files", "Modern replacement for File Explorer."),
            ("ShareX", "ShareX.ShareX", "Screenshots, screen recording and sharing."),
            ("Greenshot", "Greenshot.Greenshot", "Simple screenshot tool with annotations."),
            ("Ditto", "Ditto.Ditto", "Clipboard history manager."),
            ("AutoHotkey", "AutoHotkey.AutoHotkey", "Automate tasks and remap keys with scripts."),
            ("LocalSend", "LocalSend.LocalSend", "Send files to nearby devices without the cloud."),
            ("Bitwarden", "Bitwarden.Bitwarden", "Password manager with free sync."),
            ("KeePassXC", "KeePassXCTeam.KeePassXC", "Offline password manager."),
            ("WizTree", "AntibodySoftware.WizTree", "Shows what fills your drive, fast."),
            ("WinDirStat", "WinDirStat.WinDirStat", "Maps disk usage by folder and file type."),
            ("Bulk Crap Uninstaller", "Klocman.BulkCrapUninstaller", "Removes many programs at once, with leftovers."),
            ("BleachBit", "BleachBit.BleachBit", "Cleans temp files and app caches."),
            ("HWiNFO", "REALiX.HWiNFO", "Detailed hardware information and sensors."),
            ("CPU-Z", "CPUID.CPU-Z", "Processor, memory and motherboard details."),
            ("GPU-Z", "TechPowerUp.GPU-Z", "Graphics card details and sensors."),
            ("CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo", "Drive health and temperature."),
            ("CrystalDiskMark", "CrystalDewWorld.CrystalDiskMark", "Drive speed benchmark."),
            ("Rufus", "Rufus.Rufus", "Makes bootable USB drives."),
            ("balenaEtcher", "Balena.Etcher", "Writes disk images to USB drives and SD cards."),
            ("Ventoy", "Ventoy.Ventoy", "Boot many ISO files from one USB drive."),
            ("Process Explorer", "Microsoft.Sysinternals.ProcessExplorer", "Microsoft's in-depth process viewer."),
            ("Autoruns", "Microsoft.Sysinternals.Autoruns", "Shows everything that starts with Windows."),
        }),
        ("Multimedia", new[]
        {
            ("VLC media player", "VideoLAN.VLC", "Plays almost any video or audio file."),
            ("MPC-HC", "clsid2.mpc-hc", "Light classic media player."),
            ("foobar2000", "PeterPawlowski.foobar2000", "Light, customizable music player."),
            ("Spotify", "Spotify.Spotify", "Music streaming."),
            ("Audacity", "Audacity.Audacity", "Record and edit audio."),
            ("OBS Studio", "OBSProject.OBSStudio", "Screen recording and live streaming."),
            ("HandBrake", "HandBrake.HandBrake", "Converts and compresses video."),
            ("Shotcut", "Meltytech.Shotcut", "Open source video editor."),
            ("Kdenlive", "KDE.Kdenlive", "Open source video editor from KDE."),
            ("FFmpeg", "Gyan.FFmpeg", "Command-line audio and video converter."),
            ("GIMP", "GIMP.GIMP.3", "Open source image editor."),
            ("Krita", "KDE.Krita", "Digital painting and illustration."),
            ("Inkscape", "Inkscape.Inkscape", "Vector graphics editor."),
            ("Paint.NET", "dotPDN.PaintDotNet", "Easy image editor with layers."),
            ("Blender", "BlenderFoundation.Blender", "3D modeling, animation and rendering."),
            ("darktable", "darktable.darktable", "Photo workflow and raw editor."),
            ("IrfanView", "IrfanSkiljan.IrfanView", "Fast image viewer. Free for personal use."),
            ("XnView MP", "XnSoft.XnViewMP", "Image viewer and organizer."),
        }),
        ("Gaming", new[]
        {
            ("Steam", "Valve.Steam", "Valve's game store and launcher."),
            ("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "Epic's game store and launcher."),
            ("GOG Galaxy", "GOG.Galaxy", "GOG's launcher for DRM-free games."),
            ("EA app", "ElectronicArts.EADesktop", "Electronic Arts' game launcher."),
            ("Ubisoft Connect", "Ubisoft.Connect", "Ubisoft's game launcher."),
            ("Battle.net", "Blizzard.BattleNet", "Blizzard's game launcher."),
            ("Playnite", "Playnite.Playnite", "One library for all your game launchers."),
            ("Heroic Games Launcher", "HeroicGamesLauncher.HeroicGamesLauncher", "Open source launcher for Epic and GOG."),
            ("Prism Launcher", "PrismLauncher.PrismLauncher", "Manages Minecraft instances and mods."),
            ("MSI Afterburner", "Guru3D.Afterburner", "Graphics card tuning and monitoring."),
            ("RivaTuner Statistics Server", "Guru3D.RTSS", "Frame rate limiter and on-screen stats."),
            ("Moonlight", "MoonlightGameStreamingProject.Moonlight", "Streams games from a PC running Sunshine."),
            ("Parsec", "Parsec.Parsec", "Low-latency remote play."),
        }),
        ("Communication", new[]
        {
            ("Discord", "Discord.Discord", "Voice, video and text chat."),
            ("Telegram Desktop", "Telegram.TelegramDesktop", "Fast cloud-based messaging."),
            ("Signal", "OpenWhisperSystems.Signal", "End-to-end encrypted messaging."),
            ("Slack", "SlackTechnologies.Slack", "Team chat."),
            ("Zoom", "Zoom.Zoom", "Video meetings."),
            ("Microsoft Teams", "Microsoft.Teams", "Chat and video meetings from Microsoft."),
            ("Thunderbird", "Mozilla.Thunderbird", "Open source email client."),
            ("Element", "Element.Element", "Secure chat on the Matrix network."),
        }),
        ("Documents", new[]
        {
            ("LibreOffice", "TheDocumentFoundation.LibreOffice", "Free office suite."),
            ("ONLYOFFICE", "ONLYOFFICE.DesktopEditors", "Office suite with strong Word and Excel support."),
            ("Adobe Acrobat Reader", "Adobe.Acrobat.Reader.64-bit", "Adobe's PDF reader."),
            ("SumatraPDF", "SumatraPDF.SumatraPDF", "Small, fast PDF and ebook reader."),
            ("PDFsam Basic", "PDFsam.PDFsam", "Split and merge PDF files."),
            ("calibre", "calibre.calibre", "Ebook library manager and converter."),
            ("Obsidian", "Obsidian.Obsidian", "Notes in plain Markdown files."),
            ("Joplin", "Joplin.Joplin", "Open source notes with sync."),
            ("Logseq", "Logseq.Logseq", "Outliner for connected notes."),
            ("Notion", "Notion.Notion", "Notes, docs and wikis."),
        }),
        ("Network and files", new[]
        {
            ("qBittorrent", "qBittorrent.qBittorrent", "Open source BitTorrent client."),
            ("WinSCP", "WinSCP.WinSCP", "SFTP, SCP and FTP file transfer."),
            ("PuTTY", "PuTTY.PuTTY", "SSH and Telnet client."),
            ("Wireshark", "WiresharkFoundation.Wireshark", "Captures and inspects network traffic."),
            ("Angry IP Scanner", "angryziber.AngryIPScanner", "Finds devices on your network."),
            ("WireGuard", "WireGuard.WireGuard", "Fast, simple VPN client."),
            ("Tailscale", "Tailscale.Tailscale", "Private network between your devices."),
            ("Proton VPN", "Proton.ProtonVPN", "VPN with a free plan."),
            ("TeamViewer", "TeamViewer.TeamViewer", "Remote desktop. Free for personal use."),
            ("Syncthing", "Syncthing.Syncthing", "Keeps folders in sync between your devices."),
            ("rclone", "Rclone.Rclone", "Command-line sync for cloud storage."),
            ("Dropbox", "Dropbox.Dropbox", "Cloud file storage."),
            ("Google Drive", "Google.GoogleDrive", "Google's cloud file storage."),
        }),
        ("Runtimes", new[]
        {
            ("Visual C++ Redistributable (x64)", "Microsoft.VCRedist.2015+.x64", "Needed by many Windows programs."),
            (".NET 10 Desktop Runtime", "Microsoft.DotNet.DesktopRuntime.10", "Needed by .NET desktop apps."),
            ("Java Runtime (Temurin 21)", "EclipseAdoptium.Temurin.21.JRE", "Runs Java programs."),
        }),
    };

    public static readonly App[] Apps = Table
        .SelectMany((group, order) => group.Apps.Select(a => new App(group.Category, order, a.Name, a.Id, a.Description)))
        .ToArray();

    public sealed record BuiltIn(string Name, string Note);

    /// <summary>Friendly names and notes for the Store apps Windows ships, by package name.</summary>
    public static readonly Dictionary<string, BuiltIn> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.MicrosoftSolitaireCollection"] = new("Solitaire & Casual Games", "Card games with ads. Nothing else needs it."),
        ["Microsoft.BingNews"] = new("News", "News feed from Bing. Nothing else needs it."),
        ["Microsoft.BingWeather"] = new("Weather", "Forecasts from Bing. Nothing else needs it."),
        ["Microsoft.BingFinance"] = new("Money", "Finance news from Bing. Nothing else needs it."),
        ["Microsoft.BingSports"] = new("Sports", "Sports news from Bing. Nothing else needs it."),
        ["Microsoft.BingSearch"] = new("Bing Search", "Web results in Windows Search."),
        ["Clipchamp.Clipchamp"] = new("Microsoft Clipchamp", "Video editor. Nothing else needs it."),
        ["Microsoft.Getstarted"] = new("Tips", "Windows tips and how-tos. Nothing else needs it."),
        ["Microsoft.GetHelp"] = new("Get Help", "Microsoft's support app. Some Help links open it."),
        ["Microsoft.WindowsFeedbackHub"] = new("Feedback Hub", "Sends feedback to Microsoft."),
        ["Microsoft.MicrosoftOfficeHub"] = new("Microsoft 365 Copilot", "Starts Office apps. Installed Office keeps working without it."),
        ["Microsoft.Copilot"] = new("Copilot", "Microsoft's AI assistant."),
        ["Microsoft.MicrosoftStickyNotes"] = new("Sticky Notes", "Desktop notes. Notes that haven't synced are lost."),
        ["Microsoft.Todos"] = new("Microsoft To Do", "Task lists."),
        ["Microsoft.PowerAutomateDesktop"] = new("Power Automate", "Automates desktop tasks."),
        ["Microsoft.ZuneMusic"] = new("Media Player", "Windows' music and video player."),
        ["Microsoft.ZuneVideo"] = new("Movies & TV", "Plays videos and Store purchases."),
        ["Microsoft.Windows.Photos"] = new("Photos", "The default image viewer. Pick another first."),
        ["Microsoft.WindowsCamera"] = new("Camera", "Takes photos and video with your webcam."),
        ["Microsoft.WindowsAlarms"] = new("Clock", "Alarms, timers and focus sessions."),
        ["Microsoft.WindowsCalculator"] = new("Calculator", "Windows' calculator."),
        ["Microsoft.WindowsNotepad"] = new("Notepad", "Windows' text editor."),
        ["Microsoft.Paint"] = new("Paint", "Simple image editor."),
        ["Microsoft.ScreenSketch"] = new("Snipping Tool", "Screenshots. Win+Shift+S stops working without it."),
        ["Microsoft.WindowsSoundRecorder"] = new("Sound Recorder", "Records audio."),
        ["Microsoft.YourPhone"] = new("Phone Link", "Connects your phone to this PC."),
        ["Microsoft.GamingApp"] = new("Xbox", "Game Pass and the Xbox store."),
        ["Microsoft.XboxGamingOverlay"] = new("Game Bar", "Win+G overlay and game capture."),
        ["Microsoft.Xbox.TCUI"] = new("Xbox TCUI", "Xbox sign-in screens that games use."),
        ["Microsoft.XboxIdentityProvider"] = new("Xbox Identity Provider", "Signs games in to Xbox. Some games need it."),
        ["Microsoft.XboxSpeechToTextOverlay"] = new("Xbox Speech to Text", "Speech to text in game chat."),
        ["Microsoft.OutlookForWindows"] = new("Outlook (new)", "Microsoft's new mail app."),
        ["MSTeams"] = new("Microsoft Teams", "Chat and video meetings."),
        ["MicrosoftCorporationII.QuickAssist"] = new("Quick Assist", "Lets someone help you remotely."),
        ["MicrosoftCorporationII.MicrosoftFamily"] = new("Microsoft Family", "Family safety settings."),
        ["Microsoft.Windows.DevHome"] = new("Dev Home", "Dashboard for developers."),
        ["Microsoft.WindowsMaps"] = new("Maps", "Maps and directions."),
        ["Microsoft.People"] = new("People", "Contacts."),
        ["microsoft.windowscommunicationsapps"] = new("Mail and Calendar", "The older mail and calendar apps."),
        ["Microsoft.MixedReality.Portal"] = new("Mixed Reality Portal", "For Windows Mixed Reality headsets."),
        ["Microsoft.Microsoft3DViewer"] = new("3D Viewer", "Views 3D models."),
        ["Microsoft.SkypeApp"] = new("Skype", "Skype has been retired."),
        ["Microsoft.HEIFImageExtension"] = new("HEIF Image Extensions", "Opens HEIC photos from phones. Apps lose this format without it."),
        ["Microsoft.HEVCVideoExtension"] = new("HEVC Video Extensions", "Plays HEVC video. Apps lose this format without it."),
        ["Microsoft.AV1VideoExtension"] = new("AV1 Video Extension", "Plays AV1 video. Apps lose this format without it."),
        ["Microsoft.AVCEncoderVideoExtension"] = new("AVC Encoder Video Extension", "Encodes H.264 video. Apps lose this format without it."),
        ["Microsoft.MPEG2VideoExtension"] = new("MPEG-2 Video Extension", "Plays MPEG-2 video. Apps lose this format without it."),
        ["Microsoft.VP9VideoExtensions"] = new("VP9 Video Extensions", "Plays VP9 video. Apps lose this format without it."),
        ["Microsoft.WebMediaExtensions"] = new("Web Media Extensions", "Plays Ogg, Vorbis and Theora. Apps lose these formats without it."),
        ["Microsoft.WebpImageExtension"] = new("WebP Image Extensions", "Opens WebP images. Apps lose this format without it."),
        ["Microsoft.RawImageExtension"] = new("Raw Image Extension", "Opens camera raw photos. Apps lose this format without it."),
        ["MicrosoftWindows.Client.WebExperience"] = new("Widgets", "The widgets board."),
        ["MicrosoftWindows.CrossDevice"] = new("Cross Device Experience Host", "Connects this PC to your phone and other devices."),
    };

    /// <summary>What "Select common extras" ticks: apps nothing else depends on.</summary>
    public static readonly HashSet<string> CommonExtras = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.MicrosoftSolitaireCollection", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingFinance", "Microsoft.BingSports",
        "Clipchamp.Clipchamp", "Microsoft.Getstarted",
    };

    static readonly Dictionary<string, string> ProtectedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.WindowsStore"] = "The Microsoft Store. You need it to get removed apps back.",
        ["Microsoft.StorePurchaseApp"] = "Handles Microsoft Store purchases.",
        ["Microsoft.Services.Store.Engagement"] = "Used by Microsoft Store apps.",
        ["Microsoft.DesktopAppInstaller"] = "This is winget itself.",
        ["Microsoft.Winget.Source"] = "Winget's app list.",
        ["Microsoft.SecHealthUI"] = "Windows Security.",
        ["Microsoft.WindowsTerminal"] = "Windows' default terminal.",
        ["Microsoft.GamingServices"] = "Xbox games and the Store need it.",
        ["Microsoft.ApplicationCompatibilityEnhancements"] = "Helps older apps run.",
        ["Microsoft.StartExperiencesApp"] = "Part of the Start menu.",
        ["Microsoft.WidgetsPlatformRuntime"] = "Windows' widgets need it.",
        ["Microsoft.MicrosoftEdge.Stable"] = "Windows and other apps rely on Edge's web components.",
        ["Microsoft.VisualStudioCode"] = "Part of a desktop app. Use Installed apps to remove it.",
        ["Microsoft.OneDriveSync"] = "OneDrive isn't removed here. Use Installed apps.",
        ["Microsoft.OneDrive"] = "OneDrive isn't removed here. Use Installed apps.",
    };

    static readonly string[] ProtectedPrefixes =
    {
        "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.WindowsAppRuntime", "Microsoft.WinAppRuntime", "Microsoft.DirectX",
        "Microsoft.Ink.Handwriting", "Microsoft.LanguageExperiencePack", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "MicrosoftWindows.", "Windows.",
    };

    /// <summary>Why a package must stay, or null when it's safe to offer removal.</summary>
    public static string? ProtectedReason(string name, bool framework, bool nonRemovable, string signature, string location)
    {
        if (ProtectedApps.TryGetValue(name, out var reason)) return reason;
        if (framework) return "Shared code that other apps need.";
        if (nonRemovable || signature == "System" || ProtectedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || location.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return "Part of Windows.";
        return null;
    }

    /// <summary>A readable name for a package with no entry above and no Start menu tile: "Microsoft.Windows.CloudExperienceHost" becomes "Cloud Experience Host".</summary>
    public static string PrettyName(string package)
    {
        if (Guid.TryParse(package, out _)) return "Windows component";
        var name = package;
        foreach (var prefix in new[] { "MicrosoftWindows.", "Microsoft.Windows.", "Microsoft.", "Windows." })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        // Store publishers often get a code as the first part, as in "38985CA0.BO6Game".
        if (char.IsDigit(name[0]) && name.IndexOf('.') is > 0 and var dot) name = name[(dot + 1)..];
        return System.Text.RegularExpressions.Regex.Replace(name.Replace('.', ' '), @"(?<=[a-z0-9])(?=[A-Z])", " ");
    }
}
