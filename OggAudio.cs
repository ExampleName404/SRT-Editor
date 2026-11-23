using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Oggfile;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SrtEditor;

internal static class OggAudio
{
    private const int OpusSampleRate = 48_000;
    internal delegate void WriteSamples(ReadOnlySpan<byte> data);

    public static bool IsOggFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ogg" or ".oga" or ".opus";

    public static void Decode(string source, Action<WaveFormat, long> start, WriteSamples write)
    {
        using var input = File.OpenRead(source);
        Span<byte> page = stackalloc byte[27];
        input.ReadExactly(page);
        if (!page[..4].SequenceEqual("OggS"u8) || page[4] != 0 || page[26] == 0 || input.ReadByte() < 19)
            throw new InvalidDataException("Некорректный заголовок OGG.");

        input.Position = page.Length + page[26];
        Span<byte> header = stackalloc byte[19];
        input.ReadExactly(header);
        input.Position = 0;

        if (header[..8].SequenceEqual("OpusHead"u8))
            DecodeOpus(input, header, start, write);
        else if (header[0] == 1 && header[1..7].SequenceEqual("vorbis"u8))
            DecodeVorbis(input, start, write);
        else
            throw new InvalidDataException("Поддерживаются аудиофайлы Ogg Vorbis и Ogg Opus.");
    }

    private static void DecodeVorbis(Stream input, Action<WaveFormat, long> start, WriteSamples write)
    {
        using var decoder = new VorbisWaveReader(input, closeOnDispose: false);
        var pcm = new SampleToWaveProvider16(decoder);
        start(pcm.WaveFormat, decoder.Length / decoder.WaveFormat.BlockAlign);
        var buffer = new byte[4096 * pcm.WaveFormat.BlockAlign];
        int count;
        while ((count = pcm.Read(buffer)) > 0)
            write(buffer.AsSpan(0, count));
    }

    private static void DecodeOpus(Stream input, ReadOnlySpan<byte> header, Action<WaveFormat, long> start,
        WriteSamples write)
    {
        int channels = header[9];
        if (channels is < 1 or > 2 || header[18] != 0)
            throw new NotSupportedException("Для Ogg Opus поддерживаются моно и стерео.");

        int skipFrames = BinaryPrimitives.ReadUInt16LittleEndian(header[10..12]);
        short gainQ8 = BinaryPrimitives.ReadInt16LittleEndian(header[16..18]);
        double gain = Math.Pow(10, gainQ8 / (20.0 * 256));
        var decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, channels);
        var ogg = new OpusOggReadStream(decoder, input);
        try
        {
            // Opus granule positions include the decoder pre-skip and trim the final packet.
            long framesRemaining = ogg.GranuleCount - skipFrames;
            if (framesRemaining <= 0)
                throw new InvalidDataException(ogg.LastError ?? "Файл Ogg Opus не содержит звуковых данных.");

            start(new WaveFormat(OpusSampleRate, 16, channels), framesRemaining);
            while (framesRemaining > 0 && ogg.HasNextPacket)
            {
                var packet = ogg.DecodeNextPacket();
                if (packet is null)
                    break;

                int frames = packet.Length / channels;
                int skipped = Math.Min(frames, skipFrames);
                skipFrames -= skipped;
                int count = (int)Math.Min(framesRemaining, frames - skipped);
                var pcm = packet.AsSpan(skipped * channels, count * channels);
                if (gainQ8 != 0)
                {
                    for (int index = 0; index < pcm.Length; index++)
                        pcm[index] = (short)Math.Clamp(Math.Round(pcm[index] * gain), short.MinValue, short.MaxValue);
                }
                write(MemoryMarshal.AsBytes(pcm));
                framesRemaining -= count;
            }

            if (framesRemaining > 0)
                throw new InvalidDataException(ogg.LastError ?? "Звуковая дорожка Ogg Opus оборвана.");
        }
        finally
        {
            ogg.Close();
            (decoder as IDisposable)?.Dispose();
        }
    }
}
