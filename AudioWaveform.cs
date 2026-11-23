using System.IO;
using NAudio.Wave;

namespace SrtEditor;

public sealed class AudioWaveform
{
    internal AudioWaveform(float[][] minimum, float[][] maximum, int count, float peak, double peakExtent)
    {
        Minimum = minimum;
        Maximum = maximum;
        Count = count;
        Peak = peak;
        PeakExtent = peakExtent;
    }

    internal float[][] Minimum { get; }
    internal float[][] Maximum { get; }
    internal int Count { get; }
    internal float Peak { get; }
    // The last bucket can represent fewer frames than the others.
    internal double PeakExtent { get; }
}

internal static class AudioWaveformReader
{
    private const int MaximumPeaks = 131_072;

    public static AudioWaveform Read(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var decoder = new MediaFoundationReader(path);
        return Read(decoder, cancellation);
    }

    public static AudioWaveform Read(WaveStream decoder, CancellationToken cancellation)
    {
        var samples = decoder.ToSampleProvider();
        int channels = samples.WaveFormat.Channels;
        if (channels < 1)
            throw new InvalidDataException("Аудиофайл не содержит каналов.");

        int lanes = channels == 2 ? 2 : 1;
        var minimum = new float[lanes][];
        var maximum = new float[lanes][];
        for (int lane = 0; lane < lanes; lane++)
        {
            minimum[lane] = new float[MaximumPeaks];
            maximum[lane] = new float[MaximumPeaks];
        }

        var buffer = new float[4096 * channels];
        long expectedFrames = decoder.Length / decoder.WaveFormat.BlockAlign;
        long framesPerPeak = Math.Max(1, (long)Math.Ceiling((double)expectedFrames / MaximumPeaks));
        long framesInPeak = 0;
        int peakIndex = 0;
        int read;
        float peak = 0;
        while ((read = samples.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int offset = 0; offset + channels <= read; offset += channels)
            {
                if (peakIndex == MaximumPeaks)
                {
                    CombineAdjacentPeaks(minimum, maximum);
                    peakIndex /= 2;
                    framesPerPeak *= 2;
                }

                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = buffer[offset + channel];
                    if (!float.IsFinite(sample))
                        continue;

                    int lane = lanes == 2 ? channel : 0;
                    minimum[lane][peakIndex] = Math.Min(minimum[lane][peakIndex], sample);
                    maximum[lane][peakIndex] = Math.Max(maximum[lane][peakIndex], sample);
                    peak = Math.Max(peak, Math.Abs(sample));
                }

                if (++framesInPeak == framesPerPeak)
                {
                    framesInPeak = 0;
                    peakIndex++;
                }
            }
        }

        cancellation.ThrowIfCancellationRequested();
        int count = peakIndex + (framesInPeak > 0 ? 1 : 0);
        double peakExtent = peakIndex + (double)framesInPeak / framesPerPeak;
        return new AudioWaveform(minimum, maximum, count, peak, peakExtent);
    }

    private static void CombineAdjacentPeaks(float[][] minimum, float[][] maximum)
    {
        for (int lane = 0; lane < minimum.Length; lane++)
        {
            for (int index = 0; index < MaximumPeaks / 2; index++)
            {
                minimum[lane][index] = Math.Min(minimum[lane][2 * index], minimum[lane][2 * index + 1]);
                maximum[lane][index] = Math.Max(maximum[lane][2 * index], maximum[lane][2 * index + 1]);
            }
            Array.Clear(minimum[lane], MaximumPeaks / 2, MaximumPeaks / 2);
            Array.Clear(maximum[lane], MaximumPeaks / 2, MaximumPeaks / 2);
        }
    }
}
