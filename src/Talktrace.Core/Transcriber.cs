using System.Diagnostics;
using System.Globalization;
using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Calls;
using Talktrace.Core.Logging;
using Talktrace.Core.Models;
using Talktrace.Core.Pipeline;
using Talktrace.Core.Recordings;
using Talktrace.Core.Speakers;
using Talktrace.Core.Transcripts;

namespace Talktrace.Core;

/// <summary>The transcription pipeline (DESIGN.md section 7). The CLI and the worker both call this.</summary>
public static class Transcriber
{
    public const string VadModelFile = "silero_vad.onnx";

    public static Task<TranscriptResult> TranscribeAsync(
        string path, TranscribeOptions options, IProgress<TranscribeProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Transcribe(path, options, progress, cancellationToken), cancellationToken);

    private static TranscriptResult Transcribe(
        string path, TranscribeOptions options, IProgress<TranscribeProgress>? progress, CancellationToken cancellationToken)
    {
        Log.Redact(path);
        RecordingSidecar? call = CallRecording(path);
        LogStarted(path, options, call);

        var models = new JobModels(options);
        var stages = new Stages(progress);
        TranscriptResult result = call is null
            ? TranscribeOneTrack(path, options, models, stages, cancellationToken)
            : TranscribeCall(path, call, options, models, stages, cancellationToken);

        stages.Report(TranscribeStage.Saving, 0, 1);
        result = Save(result, path, options);
        stages.End(TranscribeStage.Saving);
        stages.Report(TranscribeStage.Saving, 1, 1);

        result = result with { Timings = stages.Timings };
        LogFinished(result, options);
        return result;
    }

    /// <summary>The normal pipeline (DESIGN.md section 7) on the whole recording, mixed to mono.</summary>
    private static TranscriptResult TranscribeOneTrack(
        string path, TranscribeOptions options, JobModels models, Stages stages, CancellationToken cancellationToken)
    {
        stages.Report(TranscribeStage.ReadingAudio, 0, 1);
        AudioClip audio = AudioDecoder.Decode(path, cancellationToken);
        stages.End(TranscribeStage.ReadingAudio);

        stages.Report(TranscribeStage.FindingSpeech, 0, 100);
        IReadOnlyList<TimeRange> chunks = FindChunks(audio, models, options, stages, 0, 100, cancellationToken);
        stages.End(TranscribeStage.FindingSpeech);

        IReadOnlyList<SpeakerSegment> segments = FindSpeakers(audio, models, options, stages, cancellationToken);

        stages.Report(TranscribeStage.Transcribing, 0, chunks.Count);
        IReadOnlyList<Word> words;
        using (var recognizer = LoadRecognizer(models, options, cancellationToken))
            words = TranscribeSlices(recognizer, chunks.Select(c => Slice(audio, c)), audio.SampleRate, 0, chunks.Count, stages, cancellationToken);
        IReadOnlyList<Turn> turns = options.Speakers.Label
            ? TurnBuilder.Turns(AssignSpeakers(words, segments, options))
            : TurnBuilder.Paragraphs(words, options.ParagraphPauseSeconds);
        stages.End(TranscribeStage.Transcribing);

        return NewResult(path, audio.DurationSeconds, options, turns, chunks.Count, yourName: null);
    }

