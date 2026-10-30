using NAudio.MediaFoundation;
using NAudio.Wave;
using Talktrace.Core;
using Talktrace.Core.Audio;

namespace Talktrace.Tests;

/// <summary>
/// Decodes files made on the fly with Windows' own encoders, so no audio is committed.
/// Needs Media Foundation but no models.
/// </summary>
public sealed class AudioDecoderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(48000, 2)]
    [InlineData(44100, 1)]
    [InlineData(16000, 1)]
    public void Decodes_wav_to_16_kHz_mono(int sampleRate, int channels)
    {
        string wav = WriteTone("tone.wav", sampleRate, channels, seconds: 2);

        AudioClip clip = AudioDecoder.Decode(wav, TestContext.Current.CancellationToken);

        Assert.Equal(16000, clip.SampleRate);
        Assert.Equal(2.0, clip.DurationSeconds, tolerance: 0.02);
        Assert.InRange(Rms(clip.Samples), 0.2, 0.4); // a 0.4-amplitude sine has an RMS of about 0.28
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("mp4")]
    [InlineData("wma")]
    public void Decodes_compressed_formats(string extension)
    {
        string source = WriteTone("source.wav", 48000, 2, seconds: 3);
        string encoded = Path.Combine(_folder, $"tone.{extension}");
        try
        {
            using var reader = new WaveFileReader(source);
            switch (extension)
            {
                case "mp3": MediaFoundationEncoder.EncodeToMp3(reader, encoded); break;
                case "wma": MediaFoundationEncoder.EncodeToWma(reader, encoded); break;
                default: MediaFoundationEncoder.EncodeToAac(reader, encoded); break;
            }
        }
        catch (InvalidOperationException e)
        {
            Assert.Skip($"Windows has no {extension} encoder here: {e.Message}");
        }

        AudioClip clip = AudioDecoder.Decode(encoded, TestContext.Current.CancellationToken);

        Assert.Equal(3.0, clip.DurationSeconds, tolerance: 0.15); // encoders add a little priming silence
        Assert.InRange(Rms(clip.Samples), 0.15, 0.4);
    }

    [Fact]
    public void Reports_a_damaged_file_by_its_type()
    {
        string path = Path.Combine(_folder, "broken.mp3");
        var random = new Random(1);
        byte[] junk = new byte[20000];
        random.NextBytes(junk);
        File.WriteAllBytes(path, junk);

        var error = Assert.Throws<TalktraceException>(() => AudioDecoder.Decode(path, TestContext.Current.CancellationToken));

        Assert.Contains("MP3 file", error.Message);
    }

    [Fact]
    public void Reports_an_empty_file()
    {
        string path = Path.Combine(_folder, "empty.m4a");
        File.WriteAllBytes(path, []);

        var error = Assert.Throws<TalktraceException>(() => AudioDecoder.Decode(path, TestContext.Current.CancellationToken));

        Assert.Contains("empty", error.Message);
    }

    [Fact]
    public void Reads_the_duration_without_decoding()
    {
        string wav = WriteTone("tone.wav", 48000, 2, seconds: 2.5);

        Assert.Equal(2.5, AudioDecoder.ReadDuration(wav).TotalSeconds, tolerance: 0.01);
    }

    [Fact]
    public void Reading_the_duration_of_a_damaged_file_reports_its_type()
    {
        string path = Path.Combine(_folder, "broken.m4a");
        File.WriteAllBytes(path, new byte[20000]);

        var error = Assert.Throws<TalktraceException>(() => AudioDecoder.ReadDuration(path));

        Assert.Contains("M4A file", error.Message);
    }

    private string WriteTone(string name, int sampleRate, int channels, double seconds)
    {
        string path = Path.Combine(_folder, name);
        int frames = (int)(sampleRate * seconds);
        byte[] pcm = new byte[frames * channels * 2];
        for (int i = 0; i < frames; i++)
        {
            short value = (short)(0.4 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            for (int c = 0; c < channels; c++)
                BitConverter.TryWriteBytes(pcm.AsSpan((i * channels + c) * 2), value);
        }

        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
        writer.Write(pcm, 0, pcm.Length);
        return path;
    }

    private static double Rms(float[] samples) =>
        Math.Sqrt(samples.Select(s => (double)s * s).Average());
}
