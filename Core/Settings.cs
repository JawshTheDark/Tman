using System.Text.Json;

namespace Tman.Core;

public sealed class Settings
{
    public int IntervalMs { get; set; } = 1000;
    public bool Topmost { get; set; }
    public bool NavCollapsed { get; set; }
    public string? LastPage { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }
    public List<string> HiddenProcessColumns { get; set; } = new() { "Threads", "Handles", "Priority", "Command line" };
    /// <summary>The user's choice for the Network column. Until they make one, it follows whether it can show data, which takes admin.</summary>
    public bool? ShowNetworkColumn { get; set; }

    // System tray: the metrics chosen for the icon, in this order.
    public bool TrayEnabled { get; set; } = true;
    /// <summary>"combined" draws one icon with a bar per metric, and a flyout on hover; "separate" draws an icon per metric.</summary>
    public string TrayLayout { get; set; } = "combined";
    public List<string> TrayItems { get; set; } = new() { "cpu", "mem", "gpu" };
    /// <summary>"number" draws the value; "meter" draws an LED bar.</summary>
    public string TrayStyle { get; set; } = "number";
    /// <summary>Closing the window hides Tman to the tray instead of exiting.</summary>
    public bool KeepInTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

    /// <summary>The Portainer-format lists behind Quick deploy on the Docker page. Earlier lists win when two describe the same thing.</summary>
    public List<string> DockerTemplateUrls { get; set; } = DockerTemplates.DefaultUrls.ToList();

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "settings.json");

    public static Settings Current { get; private set; } = Load();

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
