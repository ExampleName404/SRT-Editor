using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SrtEditor;

internal sealed record FileVersion(Guid Id, DateTimeOffset RecordedAt, Cue[] Cues)
{
    [JsonIgnore]
    public string Label => $"{RecordedAt.ToLocalTime():dd.MM.yyyy  HH:mm:ss} · блоков {Cues.Length}";
}

internal sealed class VersionStore(string directory)
{
    private const string VersionSuffix = ".srt-version.json";

    public string DirectoryPath { get; } = directory;

    public IReadOnlyList<FileVersion> Read(string path)
    {
        if (!Directory.Exists(DirectoryPath))
            return [];

        var result = new List<FileVersion>();
        foreach (string entry in Directory.EnumerateFiles(DirectoryPath, FileKey(path) + "-*" + VersionSuffix))
        {
            try
            {
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var version = JsonSerializer.Deserialize<FileVersion>(stream);
                if (version is { Cues: not null } && version.Cues.All(IsValidCue) &&
                    version.Cues.Select(cue => cue.Id).Distinct().Count() == version.Cues.Length)
                    result.Add(version);
            }
            catch (JsonException)
            {
                // An unreadable cache entry shouldn't hide the other versions of the file.
            }
            catch (FileNotFoundException)
            {
                // Another window may have cleared the cache during enumeration.
            }
        }

        return result.OrderByDescending(version => version.RecordedAt)
            .ThenByDescending(version => version.Id).ToArray();
    }

    public FileVersion Record(string path, Cue[] cues, FileVersion? latest)
    {
        cues = cues.Select(SrtFile.Normalize).ToArray();
        if (latest is not null && CueComparison.HasSameContent(cues, latest.Cues))
            return latest;

        Directory.CreateDirectory(DirectoryPath);
        var version = new FileVersion(Guid.NewGuid(), DateTimeOffset.UtcNow, cues);
        string name = $"{FileKey(path)}-{version.Id:N}";
        string temporary = Path.Combine(DirectoryPath, name + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, version);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, Path.Combine(DirectoryPath, name + VersionSuffix));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return version;
    }

    public int Clear()
    {
        if (!Directory.Exists(DirectoryPath))
            return 0;

        int count = 0;
        foreach (string entry in Directory.EnumerateFiles(DirectoryPath, "*" + VersionSuffix))
        {
            File.Delete(entry);
            count++;
        }

        return count;
    }

    private static bool IsValidCue(Cue? cue) =>
        cue is { Start: not null, End: not null, Text: not null, Settings: not null } && cue.Id != Guid.Empty;

    public static bool IsSameFile(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static string FileKey(string path)
    {
        string normalized = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
