using System.Diagnostics;
using NAudio.CoreAudioApi;
using Talktrace.Core.Logging;
using Talktrace.Core.Recordings;

namespace Talktrace.Recording;

/// <summary>What to record, and where. For a call, system audio is everything the PC plays except Talktrace.</summary>
/// <param name="MicrophoneId">A specific microphone, or null to follow the Windows default.</param>
public sealed record RecordingRequest(RecordingMode Mode, string? MicrophoneId, string Folder);

/// <summary>Why a recording stopped without being asked to.</summary>
public enum StopReason
{
    DiskFull,
    SizeLimit,
    WriteFailed,
}

/// <summary>
/// Records a meeting (DESIGN.md section 9): the microphone, and for a call everything the PC plays, paired by the
/// <see cref="AlignedWriter"/> into one WAV with its sidecar. Capture and writing run on their own threads; the app
/// process hosts it, never the worker. A microphone that changes or disappears mid-meeting is followed, and a stream
/// that stops is reopened: never a reason to stop.
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    /// <summary>Free space at which a recording stops and saves cleanly rather than failing mid-write (section 3).</summary>
    public const long StopBelowBytes = 200L * 1024 * 1024;
    /// <summary>Free space below which the recording screen warns (section 9).</summary>
    public const long WarnBelowBytes = 1024L * 1024 * 1024;

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiskCheckEvery = TimeSpan.FromSeconds(10);
    private const int StalledAfterMs = 2000;

    private readonly RecordingRequest _request;
    private readonly AlignedWriter _writer;
    private readonly RecordingWavFile _file;
    private RecordingSidecar _sidecar;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _devicesLock = new();
    private string? _microphoneId;
    private AudioInput? _microphone;
    private AudioInput? _loopback;
    private KeepAwake? _keepAwake;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private volatile bool _microphoneChanged;
    private volatile bool _systemAudioStopped;
    private bool _warnedLowDisk;
    private int _deviceChanges;
    private StopReason? _stoppedBy;

    private MeetingRecorder(RecordingRequest request, string path)
    {
        _request = request;
        _microphoneId = request.MicrophoneId;
        Path = path;
        _writer = new AlignedWriter(IsCall);
        _file = new RecordingWavFile(path, IsCall ? 2 : 1);
        _sidecar = new RecordingSidecar
        {
            Mode = request.Mode,
            Status = RecordingStatus.Recording,
            StartedAt = DateTimeOffset.Now,
            MicrophoneChannel = 0,
            SystemChannel = IsCall ? 1 : null,
        };
        _thread = new Thread(Run) { IsBackground = true, Name = "Talktrace recorder", Priority = ThreadPriority.AboveNormal };
    }

    public string Path { get; }
    public RecordingMode Mode => _request.Mode;
    public bool IsCall => _request.Mode is RecordingMode.CallHeadphones or RecordingMode.CallSpeakers;

    /// <summary>Recorded time, not counting pauses.</summary>
    public TimeSpan Recorded => _file.Duration;

    public bool IsPaused => _writer.Paused;

    /// <summary>The microphone in use now; it changes when Windows' default does.</summary>
    public AudioDevice? Microphone => _microphone?.Device;

    /// <summary>A note for the recording screen: a device changed, low disk space, phone-quality audio. Raised on the recorder's thread.</summary>
    public event Action<string>? Warning;

    /// <summary>Raised on the recorder's thread when the recording stopped and saved by itself, with the reason in plain language.</summary>
    public event Action<StopReason, string>? StoppedByItself;

    /// <summary>
    /// Starts recording into a new file in <see cref="RecordingRequest.Folder"/>. Throws <see cref="RecordingException"/>
    /// in plain language if a device can't be opened; nothing is left behind then.
    /// </summary>
    public static MeetingRecorder Start(RecordingRequest request)
    {
        string path;
        MeetingRecorder recorder;
        try
        {
            Directory.CreateDirectory(request.Folder);
            path = NewRecordingPath(request.Folder, DateTime.Now);
            Log.Redact(path);
            recorder = new MeetingRecorder(request, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RecordingException("Talktrace can't save recordings in that folder. Choose another under Save recordings in.", e);
        }

        recorder._thread.Start();
        try
        {
            recorder._started.Task.GetAwaiter().GetResult();
            return recorder;
        }
        catch
        {
            recorder._stop.Set();
            recorder._thread.Join();
            recorder._file.Dispose();
            TryDelete(path);
            TryDelete(RecordingSidecar.PathFor(path));
            throw;
        }
    }

    /// <summary><c>Recording 2026-10-29 1405.wav</c>, with " (2)" and so on if that name is taken.</summary>
    public static string NewRecordingPath(string folder, DateTime now)
    {
        string name = $"Recording {now:yyyy-MM-dd HHmm}";
        string path = System.IO.Path.Combine(folder, name + ".wav");
        for (int n = 2; File.Exists(path) || File.Exists(RecordingSidecar.PathFor(path)); n++)
            path = System.IO.Path.Combine(folder, $"{name} ({n}).wav");
        return path;
    }

    public void Pause() => _writer.Paused = true;

    public void Resume() => _writer.Paused = false;

    /// <summary>The loudest sample on each input since the last call, from 0 to 1, for the level meters.</summary>
    public (float Microphone, float System) TakeLevels()
    {
        lock (_devicesLock)
            return (_microphone?.TakePeak() ?? 0, _loopback?.TakePeak() ?? 0);
    }

    /// <summary>Stops, writes the last audio and marks the recording complete. Safe to call more than once.</summary>
    public void Stop()
    {
        _stop.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join();
    }

    public void Dispose() => Stop();

    private void Run()
    {
        try
        {
            _keepAwake = new KeepAwake("Talktrace is recording a meeting.");
            if (IsCall)
                OpenSystemAudio();
            OpenMicrophone();
            _sidecar = _sidecar with { Microphone = _microphone!.Device.Name, Playback = IsCall ? AudioDevices.DefaultPlayback()?.Name : null };
            _sidecar.Write(Path);
            ListenForDeviceChanges();
            Log.Info($"Recording started: {_request.Mode}; microphone runs at {AudioDevices.MixSampleRate(_microphone!.Device.Id)} Hz.");
        }
        catch (Exception e)
        {
            CloseDevices();
            _started.TrySetException(e is RecordingException ? e : new RecordingException("Talktrace couldn't start recording.", e));
            return;
        }
        _started.TrySetResult();
        WarnIfPhoneQuality();

        var checkpoint = Stopwatch.StartNew();
        var diskCheck = Stopwatch.StartNew();
        CheckDisk();
        while (!_stop.Wait(Tick))
        {
            if (!WriteAvailable())
                break;
            if (checkpoint.Elapsed >= CheckpointEvery)
            {
                if (!Checkpoint())
                    break;
                checkpoint.Restart();
            }
            if (diskCheck.Elapsed >= DiskCheckEvery)
            {
                if (!CheckDisk())
                    break;
                diskCheck.Restart();
            }
            FollowDevices();
        }

        Finish();
    }

    private bool WriteAvailable()
    {
        try
        {
            _file.Write(_writer.Take());
            if (_file.DataBytes < RecordingWavFile.MaxDataBytes)
                return true;
            StopBy(StopReason.SizeLimit, "The recording reached the longest a recording can be (about 18 hours), so it was stopped and saved.");
        }
        catch (IOException e)
        {
            Log.Error("Writing the recording failed.", e);
            StopBy(StopReason.WriteFailed, "Talktrace couldn't keep writing the recording, so it stopped. What was recorded until then is saved.");
        }
        return false;
    }

    private bool Checkpoint()
    {
        try
        {
            _file.Checkpoint();
            return true;
        }
        catch (IOException e)
        {
            Log.Error("Saving the recording's progress failed.", e);
            StopBy(StopReason.WriteFailed, "Talktrace couldn't keep writing the recording, so it stopped. What was recorded until then is saved.");
            return false;
        }
    }

    /// <summary>Warns below 1 GB free and stops below 200 MB; false once stopped.</summary>
    private bool CheckDisk()
    {
        long? free = FreeSpace(_request.Folder);
        if (free is null)
            return true;
        if (free < StopBelowBytes)
        {
            StopBy(StopReason.DiskFull, "The disk is almost full, so the recording was stopped and saved.");
            return false;
        }
        if (free < WarnBelowBytes && !_warnedLowDisk)
        {
            _warnedLowDisk = true;
            Warning?.Invoke("Less than 1 GB of disk space is left. The recording will stop by itself if the disk fills up.");
        }
        return true;
    }

    /// <summary>Free space on the drive holding <paramref name="folder"/>, or null if it can't be read.</summary>
    public static long? FreeSpace(string folder)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void StopBy(StopReason reason, string message)
    {
        _stoppedBy = reason;
        Log.Warning($"Recording stopped by itself: {reason}.");
        StoppedByItself?.Invoke(reason, message);
    }

    private void Finish()
    {
        try
        {
            if (_stoppedBy is null)
                _file.Write(_writer.Take());
        }
        catch (IOException e)
        {
            Log.Error("Writing the end of the recording failed.", e);
        }
        CloseDevices();

        try
        {
            _file.Dispose();
            (_sidecar with { Status = RecordingStatus.Complete }).Write(Path);
        }
        catch (IOException e)
        {
            Log.Error("Finishing the recording's file failed; it can be recovered at the next launch.", e);
        }
        Log.Info($"Recording stopped: {Recorded.TotalMinutes:0.0} minutes; {_deviceChanges} device changes; " +
                 $"aligned writer dropped {_writer.Drops}, repeated {_writer.Repeats}, resynced {_writer.Resyncs}.");
    }

    private void OpenMicrophone()
    {
        AudioDevice device = Resolve(_microphoneId, AudioDevices.Microphones, AudioDevices.DefaultMicrophone)
                             ?? throw new RecordingException("No microphone was found. Plug one in, or check it's turned on in Windows' Sound settings.");
        AudioInput input = AudioInput.OpenMicrophone(device);
        input.SamplesArrived += samples => _writer.AddMicrophone(samples);
        input.Stopped += _ => _microphoneChanged = true;
        try
        {
            input.Start();
        }
        catch
        {
            input.Dispose();
            throw;
        }
        lock (_devicesLock)
            _microphone = input;
    }

    private void OpenSystemAudio()
    {
        AudioInput input = AudioInput.OpenSystemAudio();
        input.SamplesArrived += samples => _writer.AddSystem(samples);
        input.Stopped += _ => _systemAudioStopped = true;
        try
        {
            input.Start();
        }
        catch
        {
            input.Dispose();
            throw;
        }
        lock (_devicesLock)
            _loopback = input;
    }

    /// <summary>The chosen device if it's still there, otherwise the Windows default.</summary>
    private static AudioDevice? Resolve(string? id, Func<IReadOnlyList<AudioDevice>> list, Func<AudioDevice?> fallback) =>
        id is not null && list().FirstOrDefault(d => d.Id == id) is AudioDevice chosen ? chosen : fallback();

    private void ListenForDeviceChanges()
    {
        _enumerator = new MMDeviceEnumerator();
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Capture && e.Role == Role.Communications && _microphoneId is null)
                _microphoneChanged = true;
        };
        _notifications.DeviceStateChanged += (_, e) => MicrophoneGone(e.DeviceId, e.NewState != DeviceState.Active);
        _notifications.DeviceRemoved += (_, e) => MicrophoneGone(e.DeviceId, gone: true);
    }

    private void MicrophoneGone(string? deviceId, bool gone)
    {
        if (gone && deviceId is not null && deviceId == _microphone?.Device.Id)
            _microphoneChanged = true;
    }

    /// <summary>
    /// Switches to the new microphone when the default changes or the one in use disappears, and reopens a stream that
    /// stopped delivering. Runs on the recorder's thread, never on Windows' notification thread.
    /// </summary>
    private void FollowDevices()
    {
        bool paused = _writer.Paused;
        if (!_microphoneChanged && _microphone is { MillisecondsSinceData: > StalledAfterMs } && !paused)
            _microphoneChanged = true;
        // System audio delivers continuously, silence included; a long gap means the stream died.
        if (IsCall && !_systemAudioStopped && (_loopback is null || _loopback.MillisecondsSinceData > StalledAfterMs) && !paused)
            _systemAudioStopped = true;

        if (_microphoneChanged)
            SwitchMicrophone();
        if (_systemAudioStopped && IsCall)
            ReopenSystemAudio();
    }

    private void SwitchMicrophone()
    {
        _microphoneChanged = false;
        AudioDevice? previous = _microphone?.Device;
        if (_microphoneId is not null && !AudioDevices.Microphones().Any(d => d.Id == _microphoneId))
            _microphoneId = null; // the chosen microphone has gone: follow Windows from now on

        AudioInput? old;
        lock (_devicesLock)
            (old, _microphone) = (_microphone, null);
        old?.Dispose();
        try
        {
            OpenMicrophone();
        }
        catch (RecordingException e)
        {
            Log.Warning("The microphone couldn't be reopened; trying again.", e);
            Warning?.Invoke("The microphone isn't available. Talktrace keeps trying; " + e.Message);
            _microphoneChanged = true;
            Thread.Sleep(1000);
            return;
        }
        _writer.Reset();
        _deviceChanges++;
        if (_microphone!.Device.Id != previous?.Id)
        {
            Log.Info("The recording moved to another microphone.");
            Warning?.Invoke($"The microphone changed to {_microphone.Device.Name}.");
            WarnIfPhoneQuality();
        }
    }

    private void ReopenSystemAudio()
    {
        _systemAudioStopped = false;
        AudioInput? old;
        lock (_devicesLock)
            (old, _loopback) = (_loopback, null);
        old?.Dispose();
        try
        {
            OpenSystemAudio();
        }
        catch (RecordingException e)
        {
            Log.Warning("System audio couldn't be reopened; trying again.", e);
            Warning?.Invoke("The call's audio isn't being recorded. Talktrace keeps trying; " + e.Message);
            _systemAudioStopped = true;
            Thread.Sleep(1000);
            return;
        }
        _writer.Reset();
        _deviceChanges++;
        Log.Info("System audio stopped and was reopened.");
    }

    /// <summary>Bluetooth headphones in hands-free mode can drop to 8 kHz, which costs accuracy (section 9).</summary>
    private void WarnIfPhoneQuality()
    {
        if (_microphone is not null && AudioDevices.MixSampleRate(_microphone.Device.Id) is int rate && rate < AudioInput.SampleRate)
        {
            Log.Info($"The microphone runs at {rate} Hz.");
            Warning?.Invoke(
                $"{_microphone.Device.Name} is running at phone quality ({rate / 1000.0:0.#} kHz), so the transcript may be " +
                "less accurate. Wired or USB headphones give better results.");
        }
    }

    private void CloseDevices()
    {
        _notifications?.Dispose();
        _notifications = null;
        _enumerator?.Dispose();
        _enumerator = null;
        AudioInput? microphone, loopback;
        lock (_devicesLock)
        {
            (microphone, _microphone) = (_microphone, null);
            (loopback, _loopback) = (_loopback, null);
        }
        microphone?.Dispose();
        loopback?.Dispose();
        _keepAwake?.Dispose();
        _keepAwake = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("A recording that failed to start couldn't be deleted.", e);
        }
    }
}
