using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talktrace.Core;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;
using Talktrace.Core.Transcripts;
using Talktrace.Core.Worker;

namespace Talktrace.App;

/// <summary>
/// The Transcribe a file screen (DESIGN.md section 10). One recording and one job at a time; the job runs in the
/// worker process, which auto-saves the transcript before it reports the result.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly TranscribeOptions Defaults = new();

    private readonly IDialogs _dialogs;
    private readonly DispatcherTimer _clockTimer;
    private readonly Stopwatch _jobClock = new();
    private CancellationTokenSource? _jobCancel;
    private TranscribeProgress? _progress;
    private (TimeSpan At, int Done)? _firstChunk;
    private TimeSpan _lastChunkAt;
    private string? _transcribedRecording;
    private string? _errorDetails;
    private bool _settingTranscript;
    private int _openRequests;

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateTimeText();

        SpeakerChoices =
        [
            new("Don't label speakers", SpeakerSetting.None),
            new("Auto-detect", SpeakerSetting.Auto),
            .. Enumerable.Range(2, 7).Select(n => new SpeakerChoice(n.ToString(CultureInfo.CurrentCulture), SpeakerSetting.Exactly(n))),
        ];
        Profiles = ModelProfile.All.Select(p => new ProfileChoice(p, ModelLocator.Find(p.AsrDirectory) is not null)).ToList();
        SelectedProfile = Profiles.FirstOrDefault(p => p.IsInstalled) ?? Profiles[0];
        SelectedSpeakers = SpeakerChoices[1];
        ResetAdvanced();

        RecordingTitle = "Drop a recording here";
        RecordingDetails = "or choose one: WAV, MP3, M4A, MP4 or WMA";
        StageText = "";
        TimeText = "";
        TranscriptText = "";
        StatusText = "";
    }

    // The recording

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    public partial string? RecordingPath { get; private set; }

    [ObservableProperty]
    public partial string RecordingTitle { get; private set; }

    [ObservableProperty]
    public partial string RecordingDetails { get; private set; }

    // Options

    public IReadOnlyList<SpeakerChoice> SpeakerChoices { get; }

    public IReadOnlyList<ProfileChoice> Profiles { get; }

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

    // Errors

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial bool HasErrorDetails { get; private set; }

    /// <summary>True when closing needs no questions: no job running and no unsaved edits.</summary>
    public bool CanCloseNow => !IsRunning && !HasUnsavedEdits;

    partial void OnSelectedProfileChanged(ProfileChoice value) => RefreshModelsMessage();

    partial void OnSelectedSpeakersChanged(SpeakerChoice value) => RefreshModelsMessage();

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
        try
        {
            TimeSpan duration = await Task.Run(() => AudioDecoder.ReadDuration(path));
            if (request != _openRequests || IsRunning)
                return; // replaced by a later choice
            string fullPath = Path.GetFullPath(path);
            RecordingPath = fullPath;
            RecordingTitle = Path.GetFileName(fullPath);
            RecordingDetails = $"{TranscriptFormatter.FormatDuration(duration)} · {FormatSize(new FileInfo(fullPath).Length)} · " +
                               Path.GetDirectoryName(fullPath);
        }
        catch (Exception e) when (e is TalktraceException or IOException or UnauthorizedAccessException)
        {
            if (request == _openRequests)
                ShowError(e.Message, e.InnerException?.ToString() ?? e.ToString());
        }
    }

    private bool CanTranscribe => RecordingPath is not null && !IsRunning;

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

        using var cancel = new CancellationTokenSource();
        _jobCancel = cancel;
        ShowTranscript(null);
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
            SavedPath = result.SavedPath;
            StatusText = $"Saved as {Path.GetFileName(result.SavedPath)}";
            StageText = $"Done in {TranscriptFormatter.FormatDuration(_jobClock.Elapsed)}";
        }
        catch (OperationCanceledException)
        {
            StageText = "Cancelled";
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

    private bool CanSave => HasTranscript && !IsRunning && SavedPath is not null;

    /// <summary>Saves edits over the file last saved, at first the auto-saved transcript.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => WriteTranscript(SavedPath!);

    private bool CanUseTranscript => HasTranscript && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanUseTranscript))]
    private void SaveAs()
    {
        if (_dialogs.ChooseSavePath(TranscriptFile.DefaultPath(_transcribedRecording!)) is string path)
            WriteTranscript(path);
    }

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
    private void OpenModelsFolder()
    {
        string folder = ModelLocator.UserFolder;
        try
        {
            Directory.CreateDirectory(folder);
            ShowInExplorer($"\"{folder}\"");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError($"Talktrace couldn't open the models folder: {e.Message}", e.ToString());
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

    /// <summary>Asks about a running job and unsaved edits. Returns true once the window may close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
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
               @"Download them with scripts\get-models.ps1, then click Transcribe again.";
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
        !HasUnsavedEdits || _dialogs.AskToSaveEdits(Path.GetFileName(SavedPath!)) switch
        {
            SaveChoice.Save => WriteTranscript(SavedPath!),
            SaveChoice.DontSave => true,
            _ => false,
        };

    private bool WriteTranscript(string path)
    {
        try
        {
            TranscriptFile.Overwrite(path, TranscriptText);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError($"Talktrace couldn't save {Path.GetFileName(path)}: {e.Message}", e.ToString());
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

    private static void ShowInExplorer(string arguments)
    {
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false })?.Dispose();
    }

    private void ShowError(string message, string? details)
    {
        ErrorMessage = message;
        _errorDetails = details;
        HasErrorDetails = details is not null;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        _errorDetails = null;
        HasErrorDetails = false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
