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

    // System tray: one live icon per metric, in this order.
    public bool TrayEnabled { get; set; } = true;
    public List<string> TrayItems { get; set; } = new() { "cpu", "mem", "gpu" };
    /// <summary>"number" draws the value; "meter" draws an LED bar.</summary>
    public string TrayStyle { get; set; } = "number";
    /// <summary>Closing the window hides Tman to the tray instead of exiting.</summary>
    public bool KeepInTray { get; set; } = true;
    public bool TrayHintShown { get; set; }

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
