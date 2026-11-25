using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Talktrace.Recording;

/// <summary>Receives captured audio, already 16 kHz mono. Called on the capture thread; copy what you keep.</summary>
public delegate void SamplesHandler(ReadOnlySpan<float> samples);

/// <summary>
/// One shared-mode WASAPI capture stream, never exclusive (DESIGN.md section 9, Capture): a microphone, or what a
/// playback device plays (loopback). Windows converts it to 16 kHz mono as it arrives.
/// </summary>
public sealed class AudioInput : IDisposable
{
    public const int SampleRate = 16000;

    private readonly WasapiRecorder _recorder;
    private float _peak;
    private long _lastData = Environment.TickCount64;
    private bool _disposed;

    private AudioInput(WasapiRecorder recorder, AudioDevice device, bool loopback)
    {
        _recorder = recorder;
        Device = device;
        IsLoopback = loopback;
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            if (!_disposed)
                Stopped?.Invoke(e.Exception);
        };
    }

    public AudioDevice Device { get; }
    public bool IsLoopback { get; }

    /// <summary>Raised on the capture thread with each packet of 16 kHz mono samples.</summary>
    public event SamplesHandler? SamplesArrived;

    /// <summary>Raised when capture stops by itself: the device was unplugged, or another error. Null means no error was given.</summary>
    public event Action<Exception?>? Stopped;

    /// <summary>Milliseconds since the last packet arrived. Loopback delivers nothing while nothing plays.</summary>
    public long MillisecondsSinceData => Environment.TickCount64 - Interlocked.Read(ref _lastData);

    /// <summary>What <see cref="OpenSystemAudio"/> records, as a device for messages.</summary>
    public static readonly AudioDevice SystemAudio = new("system-audio", "your PC's sound", IsHeadphones: false);

    /// <summary>Opens a capture stream on a microphone. Throws <see cref="RecordingException"/> in plain language if it can't.</summary>
    public static AudioInput OpenMicrophone(AudioDevice device)
    {
        MMDevice? endpoint = null;
        WasapiRecorder? recorder = null;
        try
        {
            endpoint = AudioDevices.Open(device.Id);
            recorder = new WasapiRecorderBuilder()
                .WithDevice(endpoint)
                .WithSharedMode()
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1))
                .Build();
            return new AudioInput(recorder, device, loopback: false);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            recorder?.Dispose();
            throw RecordingException.FromDevice(e, device, loopback: false);
        }
        finally
        {
            endpoint?.Dispose();
        }
    }

    /// <summary>
    /// Opens a stream of everything the PC plays except Talktrace's own sound, whichever device it plays on (Windows'
    /// process loopback, excluding this process tree). Unlike a playback device's loopback, it keeps delivering while
    /// nothing plays, and it opens while another app has the microphone; on the Snapdragon laptop, a device's loopback
    /// fails to start once any microphone stream is open (DESIGN.md section 9). Blocks while Windows activates it, so
    /// call it off the UI thread.
    /// </summary>
    public static AudioInput OpenSystemAudio()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new RecordingException("Recording a call needs Windows 10 version 2004 or later.");
        WasapiRecorder? recorder = null;
        try
        {
            recorder = new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1))
                .BuildAsync().GetAwaiter().GetResult();
            return new AudioInput(recorder, SystemAudio, loopback: true);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            recorder?.Dispose();
            throw RecordingException.FromDevice(e, SystemAudio, loopback: true);
        }
    }

    public void Start()
    {
        try
        {
            _recorder.StartRecording();
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            throw RecordingException.FromDevice(e, Device, IsLoopback);
        }
        if (_recorder.WaveFormat is not { SampleRate: SampleRate } format)
            throw new RecordingException($"Windows couldn't convert {Device.Name} to 16 kHz.");
        Format = format;
    }

    private WaveFormat? Format { get; set; }

    /// <summary>The loudest sample since the last call, from 0 to 1, for a level meter.</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0);

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        Interlocked.Exchange(ref _lastData, Environment.TickCount64);
        WaveFormat? format = Format ?? _recorder.WaveFormat;
        if (format is null || buffer.Length == 0)
            return;

        int frames = buffer.Length / format.BlockAlign;
        float[] rented = ArrayPool<float>.Shared.Rent(frames);
        try
        {
            Span<float> mono = rented.AsSpan(0, frames);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
                mono.Clear();
            else
                ToMono(buffer, format, mono);

            float peak = 0;
            foreach (float sample in mono)
                peak = Math.Max(peak, Math.Abs(sample));
            if (peak > _peak)
                Interlocked.Exchange(ref _peak, peak);

            SamplesArrived?.Invoke(mono);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>Mixes each frame's channels to one float sample; Windows normally delivers float mono already.</summary>
    private static void ToMono(ReadOnlySpan<byte> buffer, WaveFormat format, Span<float> mono)
    {
        int channels = format.Channels;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32;
        if (isFloat && format.BitsPerSample == 32)
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer[..(mono.Length * format.BlockAlign)]);
            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += samples[i * channels + c];
                mono[i] = sum / channels;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(buffer[..(mono.Length * format.BlockAlign)]);
            for (int i = 0; i < mono.Length; i++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++)
                    sum += samples[i * channels + c];
                mono[i] = sum / (32768f * channels);
            }
        }
        else
        {
            mono.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            // already stopped, or the device has gone
        }
        _recorder.Dispose();
    }
}
