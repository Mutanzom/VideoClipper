using System.Text.Json;

namespace VideoClipper.App.Services;

/// <summary>Small JSON settings file in %LOCALAPPDATA%\VideoClipper (unpackaged apps have no ApplicationData).</summary>
public sealed class AppSettings
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoClipper");

    private static string FilePath => Path.Combine(Root, "settings.json");

    public bool Reencode { get; set; }
    public bool Mute { get; set; }
    public int QualityIndex { get; set; } = 1;
    public double? MaxSizeMb { get; set; }
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception) { /* corrupt or unreadable: start fresh */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* settings are best effort */ }
    }
}
