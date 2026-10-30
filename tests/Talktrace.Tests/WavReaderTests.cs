using System.Text;
using Talktrace.Core.Audio;

namespace Talktrace.Tests;

public class WavReaderTests
{
    [Fact]
    public void Downmixes_16_bit_stereo_to_mono()
    {
        short[] interleaved = [16384, 0, -16384, -16384];
        using MemoryStream wav = Pcm16Wav(interleaved, channels: 2, sampleRate: 48000);

        AudioClip clip = WavReader.Read(wav);

        Assert.Equal(48000, clip.SampleRate);
        Assert.Equal(new[] { 0.25f, -0.5f }, clip.Samples);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_wav()
    {
        using var notWav = new MemoryStream(Encoding.ASCII.GetBytes("ID3 this is an mp3 file"));

        Assert.Throws<InvalidDataException>(() => WavReader.Read(notWav));
    }

    private static MemoryStream Pcm16Wav(short[] samples, short channels, int sampleRate)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (short sample in samples)
                writer.Write(sample);
        }

        stream.Position = 0;
        return stream;
    }
}
