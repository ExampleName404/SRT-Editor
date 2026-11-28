using System.IO;
using System.Runtime.InteropServices;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace SrtEditor;

internal readonly record struct TranscriptionProgress(string Message, int? Percent = null);

internal static class TranscriptionService
{
    private const int SampleRate = TranscriptionAudio.SampleRate;

    static TranscriptionService()
    {
        // The single-file host extracts the DLLs outside the editor's directory.
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string searchPaths)
            return;

        string runtime = $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        foreach (string directory in searchPaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, "runtimes", runtime, "whisper.dll")))
            {
                RuntimeOptions.LibraryPath = Path.Combine(directory, "whisper.dll");
                break;
            }
        }
    }

    public static Task<Cue[]> TranscribeAsync(string audioPath, string language, IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => RecognizeAsync(audioPath, language, progress, cancellationToken), cancellationToken);

    private static async Task<Cue[]> RecognizeAsync(string audioPath, string language, IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new("Открытие аудио…"));
        using var audio = await TranscriptionAudio.OpenAsync(audioPath, cancellationToken).ConfigureAwait(false);
        string model = await WhisperModel.GetPathAsync(progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("Запуск Whisper…"));
        using var factory = WhisperFactory.FromPath(model);
        const string running = "Распознавание речи…";
        long estimatedSamples = audio.DurationMilliseconds * SampleRate / 1000;
        long decodedSamples = 0;
        long chunkStart = 0;
        int chunkLength = 0;
        int lastPercent = 0;
        var segments = new List<RecognizedSegment>();
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
            .WithProgressHandler(percent =>
            {
                if (estimatedSamples > 0 && progress is not null)
                {
                    double position = chunkStart + chunkLength * (percent / 100.0);
                    int overall = (int)Math.Clamp(100 * position / estimatedSamples, 0, 99);
                    if (overall > lastPercent)
                    {
                        lastPercent = overall;
                        progress.Report(new(running, overall));
                    }
                }
            })
            .Build();

        progress?.Report(new(running, estimatedSamples > 0 ? 0 : null));
        foreach (var chunk in audio.ReadChunks(cancellationToken))
        {
            chunkStart = chunk.StartSample;
            chunkLength = chunk.Samples.Length;
            decodedSamples = chunk.ContentEndSample;
            await foreach (var segment in processor.ProcessAsync(chunk.Samples, cancellationToken).ConfigureAwait(false))
            {
                // Samples shared by adjacent chunks give the model context around the cut.
                // Each recognized fragment belongs to the chunk containing its midpoint.
                double midpoint = chunk.StartSample + (segment.Start.TotalSeconds + segment.End.TotalSeconds) * SampleRate / 2;
                if (midpoint >= chunk.ContentStartSample && midpoint < chunk.ContentEndSample)
                {
                    var offset = TimeSpan.FromTicks(chunk.StartSample * (TimeSpan.TicksPerSecond / SampleRate));
                    segments.Add(new(offset + segment.Start, offset + segment.End, segment.Text));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var draft = TranscriptionDraft.Create(segments, (long)Math.Ceiling(decodedSamples * 1000.0 / SampleRate));
        progress?.Report(new(running, 100));
        return draft;
    }
}