    /// <summary>
    /// The two-track path for call recordings (DESIGN.md section 9): the microphone track is transcribed and every word
    /// given my name; the system-audio track goes through the full pipeline; in speakers mode, microphone phrases the
    /// call also holds are removed as echo; then both are merged in time order.
    /// </summary>
    private static TranscriptResult TranscribeCall(
        string path, RecordingSidecar call, TranscribeOptions options, JobModels models, Stages stages, CancellationToken cancellationToken)
    {
        (AudioClip system, List<AudioSlice> micSlices, double duration) = ReadCall(path, call, models, options, stages, cancellationToken);
        IReadOnlyList<TimeRange> systemChunks = FindChunks(system, models, options, stages, 50, 100, cancellationToken);
        stages.End(TranscribeStage.FindingSpeech);

        IReadOnlyList<SpeakerSegment> segments = FindSpeakers(system, models, options, stages, cancellationToken);

        int micCount = micSlices.Count;
        int total = micCount + systemChunks.Count;
        stages.Report(TranscribeStage.Transcribing, 0, total);
        IReadOnlyList<Word> micWords, systemWords;
        using (var recognizer = LoadRecognizer(models, options, cancellationToken))
        {
            micWords = TranscribeSlices(recognizer, micSlices, system.SampleRate, 0, total, stages, cancellationToken);
            micSlices.Clear();
            systemWords = TranscribeSlices(
                recognizer, systemChunks.Select(c => Slice(system, c)), system.SampleRate, micCount, total, stages, cancellationToken);
        }

        if (call.Mode == RecordingMode.CallSpeakers)
        {
            EchoRemoval echo = EchoRemover.Remove(micWords, systemWords, options);
            Log.Info($"Echo removal: dropped {echo.DroppedPhrases} of {echo.Phrases} microphone phrases.");
            micWords = echo.Kept;
        }

        IReadOnlyList<Turn> turns = options.Speakers.Label
            ? CallTurnBuilder.Turns(micWords, AssignSpeakers(systemWords, segments, options), options.PhrasePauseSeconds)
            : CallTurnBuilder.Paragraphs(micWords, systemWords, options.PhrasePauseSeconds, options.ParagraphPauseSeconds);
        stages.End(TranscribeStage.Transcribing);

        return NewResult(path, duration, options, turns, total, yourName: options.YourName);
    }

    /// <summary>
    /// Reads both tracks and finds the microphone's speech. Only that speech is kept, not the whole microphone track,
    /// to leave room for speaker identification on the system-audio track. Ends partway through Finding speech.
    /// </summary>
    private static (AudioClip System, List<AudioSlice> MicSlices, double DurationSeconds) ReadCall(
        string path, RecordingSidecar call, JobModels models, TranscribeOptions options, Stages stages, CancellationToken cancellationToken)
    {
        stages.Report(TranscribeStage.ReadingAudio, 0, 1);
        (AudioClip left, AudioClip right) = AudioDecoder.DecodeStereo(path, cancellationToken);
        (AudioClip mic, AudioClip system) = call.MicrophoneChannel == 1 ? (right, left) : (left, right);
        stages.End(TranscribeStage.ReadingAudio);

        stages.Report(TranscribeStage.FindingSpeech, 0, 100);
        IReadOnlyList<TimeRange> micChunks = FindChunks(mic, models, options, stages, 0, 50, cancellationToken);
        return (system, micChunks.Select(c => Slice(mic, c)).ToList(), Math.Max(left.DurationSeconds, right.DurationSeconds));
    }

    /// <summary>
    /// The sidecar of a call recording Talktrace made, if <paramref name="path"/> is one; null for any other recording,
    /// which takes the normal path. A call sidecar beside a file that isn't stereo is ignored.
    /// </summary>
    private static RecordingSidecar? CallRecording(string path)
    {
        if (RecordingSidecar.TryRead(path) is not { IsCall: true } sidecar)
            return null;
        if (AudioDecoder.ReadChannelCount(path) == 2)
            return sidecar;
        Log.Warning("A call recording's sidecar is beside a file that isn't stereo, so it is transcribed as one track.");
        return null;
    }

    /// <summary>Steps 2 and 3: speech regions, packed into chunks. Progress runs from <paramref name="from"/> to <paramref name="to"/> percent.</summary>
    private static IReadOnlyList<TimeRange> FindChunks(
        AudioClip audio, JobModels models, TranscribeOptions options, Stages stages, int from, int to, CancellationToken cancellationToken)
    {
        IReadOnlyList<TimeRange> speech = SpeechDetector.FindSpeech(
            audio, models.Vad, options,
            f => stages.Report(TranscribeStage.FindingSpeech, from + (int)(f * (to - from)), 100), cancellationToken);
        return Chunker.Build(speech, audio.Samples, audio.SampleRate, options);
    }

