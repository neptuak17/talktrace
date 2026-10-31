using System.Diagnostics;
using System.Globalization;
using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Logging;
using Talktrace.Core.Models;
using Talktrace.Core.Pipeline;
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
        bool identifySpeakers = IdentifiesSpeakers(options);
        Log.Redact(path);
        LogStarted(path, options);

        string asrDirectory = RequireModel(options.Profile.AsrDirectory, options.Profile);
        string vadModel = RequireModel(VadModelFile, options.Profile);
        string? segmentationModel = identifySpeakers ? RequireModel(SpeakerDiarizer.SegmentationModelFile, options.Profile) : null;
        string? embeddingModel = identifySpeakers ? RequireModel(SpeakerDiarizer.EmbeddingModelFile, options.Profile) : null;
        var timings = new List<StageTiming>();
        var clock = Stopwatch.StartNew();

        void Report(TranscribeStage stage, int done, int total) => progress?.Report(new TranscribeProgress(stage, done, total));
        void EndStage(TranscribeStage stage)
        {
            timings.Add(new StageTiming(stage, clock.Elapsed));
            clock.Restart();
        }

        Report(TranscribeStage.ReadingAudio, 0, 1);
        AudioClip audio = AudioDecoder.Decode(path, cancellationToken);
        EndStage(TranscribeStage.ReadingAudio);

        Report(TranscribeStage.FindingSpeech, 0, 100);
        IReadOnlyList<TimeRange> speech = SpeechDetector.FindSpeech(
            audio, vadModel, options, f => Report(TranscribeStage.FindingSpeech, (int)(f * 100), 100), cancellationToken);
        IReadOnlyList<TimeRange> chunks = Chunker.Build(speech, audio.Samples, audio.SampleRate, options);
        EndStage(TranscribeStage.FindingSpeech);

        IReadOnlyList<SpeakerSegment> segments = [];
        if (identifySpeakers)
        {
            Report(TranscribeStage.IdentifyingSpeakers, 0, 1);
            segments = SpeakerDiarizer.FindSpeakers(
                audio, segmentationModel!, embeddingModel!, options,
                (done, total) => Report(TranscribeStage.IdentifyingSpeakers, done, total), cancellationToken);
            EndStage(TranscribeStage.IdentifyingSpeakers);
        }

        Report(TranscribeStage.Transcribing, 0, chunks.Count);
        IReadOnlyList<Word> words = TranscribeChunks(audio, chunks, asrDirectory, options, Report, cancellationToken);
        IReadOnlyList<Turn> turns = options.Speakers.Label
            ? TurnBuilder.Turns(AssignSpeakers(words, segments, options))
            : TurnBuilder.Paragraphs(words, options.ParagraphPauseSeconds);
        EndStage(TranscribeStage.Transcribing);

        Report(TranscribeStage.Saving, 0, 1);
        var result = new TranscriptResult(
            SourceFileName: Path.GetFileName(path),
            TranscribedAt: DateTime.Now,
            Duration: TimeSpan.FromSeconds(audio.DurationSeconds),
            ModelName: options.Profile.DisplayName,
            SpeakerLabels: options.Speakers.Label,
            Turns: turns,
            Timings: timings,
            ChunkCount: chunks.Count,
            SavedPath: null);
        result = Save(result, path, options);
        EndStage(TranscribeStage.Saving);
        Report(TranscribeStage.Saving, 1, 1);

        result = result with { Timings = timings };
        LogFinished(result, options);
        return result;
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

    private static void LogStarted(string path, TranscribeOptions options)
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
            $"Job started: {type} file, {size}; model {options.Profile.Id}; speakers {speakers}; threads {options.NumThreads}; " +
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

    /// <summary>
    /// Transcribes each chunk with one recognizer, checking for cancellation between chunks.
    /// One chunk per call: batching several was slower in the Milestone 2 benchmark.
    /// </summary>
    internal static IReadOnlyList<Word> TranscribeChunks(
        AudioClip audio,
        IReadOnlyList<TimeRange> chunks,
        string asrDirectory,
        TranscribeOptions options,
        Action<TranscribeStage, int, int> report,
        CancellationToken cancellationToken)
    {
        var words = new List<Word>();
        cancellationToken.ThrowIfCancellationRequested(); // before the model takes a few seconds to load
        using var recognizer = new ParakeetRecognizer(asrDirectory, options.NumThreads);

        for (int i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioSlice slice = Slice(audio, chunks[i]);
            RecognizedText recognized = recognizer.Recognize(slice.Samples, audio.SampleRate, slice.OffsetSeconds);
            if (!recognized.HasWordTimes && recognized.Text.Length > 0)
                throw new TalktraceException("The speech model returned text without word times, which Talktrace needs.");
            words.AddRange(recognized.Words);
            report(TranscribeStage.Transcribing, i + 1, chunks.Count);
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
            @"Run scripts\get-models.ps1 to download them.");
}
