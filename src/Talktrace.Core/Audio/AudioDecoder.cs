using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Talktrace.Core.Audio;

/// <summary>
/// Decodes any file Windows' Media Foundation can read (WAV, MP3, M4A, MP4, WMA and the audio
/// track of video files) to 16 kHz mono float samples.
/// </summary>
public static class AudioDecoder
{
    public const int SampleRate = 16000;

    public static AudioClip Decode(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new TalktraceException($"The file doesn't exist: {path}");
        if (file.Length == 0)
            throw new TalktraceException($"The file is empty: {file.Name}");

        try
        {
            using var reader = new MediaFoundationReader(path);
            using var resampler = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1))
            {
                ResamplerQuality = 60,
            };

            var samples = new SampleBuffer((long)(reader.TotalTime.TotalSeconds * SampleRate) + SampleRate);
            byte[] bytes = new byte[SampleRate * sizeof(float)];
            int read;
            while ((read = resampler.Read(bytes)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.Append(MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, read - read % sizeof(float))));
            }

            if (samples.Count == 0)
                throw new TalktraceException($"{file.Name} contains no audio.");

            return new AudioClip(samples.ToArray(), SampleRate);
        }
        catch (Exception e) when (e is DllNotFoundException || e.InnerException is DllNotFoundException)
        {
            throw new TalktraceException(
                "Windows Media Foundation isn't installed, so Talktrace can't read audio files. " +
                "On Windows N editions, install the Media Feature Pack from Settings > Apps > Optional features.", e);
        }
        catch (Exception e) when (e is COMException or MediaFoundationException or InvalidDataException)
        {
            throw new TalktraceException(
                $"Talktrace can't read this {DescribeType(file)}. It may be damaged, or Windows may have no decoder for it.", e);
        }
    }

    private static string DescribeType(FileInfo file) =>
        string.IsNullOrEmpty(file.Extension) ? "file" : $"{file.Extension.TrimStart('.').ToUpperInvariant()} file";

    /// <summary>A growable float array that avoids the doubling copies of a List for hour-long audio.</summary>
    private sealed class SampleBuffer(long capacity)
    {
        private float[] _data = new float[Math.Clamp(capacity, SampleRate, Array.MaxLength)];

        public int Count { get; private set; }

        public void Append(ReadOnlySpan<float> values)
        {
            if (Count + values.Length > _data.Length)
                Array.Resize(ref _data, (int)Math.Min(Array.MaxLength, Math.Max((long)_data.Length * 5 / 4, Count + values.Length)));
            values.CopyTo(_data.AsSpan(Count));
            Count += values.Length;
        }

        public float[] ToArray() => Count == _data.Length ? _data : _data[..Count];
    }
}
