using System.Text;

namespace Siphon.Core.Recording;

public static class RecordingNames
{
    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music", "Siphon");

    /// <summary>
    /// <c>Siphon_2026-09-25_14-32-07.wav</c> for system audio, <c>Siphon_Spotify_2026-09-25_14-32-07.wav</c> for an app.
    /// </summary>
    public static string FileName(string? appName, DateTime timestamp)
    {
        string stamp = timestamp.ToString("yyyy-MM-dd_HH-mm-ss");
        string app = Sanitize(appName);
        return app.Length == 0 ? $"Siphon_{stamp}.wav" : $"Siphon_{app}_{stamp}.wav";
    }

    /// <summary>Creates <paramref name="folder"/> if needed and returns a path that doesn't exist yet.</summary>
    public static string NewRecordingPath(string folder, string? appName, DateTime timestamp)
    {
        Directory.CreateDirectory(folder);
        string name = FileName(appName, timestamp);
        string path = System.IO.Path.Combine(folder, name);
        for (int i = 2; File.Exists(path); i++)
            path = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(name)}_{i}.wav");
        return path;
    }

    private static string Sanitize(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return "";

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(appName.Length);
        foreach (char c in appName.Trim())
        {
            if (Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) || c == '_')
                sb.Append('-');
            else
                sb.Append(c);
        }

        string result = sb.ToString().Trim('-', '.');
        return result.Length > 40 ? result[..40] : result;
    }
}
