using System.Diagnostics;
using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;
using Talktrace.Core.Pipeline;
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
        if (options.Speakers.Label)
            throw new NotSupportedException("Speaker labels aren't implemented yet.");

        string asrDirectory = RequireModel(options.Profile.AsrDirectory, options.Profile);
        string vadModel = RequireModel(VadModelFile, options.Profile);
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

        Report(TranscribeStage.Transcribing, 0, chunks.Count);
        IReadOnlyList<Word> words = TranscribeChunks(audio, chunks, asrDirectory, options, Report, cancellationToken);
        IReadOnlyList<Turn> turns = TurnBuilder.Paragraphs(words, options.ParagraphPauseSeconds);
        EndStage(TranscribeStage.Transcribing);

        Report(TranscribeStage.Saving, 0, 1);
        var result = new TranscriptResult(
            SourceFileName: Path.GetFileName(path),
            TranscribedAt: DateTime.Now,
            Duration: TimeSpan.FromSeconds(audio.DurationSeconds),
            ModelName: options.Profile.DisplayName,
            SpeakerLabels: false,
            Turns: turns,
            Timings: timings,
            ChunkCount: chunks.Count,
            SavedPath: "");
        string text = TranscriptFormatter.Format(result, options.IncludeTimestamps);
        string savedPath;
        if (options.OutputPath is null)
        {
            savedPath = TranscriptFile.SaveNew(TranscriptFile.DefaultPath(path), text);
        }
        else
        {
            TranscriptFile.Overwrite(options.OutputPath, text);
            savedPath = Path.GetFullPath(options.OutputPath);
        }
        EndStage(TranscribeStage.Saving);
        Report(TranscribeStage.Saving, 1, 1);

        return result with { SavedPath = savedPath, Timings = timings };
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

    internal static AudioSlice Slice(AudioClip audio, TimeRange range)
    {
        int start = (int)Math.Round(range.Start * audio.SampleRate);
        int end = Math.Min(audio.Samples.Length, (int)Math.Round(range.End * audio.SampleRate));
        return new AudioSlice(audio.Samples[start..end], (double)start / audio.SampleRate);
    }

    private static string RequireModel(string relativePath, ModelProfile profile) =>
        ModelLocator.Find(relativePath)
        ?? throw new TalktraceException(
            $"The {profile.DisplayName} models aren't installed ({relativePath} is missing). " +
            @"Run scripts\get-models.ps1 to download them.");
}
