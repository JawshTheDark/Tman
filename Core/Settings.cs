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