    /// <summary>Step 4, unless speaker labels are off or there is only one speaker to find.</summary>
    private static IReadOnlyList<SpeakerSegment> FindSpeakers(
        AudioClip audio, JobModels models, TranscribeOptions options, Stages stages, CancellationToken cancellationToken)
    {
        if (models.Segmentation is null || models.Embedding is null)
            return [];
        stages.Report(TranscribeStage.IdentifyingSpeakers, 0, 1);
        IReadOnlyList<SpeakerSegment> segments = SpeakerDiarizer.FindSpeakers(
            audio, models.Segmentation, models.Embedding, options,
            (done, total) => stages.Report(TranscribeStage.IdentifyingSpeakers, done, total), cancellationToken);
        stages.End(TranscribeStage.IdentifyingSpeakers);
        return segments;
    }

    private static TranscriptResult NewResult(
        string path, double durationSeconds, TranscribeOptions options, IReadOnlyList<Turn> turns, int chunkCount, string? yourName) =>
        new(
            SourceFileName: Path.GetFileName(path),
            TranscribedAt: DateTime.Now,
            Duration: TimeSpan.FromSeconds(durationSeconds),
            ModelName: options.Profile.DisplayName,
            SpeakerLabels: options.Speakers.Label,
            Turns: turns,
            Timings: [],
            ChunkCount: chunkCount,
            SavedPath: null,
            YourName: yourName);

    /// <summary>The model files a job needs, found before any work starts so a missing one fails at once.</summary>
    private sealed class JobModels
    {
        public JobModels(TranscribeOptions options)
        {
            bool identifySpeakers = IdentifiesSpeakers(options);
            Asr = RequireModel(options.Profile.AsrDirectory, options.Profile);
            Vad = RequireModel(VadModelFile, options.Profile);
            Segmentation = identifySpeakers ? RequireModel(SpeakerDiarizer.SegmentationModelFile, options.Profile) : null;
            Embedding = identifySpeakers ? RequireModel(SpeakerDiarizer.EmbeddingModelFile, options.Profile) : null;
        }

        public string Asr { get; }
        public string Vad { get; }
        public string? Segmentation { get; }
        public string? Embedding { get; }
    }

    /// <summary>Reports progress and times each stage.</summary>
    private sealed class Stages(IProgress<TranscribeProgress>? progress)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public List<StageTiming> Timings { get; } = [];

        public void Report(TranscribeStage stage, int done, int total) => progress?.Report(new TranscribeProgress(stage, done, total));

