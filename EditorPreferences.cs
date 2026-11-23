using System.IO;
using System.Text.Json;

namespace SrtEditor;

internal static class EditorPreferences
{
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "cache", "settings.json");

    public static bool ReadSplitTimeInput()
    {
        try
        {
            using var stream = File.OpenRead(SettingsPath);
            using var settings = JsonDocument.Parse(stream);
            return settings.RootElement.ValueKind == JsonValueKind.Object &&
                   settings.RootElement.TryGetProperty("splitTimeInput", out var split) && split.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void SaveSplitTimeInput(bool splitTimeInput)
    {
        string path = SettingsPath;
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        bool ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                JsonSerializer.Serialize(stream, new { splitTimeInput });
            }
            File.Move(temporary, path, overwrite: true);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
