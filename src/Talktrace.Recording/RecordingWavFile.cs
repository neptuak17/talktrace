using System.Buffers.Binary;
using System.Text;

namespace Talktrace.Recording;

/// <summary>
/// A 16 kHz, 16-bit PCM WAV written as the recording runs (DESIGN.md section 9, Files): mono for In person, stereo for
/// the call modes with the microphone on the left. It is written to disk continuously and its header refreshed at each
/// <see cref="Checkpoint"/>, so a crash or power loss leaves a playable file, and <see cref="Recover"/> fixes the
/// header of one that was cut off.
/// </summary>
public sealed class RecordingWavFile : IDisposable
{
    public const int SampleRate = 16000;
    private const int HeaderBytes = 44;

    /// <summary>The most audio one WAV can hold: its sizes are 32-bit. Stereo reaches it after about 18 hours.</summary>
    public const long MaxDataBytes = uint.MaxValue - HeaderBytes - 1024 * 1024;

    private readonly FileStream _file;
    private readonly int _channels;
    private byte[] _scratch = [];

    /// <summary>Creates the file; fails if it already exists.</summary>
    public RecordingWavFile(string path, int channels)
    {
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
        _file.Write(Header(channels, dataBytes: 0));
    }

    public long DataBytes { get; private set; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)DataBytes / (SampleRate * 2 * _channels));

    /// <summary>Appends <paramref name="frames"/>, interleaving the microphone (left) and system audio (right) for a call.</summary>
    public void Write(AlignedFrames frames)
    {
        int count = frames.Length;
        if (count == 0)
            return;
        if (frames.System is not null && _channels != 2 || frames.System is null && _channels != 1)
            throw new InvalidOperationException("The frames don't match the file's channels.");

        int bytes = count * 2 * _channels;
        if (_scratch.Length < bytes)
            _scratch = new byte[bytes];
        Span<byte> output = _scratch.AsSpan(0, bytes);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(output[(i * 2 * _channels)..], ToPcm(frames.Microphone[i]));
            if (frames.System is float[] system)
                BinaryPrimitives.WriteInt16LittleEndian(output[(i * 4 + 2)..], ToPcm(system[i]));
        }

        _file.Write(output);
        _file.Flush(flushToDisk: false); // to Windows at once, so a crash of Talktrace loses nothing written
        DataBytes += bytes;
    }

    /// <summary>Refreshes the header's sizes and pushes everything written so far to the disk.</summary>
    public void Checkpoint()
    {
        long position = _file.Position;
        _file.Position = 0;
        _file.Write(Header(_channels, DataBytes));
        _file.Position = position;
        _file.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        try
        {
            Checkpoint();
        }
        finally
        {
            _file.Dispose();
        }
    }

    /// <summary>
    /// Fixes the header of a WAV that was cut off, from the file's size, dropping any half-written sample at the end.
    /// Returns its duration.
    /// </summary>
    public static TimeSpan Recover(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> header = stackalloc byte[HeaderBytes];
        if (file.Length < HeaderBytes || file.Read(header) < HeaderBytes
            || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8) || !header[36..40].SequenceEqual("data"u8))
            throw new InvalidDataException("This isn't a recording Talktrace made.");

        int channels = BinaryPrimitives.ReadInt16LittleEndian(header[22..]);
        int blockAlign = BinaryPrimitives.ReadInt16LittleEndian(header[32..]);
        if (channels is not (1 or 2) || blockAlign != 2 * channels)
            throw new InvalidDataException("This isn't a recording Talktrace made.");

        long dataBytes = Math.Min(MaxDataBytes, file.Length - HeaderBytes) / blockAlign * blockAlign;
        file.SetLength(HeaderBytes + dataBytes);
        file.Position = 0;
        file.Write(Header(channels, dataBytes));
        file.Flush(flushToDisk: true);
        return TimeSpan.FromSeconds((double)dataBytes / (SampleRate * blockAlign));
    }

    /// <summary>How long a recording is, from its size alone: what a cut-off file holds before <see cref="Recover"/>.</summary>
    public static TimeSpan DurationFromSize(long fileBytes, int channels) =>
        TimeSpan.FromSeconds((double)Math.Max(0, fileBytes - HeaderBytes) / (SampleRate * 2 * channels));

    private static short ToPcm(float sample) => (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);

    private static byte[] Header(int channels, long dataBytes)
    {
        var header = new byte[HeaderBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);           // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);            // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), SampleRate * 2 * channels); // bytes per second
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), (short)(2 * channels));     // block align
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);           // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataBytes);
        return header;
    }
}
