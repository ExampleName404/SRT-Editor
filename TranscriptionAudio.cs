using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SrtEditor;

internal readonly record struct TranscriptionAudioChunk(float[] Samples, long StartSample, long ContentStartSample,
    long ContentEndSample);

internal sealed class TranscriptionAudio : IDisposable
{
    public const int SampleRate = 16_000;
    private const int ContentSamples = 5 * 60 * SampleRate;
    private const int OverlapSamples = 2 * SampleRate;
    private const int ReadBlockSamples = 16_384;

    private readonly WaveStream reader;
    private readonly OggPcmBuffer? ogg;
    private readonly ISampleProvider samples;
    private bool started;
    private bool disposed;

    private TranscriptionAudio(WaveStream reader, OggPcmBuffer? ogg)
    {
        this.reader = reader;
        this.ogg = ogg;
        DurationMilliseconds = Math.Max(0, (long)Math.Round(reader.TotalTime.TotalMilliseconds));

        ISampleProvider source = reader.ToSampleProvider();
        int channels = source.WaveFormat.Channels;
        if (channels < 1)
            throw new InvalidDataException("Аудиофайл не содержит каналов.");
        if (channels > 1)
        {
            var mix = new float[channels, 1];
            for (int channel = 0; channel < channels; channel++)
                mix[channel, 0] = 1f / channels;
            source = new ChannelMixerSampleProvider(source, mix);
        }

        samples = source.WaveFormat.SampleRate == SampleRate ? source : new WdlResamplingSampleProvider(source, SampleRate);
    }

    public long DurationMilliseconds { get; }

    public static async Task<TranscriptionAudio> OpenAsync(string audioPath, CancellationToken cancellationToken)
    {
        WaveStream? reader = null;
        OggPcmBuffer? buffer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OggAudio.IsOggFile(audioPath))
            {
                buffer = await OggPcmBuffer.OpenAsync(audioPath, cancellationToken).ConfigureAwait(false);
                reader = buffer.OpenReader();
            }
            else
            {
                reader = await Task.Run(() => new MediaFoundationReader(audioPath), cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new TranscriptionAudio(reader, buffer);
        }
        catch
        {
            try { reader?.Dispose(); }
            finally { buffer?.Dispose(); }
            throw;
        }
    }

    public IEnumerable<TranscriptionAudioChunk> ReadChunks(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (started)
            throw new InvalidOperationException("Аудио для распознавания уже прочитано.");
        started = true;
        if (reader is OggPcmBuffer.Reader oggReader)
            oggReader.Cancellation = cancellationToken;

        int initialCapacity = (int)Math.Clamp(DurationMilliseconds * (SampleRate / 1_000L), ReadBlockSamples,
            ContentSamples + 2 * OverlapSamples);
        var buffer = new float[initialCapacity];
        int leading = 0;
        int pending = 0;
        long contentStart = 0;
        while (true)
        {
            int count = Fill(ref buffer, leading + pending, leading + ContentSamples + OverlapSamples, cancellationToken);
            int contentLength = Math.Min(ContentSamples, count - leading);
            if (contentLength == 0)
                yield break;

            long contentEnd = contentStart + contentLength;
            yield return new TranscriptionAudioChunk(buffer.AsSpan(0, count).ToArray(), contentStart - leading,
                contentStart, contentEnd);

            cancellationToken.ThrowIfCancellationRequested();
            pending = count - leading - contentLength;
            if (pending == 0)
                yield break;

            // The last core samples and the following samples form the next chunk's prefix and initial content.
            int prefix = Math.Min(contentLength, OverlapSamples);
            Array.Copy(buffer, leading + contentLength - prefix, buffer, 0, prefix + pending);
            leading = prefix;
            contentStart = contentEnd;
        }
    }

    private int Fill(ref float[] buffer, int count, int limit, CancellationToken cancellationToken)
    {
        while (count < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = Math.Min(limit - count, ReadBlockSamples);
            if (count + requested > buffer.Length)
                Array.Resize(ref buffer, Math.Min(limit, Math.Max(count + requested, buffer.Length * 2)));

            int read = samples.Read(buffer.AsSpan(count, requested));
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0)
                break;

            int end = count + read;
            for (int i = count; i < end; i++)
                buffer[i] = float.IsFinite(buffer[i]) ? Math.Clamp(buffer[i], -1f, 1f) : 0;
            count = end;
        }

        return count;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try { reader.Dispose(); }
        finally { ogg?.Dispose(); }
    }
}
