using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talktrace.Core;
using Talktrace.Core.Logging;
using Talktrace.Core.Recordings;
using Talktrace.Core.Settings;
using Talktrace.Core.Transcripts;
using Talktrace.Recording;

namespace Talktrace.App;

/// <summary>An entry in a device dropdown: a device, or "Windows default (…)" which follows Windows.</summary>
public sealed record DeviceChoice(string Label, AudioDevice? Device)
{
    public bool IsDefault => Device is null;

    public override string ToString() => Label;
}

/// <summary>
/// The Record a meeting tab (DESIGN.md section 9, Recording screen; section 3, Milestone 7). Recording runs in this
/// process through <see cref="MeetingRecorder"/>; when it stops, the recording opens on the file tab.
/// </summary>
public sealed partial class RecordViewModel : ObservableObject, IDisposable
{
    private const string MicrophoneMutedNote = "Your microphone is muted in Windows, so your voice won't be recorded.";

    private readonly MainViewModel _owner;
    private readonly IDialogs _dialogs;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _clockTimer;
    private DeviceWatcher? _watcher;
    private LevelMonitor? _monitor;
    private MeetingRecorder? _recorder;
    private bool _isVisible;
    private bool _isMinimized;
    private bool _refreshingDevices;
    private string? _lastPlaybackId;
    private int _monitorGeneration;
    private string? _stoppedByItself;

    public RecordViewModel(MainViewModel owner, IDialogs dialogs, SettingsStore settings)
    {
        _owner = owner;
        _dialogs = dialogs;
        _settings = settings;
        UserSettings saved = settings.Current;
        YourName = saved.YourName ?? TranscribeOptions.DefaultYourName;
        TranscribeWhenStopped = saved.TranscribeWhenStopped ?? true;
        KeepRecordings = saved.KeepRecordings ?? true;
        RecordingsFolder = saved.RecordingsFolder ?? UserSettings.DefaultRecordingsFolder;
        ElapsedText = "0:00";
        FreeSpaceText = "";

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clockTimer.Tick += (_, _) => UpdateClock();
    }

