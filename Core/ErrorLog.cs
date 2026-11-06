namespace Tman.Core;

/// <summary>Appends unexpected errors to %APPDATA%\Tman\errors.log, so a terse error dialog can still be diagnosed.</summary>
public static class ErrorLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tman", "errors.log");

    public static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Keep it small: start over once it passes 256 KB.
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} on .NET {Environment.Version}\n{ex}\n\n");
        }
        catch { }
    }
}
