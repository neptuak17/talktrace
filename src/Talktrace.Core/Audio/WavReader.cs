using System.Buffers.Binary;

namespace Talktrace.Core.Audio;

/// <summary>Mono audio in the range -1 to 1.</summary>
public sealed record AudioClip(float[] Samples, int SampleRate)
{
    public double DurationSeconds => (double)Samples.Length / SampleRate;
}

/// <summary>
/// Minimal reader for 16-bit PCM and 32-bit float WAV files, used by the smoke test.
/// Media Foundation decoding replaces it for real recordings.
/// </summary>
public static class WavReader
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    public static AudioClip Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    public static AudioClip Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (ReadTag(reader) != "RIFF")
            throw new InvalidDataException("Not a WAV file: no RIFF header.");
        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE")
            throw new InvalidDataException("Not a WAV file: no WAVE marker.");

        ushort format = 0, channels = 0, bitsPerSample = 0;
        int sampleRate = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            string tag = ReadTag(reader);
            uint size = reader.ReadUInt32();
            long next = stream.Position + size + (size % 2);

            if (tag == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadUInt32(); // bytes per second
                reader.ReadUInt16(); // block align
                bitsPerSample = reader.ReadUInt16();
                if (format == FormatExtensible && size >= 40)
                {
                    reader.ReadBytes(8); // extension size, valid bits, channel mask
                    format = reader.ReadUInt16(); // first two bytes of the sub-format GUID
                }
            }
            else if (tag == "data")
            {
                if (channels == 0 || sampleRate <= 0)
                    throw new InvalidDataException("WAV data appears before its format.");

                long available = Math.Min(size, stream.Length - stream.Position);
                byte[] data = reader.ReadBytes((int)available);
                return new AudioClip(ToMono(data, format, bitsPerSample, channels), sampleRate);
            }

            stream.Position = Math.Min(next, stream.Length);
        }

        throw new InvalidDataException("WAV file has no audio data.");
    }

    private static float[] ToMono(byte[] data, ushort format, ushort bitsPerSample, ushort channels)
    {
        int bytesPerSample = format switch
        {
            FormatPcm when bitsPerSample == 16 => 2,
            FormatFloat when bitsPerSample == 32 => 4,
            _ => throw new InvalidDataException($"Unsupported WAV encoding (format {format}, {bitsPerSample}-bit)."),
        };

        int frames = data.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                ReadOnlySpan<byte> bytes = data.AsSpan((frame * channels + channel) * bytesPerSample, bytesPerSample);
                sum += bytesPerSample == 2
                    ? BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f
                    : BinaryPrimitives.ReadSingleLittleEndian(bytes);
            }
            mono[frame] = sum / channels;
        }

        return mono;
    }

    private static string ReadTag(BinaryReader reader) =>
        System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
}
