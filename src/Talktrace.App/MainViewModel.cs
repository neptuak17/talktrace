using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.MediaFoundation;
using Talktrace.Core;
using Talktrace.Core.Audio;
using Talktrace.Core.Logging;
using Talktrace.Core.Models;
using Talktrace.Core.Recordings;
using Talktrace.Core.Settings;
using Talktrace.Core.Transcripts;
using Talktrace.Core.Worker;
using Talktrace.Recording;

namespace Talktrace.App;

/// <summary>
/// The Transcribe a file screen (DESIGN.md section 10). One recording and one job at a time; the job runs in the
/// worker process, which auto-saves the transcript before it reports the result.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TranscribeOptions Defaults = new();

    /// <summary>The longest sample the speakers panel plays.</summary>
    private const double SampleSeconds = 8;

    private readonly IDialogs _dialogs;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _clockTimer;
    private readonly Stopwatch _jobClock = new();
    private readonly SamplePlayer _player = new();
    private readonly IReadOnlyList<SpeakerChoice> _fileSpeakerChoices;
    private readonly IReadOnlyList<SpeakerChoice> _callSpeakerChoices;
    private SpeakerChoice _fileSpeakers;
    private SpeakerChoice _callSpeakers;
    private CancellationTokenSource? _jobCancel;
    private TranscribeProgress? _progress;
    private (TimeSpan At, int Done)? _firstChunk;
    private TimeSpan _lastChunkAt;
    private string? _transcribedRecording;
    private string? _errorDetails;
    private bool _settingTranscript;
    private int _openRequests;
    private SpeakerRow? _playingRow;

    public MainViewModel(IDialogs dialogs, string settingsPath)
    {
        _dialogs = dialogs;
        _settings = new SettingsStore(settingsPath);
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateTimeText();
        _player.Finished += OnSampleFinished;

        _fileSpeakerChoices =
        [
            new("Don't label speakers", SpeakerSetting.None),
            new("Auto-detect", SpeakerSetting.Auto),
            .. Enumerable.Range(2, 7).Select(n => new SpeakerChoice(n.ToString(CultureInfo.CurrentCulture), SpeakerSetting.Exactly(n))),
        ];
        // For call recordings the count is of the other participants, and 1 skips speaker identification (section 9).
        _callSpeakerChoices =
        [
            new("Don't label speakers", SpeakerSetting.None),
            new("Auto-detect", SpeakerSetting.Auto),
            new("1 other participant", SpeakerSetting.Exactly(1)),
            .. Enumerable.Range(2, 7).Select(n => new SpeakerChoice($"{n} other participants", SpeakerSetting.Exactly(n))),
        ];
        _fileSpeakers = _fileSpeakerChoices[1];
        _callSpeakers = _callSpeakerChoices[1];
        SpeakerChoices = _fileSpeakerChoices;
        // Multilingual was dropped from v1 (DESIGN.md section 3); the Model dropdown shows once there are two profiles.
        Profiles = new[] { ModelProfile.Fast }.Select(p => new ProfileChoice(p, ModelLocator.Find(p.AsrDirectory) is not null)).ToList();
        SelectedProfile = Profiles.FirstOrDefault(p => p.IsInstalled) ?? Profiles[0];
        SelectedSpeakers = _fileSpeakers;
        ResetAdvanced();

        ApplySettings(_settings.Current);
        Record = new RecordViewModel(this, dialogs, _settings);
        Record.RecordingStateChanged += () => OnPropertyChanged(nameof(WindowTitle));
        SelectedTab = _settings.Current.LastTab == RecordTabName ? RecordTab : FileTab;

        RecordingTitle = "Drop a recording here";
        RecordingDetails = "or choose one: WAV, MP3, M4A, MP4 or WMA";
        StageText = "";
        TimeText = "";
        TranscriptText = "";
        StatusText = "";
    }

    /// <summary>Raised just before a rename rewrites the transcript, so the view can keep its scroll position.</summary>
    public event Action? TranscriptRewriting;

    /// <summary>Where the window was when Talktrace last closed, or null.</summary>
    public WindowPlacement? SavedWindowPlacement => _settings.Current.Window;

    // Tabs (DESIGN.md section 10): the window opens on the one used last.

    public const int FileTab = 0;
    public const int RecordTab = 1;
    private const string RecordTabName = "record";

    /// <summary>The Record a meeting tab.</summary>
    public RecordViewModel Record { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    partial void OnSelectedTabChanged(int value) =>
        _settings.Update(s => s with { LastTab = value == RecordTab ? RecordTabName : "file" });

    /// <summary>"Recording 12:34 – Talktrace" while recording (section 3), otherwise "Talktrace".</summary>
    public string WindowTitle => Record.IsRecording ? $"{Record.WindowTitleSuffix} – Talktrace" : "Talktrace";

    // The recording

    /// <summary>The chosen recording is a call Talktrace recorded, so it's transcribed as two tracks.</summary>
    [ObservableProperty]
    public partial bool IsCallRecording { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    public partial string? RecordingPath { get; private set; }

    [ObservableProperty]
    public partial string RecordingTitle { get; private set; }

    [ObservableProperty]
    public partial string RecordingDetails { get; private set; }

    // Options

    /// <summary>The Speakers choices: counts of everyone for ordinary files, of the other participants for call recordings.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SpeakerChoice> SpeakerChoices { get; private set; }

    public IReadOnlyList<ProfileChoice> Profiles { get; }

    /// <summary>One choice is no choice: the Model dropdown is hidden until there are two profiles.</summary>
    public bool ShowModelChoice => Profiles.Count > 1;

    [ObservableProperty]
    public partial SpeakerChoice SelectedSpeakers { get; set; }

    [ObservableProperty]
    public partial ProfileChoice SelectedProfile { get; set; }

    [ObservableProperty]
    public partial bool IncludeTimestamps { get; set; }

    [ObservableProperty]
    public partial string ClusteringThreshold { get; set; }

    [ObservableProperty]
    public partial string Threads { get; set; }

    [ObservableProperty]
    public partial string MaxChunkSeconds { get; set; }

    [ObservableProperty]
    public partial string MaxSpeechSeconds { get; set; }

    /// <summary>Names the models the chosen options need that aren't installed, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelsMessage))]
    public partial string? ModelsMessage { get; private set; }

    public bool HasModelsMessage => ModelsMessage is not null;

    // The job

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsTranscriptReadOnly), nameof(CanCloseNow))]
    [NotifyCanExecuteChangedFor(
        nameof(ChooseRecordingCommand), nameof(TranscribeCommand), nameof(CancelCommand), nameof(SaveCommand),
        nameof(SaveAsCommand), nameof(CopyAllCommand), nameof(OpenFolderCommand))]
    public partial bool IsRunning { get; private set; }

    public bool IsIdle => !IsRunning;

    /// <summary>Reading audio, Identifying speakers, Transcribing 42 of 310; afterwards, how it ended.</summary>
    [ObservableProperty]
    public partial string StageText { get; private set; }

    /// <summary>Elapsed time, and once transcription starts, the time left.</summary>
    [ObservableProperty]
    public partial string TimeText { get; private set; }

    /// <summary>Progress within the current stage, from 0 to 1.</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    /// <summary>For stages that report nothing as they go.</summary>
    [ObservableProperty]
    public partial bool ProgressIsIndeterminate { get; private set; }

    // The transcript

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyAllCommand))]
    public partial string TranscriptText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranscriptReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand), nameof(CopyAllCommand))]
    public partial bool HasTranscript { get; private set; }

    public bool IsTranscriptReadOnly => IsRunning || !HasTranscript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCloseNow))]
    public partial bool HasUnsavedEdits { get; private set; }

    /// <summary>Where the transcript on screen was last saved: first the worker's auto-save, then wherever Save as put it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(OpenFolderCommand))]
    public partial string? SavedPath { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; }

    // The speakers panel

    /// <summary>One row per speaker in order of first appearance; empty when the transcript has no speaker labels.</summary>
    public ObservableCollection<SpeakerRow> Speakers { get; } = [];

    [ObservableProperty]
    public partial bool HasSpeakers { get; private set; }

    // Errors

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial bool HasErrorDetails { get; private set; }

    partial void OnSelectedProfileChanged(ProfileChoice value) => RefreshModelsMessage();

    partial void OnSelectedSpeakersChanged(SpeakerChoice value)
    {
        if (value is null)
            return; // the list is being swapped
        if (IsCallRecording)
            _callSpeakers = value;
        else
            _fileSpeakers = value;
        RefreshModelsMessage();
    }

    /// <summary>Shows the Speakers choices for the kind of recording chosen, each kind keeping its own selection.</summary>
    private void ShowSpeakerChoicesFor(bool call)
    {
        SpeakerChoice selected = call ? _callSpeakers : _fileSpeakers;
        IsCallRecording = call;
        SpeakerChoices = call ? _callSpeakerChoices : _fileSpeakerChoices;
        SelectedSpeakers = selected;
    }

    partial void OnTranscriptTextChanged(string value)
    {
        if (_settingTranscript || !HasTranscript)
            return;
        HasUnsavedEdits = true;
        StatusText = "Unsaved changes";
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseRecordingAsync()
    {
        if (_dialogs.ChooseRecording(RecordingPath) is string path)
            await OpenRecordingAsync(path);
    }

    /// <summary>Selects a recording that was chosen or dropped, once Talktrace has checked it can read it.</summary>
    public async Task OpenRecordingAsync(string path)
    {
        if (IsRunning)
            return;
        int request = ++_openRequests;
        ClearError();
        Log.Redact(path);
        try
        {
            (TimeSpan duration, RecordingMode? call) = await Task.Run(() =>
            {
                TimeSpan length = AudioDecoder.ReadDuration(path);
                bool isCall = RecordingSidecar.TryRead(path) is { IsCall: true } && AudioDecoder.ReadChannelCount(path) == 2;
                return (length, isCall ? RecordingSidecar.TryRead(path)?.Mode : null);
            });
            if (request != _openRequests || IsRunning)
                return; // replaced by a later choice
            string fullPath = Path.GetFullPath(path);
            long size = new FileInfo(fullPath).Length;
            RecordingPath = fullPath;
            RecordingTitle = Path.GetFileName(fullPath);
            string kind = call switch
            {
                RecordingMode.CallHeadphones => "Online call, headphones · ",
                RecordingMode.CallSpeakers => "Online call, speakers · ",
                _ => "",
            };
            RecordingDetails = $"{TranscriptFormatter.FormatDuration(duration)} · {FormatSize(size)} · {kind}" + Path.GetDirectoryName(fullPath);
            ShowSpeakerChoicesFor(call is not null);
            Log.Info($"Recording chosen: {DescribeType(fullPath)} file, {FormatSize(size)}, {TranscriptFormatter.FormatDuration(duration)}.");
        }
        catch (Exception e) when (e is TalktraceException or IOException or UnauthorizedAccessException)
        {
            if (request == _openRequests)
                ShowError(e.Message, e.InnerException?.ToString() ?? e.ToString());
        }
    }

    /// <summary>No transcribing while recording (section 6).</summary>
    private bool CanTranscribe => RecordingPath is not null && !IsRunning && !Record.IsRecording && !Record.IsStarting;

    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeAsync()
    {
        if (RecordingPath is not string recording || !ConfirmDiscardingEdits())
            return;

        ClearError();
        if (ReadOptions() is not TranscribeOptions options)
            return;
        ModelsMessage = MissingModelsMessage(options);
        if (ModelsMessage is not null)
            return;
        SaveOptions(options);

        using var cancel = new CancellationTokenSource();
        _jobCancel = cancel;
        StopSample();
        ShowTranscript(null);
        ShowSpeakers(null);
        SavedPath = null;
        StatusText = "";
        _progress = null;
        _firstChunk = null;
        StageText = "Starting";
        ProgressIsIndeterminate = true;
        ProgressValue = 0;
        IsRunning = true;
        _jobClock.Restart();
        _clockTimer.Start();
        UpdateTimeText();

        try
        {
            TranscriptResult result = await WorkerClient.RunAsync(
                recording, options, new Progress<TranscribeProgress>(OnProgress), cancel.Token);
            _transcribedRecording = recording;
            ShowTranscript(TranscriptFormatter.Format(result, options.IncludeTimestamps));
            ShowSpeakers(result);
            SavedPath = result.SavedPath;
            StageText = $"Done in {TranscriptFormatter.FormatDuration(_jobClock.Elapsed)}";
            Log.Info($"Transcription done in {TranscriptFormatter.FormatDuration(_jobClock.Elapsed)}.");
            if (result.SavedPath is string saved)
            {
                StatusText = $"Saved as {Path.GetFileName(saved)}";
                RecycleRecordingIfNotKept(recording);
            }
            else
            {
                // The transcript exists only on screen, so leaving it asks first, as unsaved edits do.
                HasUnsavedEdits = true;
                StatusText = "Not saved";
                ShowError($"{result.SaveError} The transcript hasn't been saved: use Save as… to save it somewhere else.", null);
            }
        }
        catch (OperationCanceledException)
        {
            StageText = "Cancelled";
            Log.Info("Transcription cancelled.");
        }
        catch (WorkerException e)
        {
            StageText = "";
            ShowError(e.Message, e.Details);
        }
        catch (Exception e)
        {
            StageText = "";
            ShowError("Transcription failed because of an unexpected error.", e.ToString());
        }
        finally
        {
            _jobCancel = null;
            _jobClock.Stop();
            _clockTimer.Stop();
            TimeText = "";
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        if (_jobCancel is null || _jobCancel.IsCancellationRequested)
            return;
        StageText = "Cancelling";
        ProgressIsIndeterminate = true;
        _jobCancel.Cancel();
    }

    private bool CanUseTranscript => HasTranscript && !IsRunning;

    /// <summary>
    /// Saves edits over the file last saved, at first the auto-saved transcript. A transcript the worker couldn't save
    /// goes through Save as instead.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseTranscript))]
    private void Save() => SaveTranscript();

    [RelayCommand(CanExecute = nameof(CanUseTranscript))]
    private void SaveAs() => SaveTranscriptAs();

    private bool SaveTranscript() => SavedPath is string path ? WriteTranscript(path) : SaveTranscriptAs();

    private bool SaveTranscriptAs() =>
        _dialogs.ChooseSavePath(TranscriptFile.DefaultPath(_transcribedRecording!)) is string path && WriteTranscript(path);

    [RelayCommand(CanExecute = nameof(CanUseTranscript))]
    private void CopyAll()
    {
        if (CopyToClipboard(TranscriptText))
            StatusText = "Copied the transcript";
    }

    private bool CanOpenFolder => SavedPath is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder() => ShowInExplorer($"/select,\"{SavedPath}\"");

    [RelayCommand]
    private void OpenModelsFolder() => ShowFolder(ModelLocator.UserFolder, "models");

    [RelayCommand]
    private void OpenLogFolder() => ShowFolder(Log.Folder, "log");

    private void ShowFolder(string folder, string what)
    {
        try
        {
            Directory.CreateDirectory(folder);
            ShowInExplorer($"\"{folder}\"");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError($"Talktrace couldn't open the {what} folder: {e.Message}", e.ToString());
        }
    }

    [RelayCommand]
    private void CopyErrorDetails()
    {
        string text = $"{ErrorMessage}{Environment.NewLine}{Environment.NewLine}{_errorDetails}";
        if (CopyToClipboard(text))
            StatusText = "Copied the error details";
    }

    [RelayCommand]
    private void ResetAdvanced()
    {
        ClusteringThreshold = Defaults.ClusteringThreshold.ToString(CultureInfo.CurrentCulture);
        Threads = Defaults.NumThreads.ToString(CultureInfo.CurrentCulture);
        MaxChunkSeconds = Defaults.MaxChunkSeconds.ToString(CultureInfo.CurrentCulture);
        MaxSpeechSeconds = Defaults.VadMaxSpeechSeconds.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>Asks about a recording, a running job and unsaved edits. Returns true once the window may close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (Record.IsRecording)
        {
            if (!_dialogs.ConfirmStopRecordingAndClose())
                return false;
            await Record.StopRecordingAsync(openAfterwards: false);
        }
        if (IsRunning)
        {
            if (!_dialogs.ConfirmStopAndClose())
                return false;
            Cancel();
            if (TranscribeCommand.ExecutionTask is Task job)
                await job;
        }
        return ConfirmDiscardingEdits();
    }

    /// <summary>Remembers where the window was, for the next launch.</summary>
    public void SaveWindowPlacement(WindowPlacement placement) => _settings.Update(s => s with { Window = placement });

    // Recording (DESIGN.md section 9)

    /// <summary>True when closing needs no questions: nothing recording, no job running and no unsaved edits.</summary>
    public bool CanCloseNow => !IsRunning && !HasUnsavedEdits && !Record.IsRecording;

    internal void OnRecordingStarted()
    {
        TranscribeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCloseNow));
    }

    /// <summary>Cancels the running job and waits for it to end, so a recording can start.</summary>
    internal async Task CancelJobAsync()
    {
        Cancel();
        if (TranscribeCommand.ExecutionTask is Task job)
            await job;
    }

    /// <summary>
    /// Opens a recording that just stopped in the transcribe flow, with the options its mode calls for, and starts the job
    /// if Transcribe automatically when I stop is on.
    /// </summary>
    internal async Task OnRecordingFinishedAsync(string path, bool transcribe, bool switchTab)
    {
        TranscribeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCloseNow));
        if (switchTab)
            SelectedTab = FileTab;
        if (!ConfirmDiscardingEdits())
            return; // keep the transcript on screen; the recording is saved and can be chosen later
        await OpenRecordingAsync(path);
        if (transcribe && RecordingPath == Path.GetFullPath(path) && TranscribeCommand.CanExecute(null))
            await TranscribeCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// At launch: offers to recover each recording Talktrace didn't finish (section 3). Recover and transcribe opens it
    /// on the file tab and starts the job; any others are asked about at the next launch.
    /// </summary>
    public async Task CheckForUnfinishedRecordingsAsync()
    {
        foreach (UnfinishedRecording unfinished in RecordingRecovery.Find(Record.RecordingsFolder))
        {
            RecoverChoice choice = _dialogs.AskToRecover(unfinished.Sidecar.StartedAt.LocalDateTime, unfinished.Recorded);
            if (choice == RecoverChoice.NotNow)
                continue;
            try
            {
                RecordingRecovery.Recover(unfinished);
            }
            catch (RecordingException e)
            {
                ShowError(e.Message, e.InnerException?.ToString());
                continue;
            }
            if (choice == RecoverChoice.RecoverAndTranscribe)
            {
                await OnRecordingFinishedAsync(unfinished.Path, transcribe: true, switchTab: true);
                return;
            }
        }
    }

    /// <summary>With Keep recordings after transcribing off, a recording Talktrace made goes to the Recycle Bin once its transcript is saved.</summary>
    private void RecycleRecordingIfNotKept(string recording)
    {
        if (Record.KeepRecordings || RecordingSidecar.TryRead(recording) is null)
            return;
        try
        {
            RecycleBin.Send(recording, RecordingSidecar.PathFor(recording));
            _transcribedRecording = null; // nothing left to play samples from
            foreach (SpeakerRow row in Speakers)
                row.CanPlay = false;
            RecordingPath = null;
            RecordingTitle = "Drop a recording here";
            RecordingDetails = "or choose one: WAV, MP3, M4A, MP4 or WMA";
            StatusText += " · recording moved to the Recycle Bin";
            Log.Info("The recording was moved to the Recycle Bin after transcribing.");
        }
        catch (IOException e)
        {
            Log.Warning("The recording couldn't be moved to the Recycle Bin.", e);
            StatusText += " · the recording couldn't be moved to the Recycle Bin";
        }
    }

    /// <summary>For errors nothing else caught: the details go to the log and Copy details, and the window stays open.</summary>
    public void ShowUnexpectedError(Exception e) =>
        ShowError("Something went wrong in Talktrace. If it keeps happening, restart Talktrace.", e.ToString());

    public void Dispose()
    {
        _player.Dispose();
        Record.Dispose();
    }

    // The speakers panel

    /// <summary>
    /// Applies a name typed for a speaker: replaces their current name in the transcript and the Speakers line. An empty
    /// name goes back to Speaker N; a name another speaker has is refused.
    /// </summary>
    internal void RenameSpeaker(SpeakerRow row, string typed)
    {
        string trimmed = typed.Trim();
        string name = trimmed.Length == 0 ? row.Label : trimmed;
        if (name == row.CurrentName)
        {
            row.NameError = null;
            row.ResetNameText();
            return;
        }
        if (SpeakerNames.IsTaken(name, Speakers.Where(r => r != row).Select(r => r.CurrentName)))
        {
            row.NameError = "Another speaker already has this name.";
            row.ResetNameText();
            return;
        }

        string oldName = row.CurrentName;
        row.CurrentName = name;
        row.NameError = null;
        row.ResetNameText();
        TranscriptRewriting?.Invoke();
        TranscriptText = SpeakerNames.Rename(TranscriptText, oldName, name, Speakers.Select(r => r.CurrentName).ToList());
    }

    /// <summary>Plays the speaker's sample turn from the recording, or stops it if it's already playing.</summary>
    internal void ToggleSample(SpeakerRow row)
    {
        bool wasPlaying = row.IsPlaying;
        StopSample();
        if (wasPlaying || row.SampleTurn is not Turn turn || _transcribedRecording is not string recording)
            return;

        try
        {
            _player.Play(recording, turn.Start, Math.Clamp(turn.End - turn.Start, 1, SampleSeconds));
            row.IsPlaying = true;
            _playingRow = row;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or MediaFoundationException)
        {
            ShowError(FileErrors.Reading(e, Path.GetFileName(recording)), e.ToString());
        }
    }

    private void StopSample()
    {
        _player.Stop();
        OnSampleFinished();
    }

    private void OnSampleFinished()
    {
        if (_playingRow is not null)
            _playingRow.IsPlaying = false;
        _playingRow = null;
    }

    private void ShowSpeakers(TranscriptResult? result)
    {
        Speakers.Clear();
        if (result is { SpeakerLabels: true })
        {
            foreach (int speaker in result.Speakers)
                Speakers.Add(new SpeakerRow(this, speaker, result.SpeakerName(speaker), SpeakerNames.SampleTurn(result.Turns, speaker)));
        }
        HasSpeakers = Speakers.Count > 0;
    }

    // Settings

    private void ApplySettings(UserSettings settings)
    {
        if (UserSettings.ParseSpeakers(settings.Speakers) is SpeakerSetting speakers
            && _fileSpeakerChoices.FirstOrDefault(c => c.Setting == speakers) is SpeakerChoice choice)
            SelectedSpeakers = choice;
        if (UserSettings.ParseSpeakers(settings.CallSpeakers) is SpeakerSetting callSpeakers
            && _callSpeakerChoices.FirstOrDefault(c => c.Setting == callSpeakers) is SpeakerChoice callChoice)
            _callSpeakers = callChoice;
        IncludeTimestamps = settings.IncludeTimestamps;
        if (settings.ClusteringThreshold is double threshold)
            ClusteringThreshold = threshold.ToString(CultureInfo.CurrentCulture);
        if (settings.Threads is int threads)
            Threads = Math.Clamp(threads, 1, Environment.ProcessorCount).ToString(CultureInfo.CurrentCulture);
        if (settings.MaxSpeechSeconds is double segment)
            MaxSpeechSeconds = segment.ToString(CultureInfo.CurrentCulture);
        if (settings.MaxChunkSeconds is double chunk)
            MaxChunkSeconds = chunk.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>Remembers the options a transcription started with; they have passed the checks by then.</summary>
    private void SaveOptions(TranscribeOptions options)
    {
        bool call = IsCallRecording;
        _settings.Update(s => s with
        {
            Speakers = call ? s.Speakers : UserSettings.ToText(options.Speakers),
            CallSpeakers = call ? UserSettings.ToText(options.Speakers) : s.CallSpeakers,
            IncludeTimestamps = options.IncludeTimestamps,
            ClusteringThreshold = options.ClusteringThreshold,
            Threads = options.NumThreads,
            MaxSpeechSeconds = options.VadMaxSpeechSeconds,
            MaxChunkSeconds = options.MaxChunkSeconds,
        });
    }

    private void OnProgress(TranscribeProgress p)
    {
        if (!IsRunning || _jobCancel?.IsCancellationRequested != false)
            return; // arrived after the job ended or was cancelled

        _progress = p;
        StageText = p.Stage switch
        {
            TranscribeStage.ReadingAudio => "Reading audio",
            TranscribeStage.FindingSpeech => "Finding speech",
            TranscribeStage.IdentifyingSpeakers => "Identifying speakers",
            TranscribeStage.Transcribing when p.Total > 0 => $"Transcribing {p.Done} of {p.Total}",
            TranscribeStage.Transcribing => "Transcribing",
            TranscribeStage.Saving => "Saving",
            _ => p.Stage.ToString(),
        };
        // Reading audio, saving, and segmentation at the start of speaker identification report nothing as they go.
        ProgressIsIndeterminate = p.Total <= 0
                                  || p.Stage is TranscribeStage.ReadingAudio or TranscribeStage.Saving
                                  || (p.Stage == TranscribeStage.IdentifyingSpeakers && p.Done == 0);
        ProgressValue = p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : 0;

        if (p.Stage == TranscribeStage.Transcribing && p.Done > 0)
        {
            _firstChunk ??= (_jobClock.Elapsed, p.Done);
            _lastChunkAt = _jobClock.Elapsed;
        }
        UpdateTimeText();
    }

    private void UpdateTimeText()
    {
        if (!IsRunning)
            return;
        string elapsed = $"{TranscriptFormatter.FormatDuration(_jobClock.Elapsed)} elapsed";
        TimeText = TimeLeft() is TimeSpan left ? $"{elapsed} · {DescribeTimeLeft(left)}" : elapsed;
    }

    /// <summary>Estimated from the pace of transcription so far, after the first few chunks. Nothing before that.</summary>
    private TimeSpan? TimeLeft()
    {
        if (_progress is not { Stage: TranscribeStage.Transcribing } p || _firstChunk is not { } first)
            return null;
        int timed = p.Done - first.Done;
        if (timed < 3)
            return null;
        TimeSpan perChunk = (_lastChunkAt - first.At) / timed;
        TimeSpan left = perChunk * (p.Total - p.Done) - (_jobClock.Elapsed - _lastChunkAt);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static string DescribeTimeLeft(TimeSpan left) =>
        left < TimeSpan.FromMinutes(1)
            ? "less than a minute left"
            : $"about {Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero):0} min left";

    private TranscribeOptions BaseOptions() => Defaults with { Profile = SelectedProfile.Profile, Speakers = SelectedSpeakers.Setting };

    /// <summary>The options as set, or null after showing what's wrong with one of them.</summary>
    private TranscribeOptions? ReadOptions()
    {
        if (!TryParseNumber(ClusteringThreshold, out double threshold) || threshold is <= 0 or >= 2)
            return Invalid("The clustering threshold must be a number between 0 and 2, such as 0.6.");
        if (!int.TryParse(Threads, NumberStyles.Integer, CultureInfo.CurrentCulture, out int threads)
            || threads < 1 || threads > Environment.ProcessorCount)
            return Invalid($"Threads must be a whole number from 1 to {Environment.ProcessorCount}.");
        if (!TryParseNumber(MaxChunkSeconds, out double chunk) || chunk is < 5 or > 60)
            return Invalid("The longest chunk must be between 5 and 60 seconds.");
        if (!TryParseNumber(MaxSpeechSeconds, out double segment) || segment is < 5 or > 60)
            return Invalid("The longest speech segment must be between 5 and 60 seconds.");

        return BaseOptions() with
        {
            YourName = Record.EffectiveYourName,
            IncludeTimestamps = IncludeTimestamps,
            NumThreads = threads,
            ClusteringThreshold = threshold,
            MaxChunkSeconds = chunk,
            VadMaxSpeechSeconds = segment,
        };

        TranscribeOptions? Invalid(string problem)
        {
            ShowError($"{problem} Change it under Advanced.", null);
            return null;
        }
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void RefreshModelsMessage()
    {
        if (SelectedProfile is not null && SelectedSpeakers is not null)
            ModelsMessage = MissingModelsMessage(BaseOptions());
    }

    private static string? MissingModelsMessage(TranscribeOptions options)
    {
        IReadOnlyList<string> missing = Transcriber.MissingModels(options);
        if (missing.Count == 0)
            return null;
        return $"Talktrace can't find {(missing.Count == 1 ? "this model" : "these models")}: {string.Join(", ", missing)}. " +
               "Reinstall Talktrace to restore them, then click Transcribe again.";
    }

    private void ShowTranscript(string? text)
    {
        _settingTranscript = true;
        TranscriptText = text ?? "";
        _settingTranscript = false;
        HasTranscript = text is not null;
        HasUnsavedEdits = false;
    }

    /// <summary>True if there are no unsaved edits, or they were saved, or the user chose to discard them.</summary>
    private bool ConfirmDiscardingEdits() =>
        !HasUnsavedEdits || _dialogs.AskToSaveEdits(SavedPath is null ? null : Path.GetFileName(SavedPath)) switch
        {
            SaveChoice.Save => SaveTranscript(),
            SaveChoice.DontSave => true,
            _ => false,
        };

    private bool WriteTranscript(string path)
    {
        Log.Redact(path);
        try
        {
            TranscriptFile.Overwrite(path, TranscriptText);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowError(FileErrors.Saving(e, Path.GetFileName(path)), e.ToString());
            return false;
        }
        ClearError();
        SavedPath = path;
        HasUnsavedEdits = false;
        StatusText = $"Saved as {Path.GetFileName(path)}";
        return true;
    }

    private bool CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException e)
        {
            ShowError("Talktrace couldn't use the clipboard; another app may be holding it. Try again.", e.ToString());
            return false;
        }
    }

    internal static void ShowInExplorer(string arguments)
    {
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false })?.Dispose();
    }

    private void ShowError(string message, string? details)
    {
        ErrorMessage = message;
        _errorDetails = details;
        HasErrorDetails = details is not null;
        Log.Warning($"Shown to the user: {message}");
        if (details is not null)
            Log.Error("Details of the error shown.", details);
    }

    private static string DescribeType(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Length == 0 ? "no-extension" : extension.TrimStart('.').ToUpperInvariant();
    }

    private void ClearError()
    {
        ErrorMessage = null;
        _errorDetails = null;
        HasErrorDetails = false;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