    // Mode

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInPerson), nameof(IsCallHeadphones), nameof(IsCallSpeakers), nameof(IsCall))]
    public partial RecordingMode Mode { get; set; } = RecordingMode.CallSpeakers;

    public bool IsInPerson { get => Mode == RecordingMode.InPerson; set { if (value) Mode = RecordingMode.InPerson; } }
    public bool IsCallHeadphones { get => Mode == RecordingMode.CallHeadphones; set { if (value) Mode = RecordingMode.CallHeadphones; } }
    public bool IsCallSpeakers { get => Mode == RecordingMode.CallSpeakers; set { if (value) Mode = RecordingMode.CallSpeakers; } }
    public bool IsCall => Mode != RecordingMode.InPerson;

    partial void OnModeChanged(RecordingMode value) => RestartMonitor();

    // Devices

    public ObservableCollection<DeviceChoice> Microphones { get; } = [];

    [ObservableProperty]
    public partial DeviceChoice? SelectedMicrophone { get; set; }

    partial void OnSelectedMicrophoneChanged(DeviceChoice? value)
    {
        if (_refreshingDevices)
            return;
        RefreshMicrophoneNote();
        RestartMonitor();
    }


    /// <summary>From 0 to 1 on a decibel scale, for the level meters.</summary>
    [ObservableProperty]
    public partial double MicrophoneLevel { get; private set; }

    [ObservableProperty]
    public partial double SystemLevel { get; private set; }

    /// <summary>Something about the microphone to know before starting: muted, phone quality, or can't be opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMicrophoneNote))]
    public partial string? MicrophoneNote { get; private set; }

    public bool HasMicrophoneNote => MicrophoneNote is not null;

    // Recording

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsRecordingNotPaused))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(ResumeCommand), nameof(StopCommand), nameof(ChangeFolderCommand))]
    public partial bool IsRecording { get; private set; }

    public bool IsIdle => !IsRecording && !IsStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsStarting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingNotPaused))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand), nameof(ResumeCommand))]
    public partial bool IsPaused { get; private set; }

    public bool IsRecordingNotPaused => IsRecording && !IsPaused;

    /// <summary>Recorded time, not counting pauses.</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; private set; }

    /// <summary>Device changes, low disk space and the like, newest first.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial string FreeSpaceText { get; private set; }

    [ObservableProperty]
    public partial bool IsLowOnSpace { get; private set; }

    // Settings

    [ObservableProperty]
    public partial string YourName { get; set; }

    [ObservableProperty]
    public partial bool TranscribeWhenStopped { get; set; }

    [ObservableProperty]
    public partial bool KeepRecordings { get; set; }

    [ObservableProperty]
    public partial string RecordingsFolder { get; private set; }

    /// <summary>The name for call transcripts: Your name, or "Me" if it's empty.</summary>
    public string EffectiveYourName => string.IsNullOrWhiteSpace(YourName) ? TranscribeOptions.DefaultYourName : YourName.Trim();

    partial void OnYourNameChanged(string value) => _settings.Update(s => s with { YourName = EffectiveYourName });

    partial void OnTranscribeWhenStoppedChanged(bool value) => _settings.Update(s => s with { TranscribeWhenStopped = value });

    partial void OnKeepRecordingsChanged(bool value) => _settings.Update(s => s with { KeepRecordings = value });

    /// <summary>Raised when recording starts, stops, pauses or resumes, or its time changes: the window's title and taskbar follow it.</summary>
    public event Action? RecordingStateChanged;

    /// <summary>The tab is showing and the window isn't minimised: the level meters run only then, or while recording (section 3).</summary>
    public void SetVisibility(bool tabShown, bool minimized)
    {
        bool wasVisible = _isVisible && !_isMinimized;
        _isVisible = tabShown;
        _isMinimized = minimized;
        bool visible = _isVisible && !_isMinimized;
        if (visible == wasVisible)
            return;
        if (visible)
        {
            _watcher ??= CreateWatcher();
            RefreshDevices();
            RefreshFreeSpace();
            RestartMonitor();
            _meterTimer.Start();
        }
        else
        {
            StopMonitor();
            if (!IsRecording)
                _meterTimer.Stop();
            MicrophoneLevel = SystemLevel = 0;
        }
    }

    private bool CanStart => IsIdle;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_owner.IsRunning)
        {
            if (!_dialogs.ConfirmCancelJobToRecord())
                return;
            await _owner.CancelJobAsync(); // the recording always wins (section 6)
        }

        ErrorMessage = null;
        Warnings.Clear();
        _stoppedByItself = null;
        StopMonitor();
        IsStarting = true;
        var request = new RecordingRequest(Mode, SelectedMicrophone?.Device?.Id, RecordingsFolder);
        try
        {
            MeetingRecorder recorder = await Task.Run(() => MeetingRecorder.Start(request));
            recorder.Warning += message => Post(() => AddWarning(message));
            recorder.StoppedByItself += (_, message) => Post(() => OnStoppedByItself(message));
            _recorder = recorder;
        }
        catch (RecordingException e)
        {
            ErrorMessage = e.Message;
            Log.Warning($"Recording couldn't start: {e.Message}", e.InnerException);
            IsStarting = false;
            RestartMonitor();
            return;
        }

        RememberMode();
        IsStarting = false;
        IsRecording = true;
        IsPaused = false;
        _meterTimer.Start();
        _clockTimer.Start();
        UpdateClock();
        if (_recorder.Microphone is AudioDevice mic && AudioDevices.IsMuted(mic.Id) == true)
            AddWarning(MicrophoneMutedNote);
        _owner.OnRecordingStarted();
    }

    private bool CanPause => IsRecording && !IsPaused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _recorder?.Pause();
        IsPaused = true;
        Log.Info("Recording paused.");
        RecordingStateChanged?.Invoke();
    }

    private bool CanResume => IsRecording && IsPaused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _recorder?.Resume();
        IsPaused = false;
        Log.Info("Recording resumed.");
        RecordingStateChanged?.Invoke();
    }

    private bool CanStop => IsRecording;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => StopRecordingAsync(openAfterwards: true);

    /// <summary>Stops and saves. With <paramref name="openAfterwards"/>, the recording then opens on the file tab.</summary>
    public async Task StopRecordingAsync(bool openAfterwards)
    {
        if (_recorder is not MeetingRecorder recorder)
            return;
        _recorder = null;
        _clockTimer.Stop();
        await Task.Run(recorder.Stop);
        IsRecording = false;
        IsPaused = false;
        MicrophoneLevel = SystemLevel = 0;
        if (!_isVisible || _isMinimized)
            _meterTimer.Stop();
        RestartMonitor();
        RefreshFreeSpace();
        RecordingStateChanged?.Invoke();

        if (openAfterwards)
            await _owner.OnRecordingFinishedAsync(recorder.Path, transcribe: TranscribeWhenStopped && _stoppedByItself is null,
                switchTab: _stoppedByItself is null);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ChangeFolder()
    {
        if (_dialogs.ChooseFolder(RecordingsFolder) is not string folder)
            return;
        RecordingsFolder = folder;
        _settings.Update(s => s with { RecordingsFolder = folder });
        RefreshFreeSpace();
    }

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingsFolder);
            MainViewModel.ShowInExplorer($"\"{RecordingsFolder}\"");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Talktrace couldn't open the recordings folder: {e.Message}";
        }
    }

    public string WindowTitleSuffix => !IsRecording ? "" : $"{(IsPaused ? "Paused" : "Recording")} {ElapsedText}";

    public void Dispose()
    {
        _meterTimer.Stop();
        _clockTimer.Stop();
        StopMonitor();
        _watcher?.Dispose();
        _recorder?.Stop();
    }

    // Devices

    private DeviceWatcher? CreateWatcher()
    {
        try
        {
            var watcher = new DeviceWatcher();
            watcher.Changed += () =>
            {
                if (_isVisible)
                    RefreshDevices();
            };
            return watcher;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException)
        {
            Log.Warning("Device changes can't be followed on the recording screen.", e);
            return null;
        }
    }

    /// <summary>Lists the devices, keeping the current choices where they still exist.</summary>
    private void RefreshDevices()
    {
        string? micId = SelectedMicrophone?.Device?.Id;
        string? playbackId = AudioDevices.DefaultPlayback()?.Id;
        bool firstTime = Microphones.Count == 0;
        _refreshingDevices = true;
        try
        {
            Fill(Microphones, AudioDevices.DefaultMicrophone(), AudioDevices.Microphones());
            SelectedMicrophone = Microphones.FirstOrDefault(c => c.Device?.Id == micId) ?? Microphones.FirstOrDefault();
        }
        finally
        {
            _refreshingDevices = false;
        }

        RefreshMicrophoneNote();
        if (!IsRecording)
        {
            // Plugging in headphones or the speakerphone changes Windows' default playback device, and with it the mode.
            if (firstTime || playbackId != _lastPlaybackId)
                PreselectMode();
            RestartMonitor();
        }
        _lastPlaybackId = playbackId;
    }

    private static void Fill(ObservableCollection<DeviceChoice> choices, AudioDevice? fallback, IReadOnlyList<AudioDevice> devices)
    {
        choices.Clear();
        choices.Add(new DeviceChoice(fallback is null ? "Windows default (none)" : $"Windows default ({fallback.Name})", null));
        foreach (AudioDevice device in devices)
            choices.Add(new DeviceChoice(device.Name, device));
    }

    private AudioDevice? ChosenMicrophone => SelectedMicrophone?.Device ?? AudioDevices.DefaultMicrophone();

    /// <summary>The mode last used with Windows' default playback device, or a guess from its type (section 9).</summary>
    private void PreselectMode()
    {
        if (IsRecording || AudioDevices.DefaultPlayback() is not AudioDevice playback)
            return;
        if (_settings.Current.ModeByPlayback?.GetValueOrDefault(playback.Id) is string saved
            && Enum.TryParse(saved, out RecordingMode remembered))
            Mode = remembered;
        else
            Mode = playback.IsHeadphones ? RecordingMode.CallHeadphones : RecordingMode.CallSpeakers;
    }

    private void RememberMode()
    {
        if (AudioDevices.DefaultPlayback()?.Id is not string deviceId)
            return;
        _settings.Update(s =>
        {
            var modes = new Dictionary<string, string>(s.ModeByPlayback ?? []) { [deviceId] = Mode.ToString() };
            return s with { ModeByPlayback = modes };
        });
    }

    private void RefreshMicrophoneNote()
    {
        if (ChosenMicrophone is not AudioDevice mic)
        {
            MicrophoneNote = "No microphone was found. Plug one in, or check it's turned on in Windows' Sound settings.";
            return;
        }
        if (AudioDevices.IsMuted(mic.Id) == true)
            MicrophoneNote = MicrophoneMutedNote;
        else if (AudioDevices.MixSampleRate(mic.Id) is int rate && rate < AudioInput.SampleRate)
            MicrophoneNote = $"{mic.Name} is running at phone quality ({rate / 1000.0:0.#} kHz), so the transcript may be less accurate. " +
                             "Wired or USB headphones give better results.";
        else
            MicrophoneNote = _monitor?.Problem;
    }

    private async void RestartMonitor()
    {
        StopMonitor();
        int generation = _monitorGeneration;
        if (IsRecording || IsStarting || !_isVisible || _isMinimized)
            return;
        LevelMonitor monitor = await LevelMonitor.OpenAsync(ChosenMicrophone, IsCall);
        if (generation != _monitorGeneration)
        {
            monitor.Dispose(); // replaced or stopped while it opened
            return;
        }
        _monitor = monitor;
        if (monitor.Problem is not null && MicrophoneNote is null)
            MicrophoneNote = monitor.Problem;
    }

    private void StopMonitor()
    {
        _monitorGeneration++;
        _monitor?.Dispose();
        _monitor = null;
    }

    private void UpdateMeters()
    {
        (float mic, float system) = _recorder?.TakeLevels() ?? _monitor?.TakeLevels() ?? (0, 0);
        MicrophoneLevel = Smooth(MicrophoneLevel, mic);
        SystemLevel = Smooth(SystemLevel, system);
    }

    /// <summary>Rises at once, falls gently; −60 dB to 0 dB fills the meter.</summary>
    private static double Smooth(double shown, float peak)
    {
        double level = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
        return Math.Max(level, shown * 0.85);
    }

    private void UpdateClock()
    {
        if (_recorder is null)
            return;
        ElapsedText = TranscriptFormatter.FormatDuration(_recorder.Recorded);
        RecordingStateChanged?.Invoke();
    }

    private void RefreshFreeSpace()
    {
        long? free = MeetingRecorder.FreeSpace(RecordingsFolder);
        FreeSpaceText = free is long bytes ? $"Free space: {MainViewModel.FormatSize(bytes)}" : "";
        IsLowOnSpace = free < MeetingRecorder.WarnBelowBytes;
    }

    private void AddWarning(string message)
    {
        Warnings.Remove(message);
        Warnings.Insert(0, message);
        while (Warnings.Count > 4)
            Warnings.RemoveAt(Warnings.Count - 1);
        if (message.StartsWith("Less than 1 GB", StringComparison.Ordinal))
            RefreshFreeSpace();
    }

    private async void OnStoppedByItself(string message)
    {
        _stoppedByItself = message;
        ErrorMessage = message;
        await StopRecordingAsync(openAfterwards: true);
    }

    private static void Post(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}