        public void End(TranscribeStage stage)
        {
            Timings.Add(new StageTiming(stage, _clock.Elapsed));
            _clock.Restart();
        }
    }

    /// <summary>
    /// Saves next to the recording, never overwriting, or where <see cref="TranscribeOptions.OutputPath"/> says.
    /// A failed save still returns the transcript, with the reason, so the job isn't lost (DESIGN.md section 10).
    /// </summary>
    private static TranscriptResult Save(TranscriptResult result, string recordingPath, TranscribeOptions options)
    {
        string target = options.OutputPath ?? TranscriptFile.DefaultPath(recordingPath);
        Log.Redact(target);
        string text = TranscriptFormatter.Format(result, options.IncludeTimestamps);
        try
        {
            if (options.OutputPath is null)
                return result with { SavedPath = TranscriptFile.SaveNew(target, text) };
            TranscriptFile.Overwrite(target, text);
            return result with { SavedPath = Path.GetFullPath(target) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warning("The transcript couldn't be saved.", e);
            return result with { SaveError = FileErrors.Saving(e, Path.GetFileName(target)) };
        }
    }

    private static void LogStarted(string path, TranscribeOptions options, RecordingSidecar? call)
    {
        var file = new FileInfo(path);
        string type = string.IsNullOrEmpty(file.Extension) ? "no extension" : file.Extension.TrimStart('.').ToUpperInvariant();
        string size = file.Exists ? $"{file.Length / (1024.0 * 1024):0.0} MB" : "missing";
        string speakers = options.Speakers switch
        {
            { Label: false } => "none",
            { Count: int n } => n.ToString(CultureInfo.InvariantCulture),
            _ => "auto",
        };
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Job started: {type} file, {size}{(call is null ? "" : $", call recording ({call.Mode})")}; " +
            $"model {options.Profile.Id}; speakers {speakers}; threads {options.NumThreads}; " +
            $"threshold {options.ClusteringThreshold}; longest segment {options.VadMaxSpeechSeconds} s; longest chunk {options.MaxChunkSeconds} s; " +
            $"timestamps {(options.IncludeTimestamps ? "on" : "off")}."));
    }

    private static void LogFinished(TranscriptResult result, TranscribeOptions options)
    {
        string stages = string.Join(", ", result.Timings.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Stage} {t.Elapsed.TotalSeconds:0.0} s")));
        string found = result.SpeakerLabels
            ? $"{result.Speakers.Count} speakers, {result.Turns.Count} turns"
            : $"{result.Turns.Count} paragraphs";
        long peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024);
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"Job done: audio {TranscriptFormatter.FormatDuration(result.Duration)}; {result.ChunkCount} chunks; {found}; {stages}; " +
            $"total {result.TotalElapsed.TotalSeconds:0.0} s, real-time factor {result.RealTimeFactor:0.000}; threads {options.NumThreads}; " +
            $"peak memory {peakMb} MB; {(result.SavedPath is null ? "not saved" : "saved")}."));
    }

    private static ParakeetRecognizer LoadRecognizer(JobModels models, TranscribeOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); // before the model takes a few seconds to load
        return new ParakeetRecognizer(models.Asr, options.NumThreads);
    }

    /// <summary>
    /// Transcribes each slice, checking for cancellation between them. One slice per call: batching several was slower
    /// in the Milestone 2 benchmark. Progress counts on from <paramref name="done"/> of <paramref name="total"/>.
    /// </summary>
    private static IReadOnlyList<Word> TranscribeSlices(
        ParakeetRecognizer recognizer,
        IEnumerable<AudioSlice> slices,
        int sampleRate,
        int done,
        int total,
        Stages stages,
        CancellationToken cancellationToken)
    {
        var words = new List<Word>();
        foreach (AudioSlice slice in slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecognizedText recognized = recognizer.Recognize(slice.Samples, sampleRate, slice.OffsetSeconds);
            if (!recognized.HasWordTimes && recognized.Text.Length > 0)
                throw new TalktraceException("The speech model returned text without word times, which Talktrace needs.");
            words.AddRange(recognized.Words);
            stages.Report(TranscribeStage.Transcribing, ++done, total);
        }

        return words;
    }

    /// <summary>Step 6 of the pipeline: a speaker for each word, then smoothing of short slips.</summary>
    internal static IReadOnlyList<SpeakerWord> AssignSpeakers(
        IReadOnlyList<Word> words, IReadOnlyList<SpeakerSegment> segments, TranscribeOptions options) =>
        SpeakerAssigner.Smooth(
            SpeakerAssigner.Assign(words, segments, options.SpeakerReachSeconds),
            options.ShortRunSeconds,
            options.ShortRunPauseSeconds);

    internal static AudioSlice Slice(AudioClip audio, TimeRange range)
    {
        int start = (int)Math.Round(range.Start * audio.SampleRate);
        int end = Math.Min(audio.Samples.Length, (int)Math.Round(range.End * audio.SampleRate));
        return new AudioSlice(audio.Samples[start..end], (double)start / audio.SampleRate);
    }

    /// <summary>The model files a job with these options needs that aren't installed, as paths relative to the models folder.</summary>
    public static IReadOnlyList<string> MissingModels(TranscribeOptions options)
    {
        string[] needed = IdentifiesSpeakers(options)
            ? [options.Profile.AsrDirectory, VadModelFile, SpeakerDiarizer.SegmentationModelFile, SpeakerDiarizer.EmbeddingModelFile]
            : [options.Profile.AsrDirectory, VadModelFile];
        return needed.Where(file => ModelLocator.Find(file) is null).ToList();
    }

    // With one speaker there is nobody to tell apart, so every word goes to Speaker 1.
    private static bool IdentifiesSpeakers(TranscribeOptions options) => options.Speakers.Label && options.Speakers.Count != 1;

    private static string RequireModel(string relativePath, ModelProfile profile) =>
        ModelLocator.Find(relativePath)
        ?? throw new TalktraceException(
            $"The {profile.DisplayName} models aren't installed ({relativePath} is missing). " +
            "Reinstall Talktrace to restore them.");
}
