using System.Diagnostics;
using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;
using Talktrace.Core.Pipeline;

namespace Talktrace.Core;

/// <summary>
/// Times the transcription stage alone on the start of a recording, so thread counts and batch
/// sizes can be compared without re-reading the audio each time.
/// </summary>
public sealed class AsrBenchmark
{
    private readonly AudioClip _audio;
    private readonly IReadOnlyList<TimeRange> _chunks;
    private readonly string _asrDirectory;

    private AsrBenchmark(AudioClip audio, IReadOnlyList<TimeRange> chunks, string asrDirectory)
    {
        _audio = audio;
        _chunks = chunks;
        _asrDirectory = asrDirectory;
    }

    public int ChunkCount => _chunks.Count;

    /// <summary>Length of the recording the chunks cover, in seconds.</summary>
    public double CoveredSeconds => _chunks.Count == 0 ? 0 : _chunks[^1].End;

    /// <summary>Reads the recording and finds the chunks that start within the first <paramref name="minutes"/>.</summary>
    public static AsrBenchmark Prepare(string path, TranscribeOptions options, double minutes)
    {
        string asrDirectory = ModelLocator.Find(options.Profile.AsrDirectory)
            ?? throw new TalktraceException($"The {options.Profile.DisplayName} model isn't installed.");
        string vadModel = ModelLocator.Find(Transcriber.VadModelFile)
            ?? throw new TalktraceException("The voice activity model isn't installed.");

        AudioClip audio = AudioDecoder.Decode(path);
        IReadOnlyList<TimeRange> speech = SpeechDetector.FindSpeech(audio, vadModel, options, null, CancellationToken.None);
        var chunks = Chunker.Build(speech, audio.Samples, audio.SampleRate, options)
            .Where(c => c.Start < minutes * 60)
            .ToList();
        return new AsrBenchmark(audio, chunks, asrDirectory);
    }

    /// <summary>Transcribes every chunk and returns the time taken, excluding model loading and one warm-up chunk.</summary>
    public TimeSpan Measure(int threads, int batchSize)
    {
        using var recognizer = new ParakeetRecognizer(_asrDirectory, threads);
        if (_chunks.Count > 0)
            recognizer.Recognize([Transcriber.Slice(_audio, _chunks[0])], _audio.SampleRate);

        var clock = Stopwatch.StartNew();
        for (int first = 0; first < _chunks.Count; first += batchSize)
        {
            var batch = _chunks.Skip(first).Take(batchSize).Select(c => Transcriber.Slice(_audio, c)).ToList();
            recognizer.Recognize(batch, _audio.SampleRate);
        }
        return clock.Elapsed;
    }
}
