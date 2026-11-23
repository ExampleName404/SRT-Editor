using System.IO;
using NAudio.Wave;

namespace SrtEditor;

// Playback and waveform generation read independently from the same decoded samples.
internal sealed class OggPcmBuffer : IDisposable
{
    private readonly object gate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileStream? pcm;
    private WaveFormat format = null!;
    private long length;
    private long written;
    private Exception? failure;
    private bool finished;
    private bool closed;

    public static async Task<OggPcmBuffer> OpenAsync(string path, CancellationToken cancellation)
    {
        var buffer = new OggPcmBuffer();
        _ = Task.Run(() => buffer.Decode(path));
        try
        {
            await buffer.ready.Task.WaitAsync(cancellation).ConfigureAwait(false);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public Reader OpenReader() => new(this);

    private void Decode(string path)
    {
        try
        {
            OggAudio.Decode(path, Start, Append);
            if (written < length)
                throw new InvalidDataException("Звуковая дорожка OGG оборвана.");
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (!closed)
                {
                    failure = exception;
                    ready.TrySetException(exception);
                }
            }
        }
        finally
        {
            lock (gate)
            {
                finished = true;
                Monitor.PulseAll(gate);
            }
        }
    }

    private void Start(WaveFormat waveFormat, long frames)
    {
        if (frames <= 0)
            throw new InvalidDataException("Файл OGG не содержит звуковых данных.");

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            format = waveFormat;
            length = checked(frames * waveFormat.BlockAlign);
            string directory = Path.Combine(Path.GetTempPath(), "SrtEditor", "audio");
            Directory.CreateDirectory(directory);
            pcm = new FileStream(Path.Combine(directory, $"{Guid.NewGuid():N}.pcm"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        }
    }

    private void Append(ReadOnlySpan<byte> samples)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            int count = (int)Math.Min(samples.Length, length - written);
            RandomAccess.Write(pcm!.SafeFileHandle, samples[..count], written);
            written += count;
            if (written >= Math.Min(length, format.AverageBytesPerSecond / 4))
                ready.TrySetResult();
            Monitor.PulseAll(gate);
        }
    }

    private int Read(long position, Span<byte> destination, CancellationToken cancellation)
    {
        int count = (int)Math.Min(length - position, destination.Length);
        lock (gate)
        {
            while (!closed && !finished && !cancellation.IsCancellationRequested && written - position < count)
                Monitor.Wait(gate, 30);

            if (closed || cancellation.IsCancellationRequested || count == 0)
                return 0;
            if (written - position < count && failure is { } error)
                throw new InvalidDataException(error.Message, error);

            return RandomAccess.Read(pcm!.SafeFileHandle, destination[..count], position);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            closed = true;
            pcm?.Dispose();
            Monitor.PulseAll(gate);
        }
    }

    internal sealed class Reader(OggPcmBuffer source) : WaveStream
    {
        private long position;
        public CancellationToken Cancellation { get; set; }
        public override WaveFormat WaveFormat => source.format;
        public override long Length => source.length;
        public override long Position
        {
            get => position;
            set
            {
                long target = Math.Clamp(value, 0, Length);
                position = target - target % WaveFormat.BlockAlign;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = source.Read(position, buffer, Cancellation);
            position += read;
            return read;
        }
    }
}
