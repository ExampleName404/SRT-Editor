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
    private sealed record VersionHeader(string Path, Guid Id, DateTimeOffset RecordedAt, FileVersion? Version = null);

    private const string VersionSuffix = ".srt-version.json";

    public string DirectoryPath { get; } = directory;

    public IReadOnlyList<FileVersion> Read(string path) =>
        Entries(path).Select(ReadVersion).OfType<FileVersion>()
            .OrderByDescending(version => version.RecordedAt).ThenByDescending(version => version.Id).ToArray();

    public FileVersion? ReadLatest(string path)
    {
        var ordered = Entries(path).Select(ReadHeader).OfType<VersionHeader>()
            .OrderByDescending(header => header.RecordedAt).ThenByDescending(header => header.Id);
        foreach (var entry in ordered)
        {
            var version = entry.Version ?? ReadVersion(entry.Path);
            if (version is not null)
                return version;
        }

        return null;
    }

    private IEnumerable<string> Entries(string path) => Directory.Exists(DirectoryPath)
        ? Directory.EnumerateFiles(DirectoryPath, FileKey(path) + "-*" + VersionSuffix) : [];

    private static FileVersion? ReadVersion(string entry)
    {
        try
        {
            using var stream = OpenEntry(entry);
            return ReadVersion(stream);
        }
        catch (Exception exception) when (exception is JsonException or FileNotFoundException)
        {
            return null;
        }
    }

    private static VersionHeader? ReadHeader(string entry)
    {
        try
        {
            using var stream = OpenEntry(entry);
            Span<byte> prefix = stackalloc byte[256];
            int length = stream.ReadAtLeast(prefix, prefix.Length, false);
            if (TryReadHeader(prefix[..length], out Guid id, out DateTimeOffset recordedAt))
                return new VersionHeader(entry, id, recordedAt);

            // Snapshots with a different property order are still readable.
            stream.Position = 0;
            var version = ReadVersion(stream);
            return version is null ? null : new VersionHeader(entry, version.Id, version.RecordedAt, version);
        }
        catch (Exception exception) when (exception is JsonException or FileNotFoundException)
        {
            return null;
        }
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> prefix, out Guid id, out DateTimeOffset recordedAt)
    {
        if (prefix.StartsWith("\uFEFF"u8))
            prefix = prefix[3..];

        id = default;
        recordedAt = default;
        bool hasId = false;
        bool hasRecordedAt = false;
        var reader = new Utf8JsonReader(prefix, isFinalBlock: false, state: default);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                continue;
            if (reader.ValueTextEquals("Cues"u8))
                break;
            if (reader.ValueTextEquals("Id"u8))
                hasId = reader.Read() && reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out id);
            else if (reader.ValueTextEquals("RecordedAt"u8))
                hasRecordedAt = reader.Read() && reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out recordedAt);
        }

        return hasId && hasRecordedAt;
    }

    private static FileStream OpenEntry(string entry) =>
        new(entry, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static FileVersion? ReadVersion(Stream stream)
    {
        var version = JsonSerializer.Deserialize<FileVersion>(stream);
        return version is { Cues: not null } && version.Cues.All(IsValidCue) &&
            version.Cues.Select(cue => cue.Id).Distinct().Count() == version.Cues.Length ? version : null;
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
