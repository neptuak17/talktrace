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

    public static AudioClip Decode(string path, CancellationToken cancellationToken = default) =>
        Read(path, (file, reader) =>
        {
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
        });

    /// <summary>
    /// Decodes a stereo file to two 16 kHz mono tracks, one per channel, for call recordings (DESIGN.md section 9):
    /// the microphone on one channel and system audio on the other.
    /// </summary>
    public static (AudioClip Left, AudioClip Right) DecodeStereo(string path, CancellationToken cancellationToken = default) =>
        Read(path, (file, reader) =>
        {
            if (reader.WaveFormat.Channels != 2)
                throw new TalktraceException($"{file.Name} isn't a two-track call recording.");

            using var resampler = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2))
            {
                ResamplerQuality = 60,
            };

            long capacity = (long)(reader.TotalTime.TotalSeconds * SampleRate) + SampleRate;
            var left = new SampleBuffer(capacity);
            var right = new SampleBuffer(capacity);
            const int FrameBytes = 2 * sizeof(float);
            byte[] bytes = new byte[SampleRate * FrameBytes];
            float[] leftBlock = new float[SampleRate];
            float[] rightBlock = new float[SampleRate];
            int carried = 0; // bytes of a partial frame left over from the last read
            int read;
            while ((read = resampler.Read(bytes.AsSpan(carried))) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int available = carried + read;
                int frames = available / FrameBytes;
                ReadOnlySpan<float> interleaved = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, frames * FrameBytes));
                for (int i = 0; i < frames; i++)
                {
                    leftBlock[i] = interleaved[2 * i];
                    rightBlock[i] = interleaved[2 * i + 1];
                }
                left.Append(leftBlock.AsSpan(0, frames));
                right.Append(rightBlock.AsSpan(0, frames));

                carried = available - frames * FrameBytes;
                bytes.AsSpan(frames * FrameBytes, carried).CopyTo(bytes);
            }

            if (left.Count == 0)
                throw new TalktraceException($"{file.Name} contains no audio.");

            return (new AudioClip(left.ToArray(), SampleRate), new AudioClip(right.ToArray(), SampleRate));
        });

    /// <summary>The recording's length from its header, without decoding it. Fails as <see cref="Decode"/> would on a file it can't read.</summary>
    public static TimeSpan ReadDuration(string path) => Read(path, (_, reader) => reader.TotalTime);

    /// <summary>How many channels the file has, from its header.</summary>
    public static int ReadChannelCount(string path) => Read(path, (_, reader) => reader.WaveFormat.Channels);

    private static T Read<T>(string path, Func<FileInfo, MediaFoundationReader, T> read)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new TalktraceException(FileErrors.Reading(new FileNotFoundException(null, path), file.Name));
        if (file.Length == 0)
            throw new TalktraceException($"{file.Name} is empty: it holds no audio.");

        try
        {
            using var reader = new MediaFoundationReader(path);
            return read(file, reader);
        }
        catch (Exception e) when (e is DllNotFoundException || e.InnerException is DllNotFoundException)
        {
            throw new TalktraceException(
                "Windows Media Foundation isn't installed, so Talktrace can't read audio files. " +
                "On Windows N editions, install the Media Feature Pack from Settings > Apps > Optional features.", e);
        }
        catch (Exception e) when ((e is COMException or IOException or UnauthorizedAccessException)
                                  && FileErrors.Describe(e, file.Name, saving: false) is not null)
        {
            // Moved, in use by another app, an online-only cloud file, a network folder that's gone
            throw new TalktraceException(FileErrors.Reading(e, file.Name), e);
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
