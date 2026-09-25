using System.IO;
using System.Text.Json;
using Siphon.Core.Recording;

namespace Siphon.App;

/// <summary>User settings, stored as JSON in <c>%APPDATA%\Siphon\settings.json</c>.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Siphon", "settings.json");

    public string OutputFolder { get; set; } = RecordingNames.DefaultFolder;

    public bool AlwaysOnTop { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public string? LastFilePath { get; set; }

    /// <summary>Loads settings, falling back to defaults if the file is missing or unreadable.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save them must never break recording.
        }
    }
}
