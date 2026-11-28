using System.IO;
using NAudio.Wave;

namespace SrtEditor.Tests;

internal static class TranscriptionAudioTests
{
    public static void Run()
    {
        MixesAndResamplesStereo();
        PreservesChunkBoundariesAndOverlap();
    }

    private static void MixesAndResamplesStereo()
    {
        string path = TemporaryWavePath();
        try
        {
            const int rate = 48_000;
            var frame = new short[rate * 2];
            for (int i = 0; i < rate; i++)
                frame[2 * i] = (short)(Math.Sin(i * 2 * Math.PI * 400 / rate) * 10_000);
            using (var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 2)))
                writer.WriteSamples(frame, 0, frame.Length);

            using var audio = TranscriptionAudio.OpenAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            var chunk = audio.ReadChunks(CancellationToken.None).Single();
            Check.True(audio.DurationMilliseconds == 1_000 && chunk.StartSample == 0 && chunk.ContentStartSample == 0,
                "The prepared recording keeps its original timeline.");
            Check.True(Math.Abs(chunk.Samples.Length - TranscriptionAudio.SampleRate) <= 2 &&
                       chunk.ContentEndSample == chunk.Samples.Length,
                "The stereo input produces one 16 kHz mono stream.");
            Check.True(chunk.Samples.All(float.IsFinite) && chunk.Samples.Max() is > 0.14f and < 0.17f,
                "The mono mix combines both channels into normalized samples.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void PreservesChunkBoundariesAndOverlap()
    {
        string path = TemporaryWavePath();
        try
        {
            const int duration = 303;
            int rate = TranscriptionAudio.SampleRate;
            var second = new short[rate];
            using (var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1)))
            {
                for (int i = 0; i < duration; i++)
                {
                    Array.Fill(second, (short)((i % 13 - 6) * 2_222));
                    writer.WriteSamples(second, 0, second.Length);
                }
            }

            using (var audio = TranscriptionAudio.OpenAsync(path, CancellationToken.None).GetAwaiter().GetResult())
            {
                var chunks = audio.ReadChunks(CancellationToken.None).ToArray();
                Check.True(chunks.Length == 2 && chunks[0].ContentStartSample == 0 &&
                           chunks[0].ContentEndSample == chunks[1].ContentStartSample &&
                           chunks[1].ContentEndSample == duration * (long)rate,
                    "Adjacent content ranges cover the recording without gaps or duplicates.");
                int overlap = (int)(chunks[0].StartSample + chunks[0].Samples.Length - chunks[1].StartSample);
                Check.True(overlap == 4 * rate &&
                           chunks[0].Samples.AsSpan(^overlap).SequenceEqual(chunks[1].Samples.AsSpan(0, overlap)),
                    "Both chunks carry the same four seconds around their boundary.");
            }

            using var canceledAudio = TranscriptionAudio.OpenAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            using var cancellation = new CancellationTokenSource();
            using var iterator = canceledAudio.ReadChunks(cancellation.Token).GetEnumerator();
            Check.True(iterator.MoveNext(), "The decoder starts before cancellation.");
            cancellation.Cancel();
            try
            {
                _ = iterator.MoveNext();
                throw new InvalidOperationException("The decoder continued after cancellation.");
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellation.Token)
            {
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryWavePath() => Path.Combine(Path.GetTempPath(), $"srt-editor-test-{Guid.NewGuid():N}.wav");
}
