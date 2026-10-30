using SherpaOnnx;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;
using Talktrace.Core.Pipeline;

namespace Talktrace.Core.Speakers;

/// <summary>
/// Finds who spoke when over the whole recording (DESIGN.md section 7, step 4): sherpa-onnx speaker
/// diarization (pyannote segmentation, TitaNet speaker embeddings, fast clustering) set to over-split,
/// then <see cref="SpeakerMerger"/> merges the groups by voice.
/// </summary>
internal static class SpeakerDiarizer
{
    // Full precision: the int8 model found no speaker in a third of the Milestone 3 test recording's speech.
    public const string SegmentationModelFile = @"sherpa-onnx-pyannote-segmentation-3-0\model.onnx";
    public const string EmbeddingModelFile = "nemo_en_titanet_small.onnx";

    /// <param name="report">Called with speaker embeddings done out of total. Segmentation runs first and reports nothing.</param>
    /// <returns>Segments sorted by start time. Segments of different speakers can overlap.</returns>
    public static IReadOnlyList<SpeakerSegment> FindSpeakers(
        AudioClip audio,
        string segmentationModel,
        string embeddingModel,
        TranscribeOptions options,
        Action<int, int>? report,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeakerSegment> segments = Diarize(audio, segmentationModel, embeddingModel, options, report, cancellationToken);
        IReadOnlyList<VoiceGroup> groups = VoicePrints(audio, segments, embeddingModel, options);
        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(
            groups, options.Speakers.Count, options.ClusteringThreshold, options.MinSpeakerSeconds);

        // Groups too short for a voice print are dropped; their words take a neighbouring speaker in step 6.
        return segments
            .Where(s => speakers.ContainsKey(s.Speaker))
            .Select(s => s with { Speaker = speakers[s.Speaker] })
            .ToList();
    }

    /// <summary>Runs sherpa-onnx diarization with the over-splitting threshold, ignoring any chosen count.</summary>
    internal static IReadOnlyList<SpeakerSegment> Diarize(
        AudioClip audio,
        string segmentationModel,
        string embeddingModel,
        TranscribeOptions options,
        Action<int, int>? report,
        CancellationToken cancellationToken)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = ModelLocator.CheckOpenable(segmentationModel);
        config.Segmentation.NumThreads = options.NumThreads;
        config.Embedding.Model = ModelLocator.CheckOpenable(embeddingModel);
        config.Embedding.NumThreads = options.NumThreads;
        // A fixed cluster count would be spent on stray embeddings; SpeakerMerger applies the chosen count.
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = options.SplitThreshold;
        config.MinDurationOn = (float)options.MinSpeakerSegmentSeconds;
        config.MinDurationOff = (float)options.SpeakerSegmentMergeSeconds;

        cancellationToken.ThrowIfCancellationRequested();
        using var diarizer = new OfflineSpeakerDiarization(config);
        if (NativeHandle.IsNull(diarizer))
            throw new TalktraceException(
                @"The speaker identification models couldn't be loaded. Run scripts\get-models.ps1 to replace them.");
        if (diarizer.SampleRate != audio.SampleRate)
            throw new InvalidOperationException(
                $"Speaker identification needs {diarizer.SampleRate} Hz audio, not {audio.SampleRate} Hz.");

        cancellationToken.ThrowIfCancellationRequested();
        OfflineSpeakerDiarizationSegment[] segments = diarizer.ProcessWithCallback(
            audio.Samples,
            (done, total, _) =>
            {
                report?.Invoke(done, total);
                return 0; // sherpa-onnx ignores this, so a run can't be stopped midway (DESIGN.md section 6)
            },
            IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        return segments.Select(s => new SpeakerSegment(s.Start, s.End, s.Speaker)).ToList();
    }

    /// <summary>
    /// One voice print per speaker group, from up to <see cref="TranscribeOptions.VoicePrintSeconds"/> of its
    /// speech, longest stretches first. Stretches where another group also speaks are left out unless the
    /// group has nothing else.
    /// </summary>
    internal static IReadOnlyList<VoiceGroup> VoicePrints(
        AudioClip audio, IReadOnlyList<SpeakerSegment> segments, string embeddingModel, TranscribeOptions options)
    {
        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = ModelLocator.CheckOpenable(embeddingModel);
        config.NumThreads = options.NumThreads;

        using var extractor = new SpeakerEmbeddingExtractor(config);
        if (NativeHandle.IsNull(extractor))
            throw new TalktraceException(
                @"The speaker identification models couldn't be loaded. Run scripts\get-models.ps1 to replace them.");

        var groups = new List<VoiceGroup>();
        foreach (IGrouping<int, SpeakerSegment> group in segments.GroupBy(s => s.Speaker).OrderBy(g => g.Key))
        {
            List<TimeRange> all = group.Select(s => new TimeRange(s.Start, s.End)).ToList();
            List<SpeakerSegment> others = segments.Where(s => s.Speaker != group.Key).ToList();
            List<TimeRange> clean = all.SelectMany(r => Subtract(r, others)).ToList();

            float[]? embedding = Embed(extractor, audio, clean, options.VoicePrintSeconds)
                                 ?? Embed(extractor, audio, all, options.VoicePrintSeconds);
            groups.Add(new VoiceGroup(group.Key, all.Sum(r => r.Duration), embedding));
        }
        return groups;
    }

    /// <summary>The parts of <paramref name="range"/> that none of <paramref name="others"/> covers.</summary>
    internal static IEnumerable<TimeRange> Subtract(TimeRange range, IEnumerable<SpeakerSegment> others)
    {
        var pieces = new List<TimeRange> { range };
        foreach (SpeakerSegment other in others)
        {
            var remaining = new List<TimeRange>();
            foreach (TimeRange piece in pieces)
            {
                if (other.End <= piece.Start || other.Start >= piece.End)
                {
                    remaining.Add(piece);
                    continue;
                }
                if (other.Start > piece.Start)
                    remaining.Add(piece with { End = other.Start });
                if (other.End < piece.End)
                    remaining.Add(piece with { Start = other.End });
            }
            pieces = remaining;
        }
        return pieces;
    }

    private static float[]? Embed(SpeakerEmbeddingExtractor extractor, AudioClip audio, IEnumerable<TimeRange> ranges, double maxSeconds)
    {
        using OnlineStream stream = extractor.CreateStream();
        double total = 0;
        foreach (TimeRange range in ranges.OrderByDescending(r => r.Duration))
        {
            if (total >= maxSeconds)
                break;
            TimeRange piece = range with { End = Math.Min(range.End, range.Start + maxSeconds - total) };
            float[] samples = Transcriber.Slice(audio, piece).Samples;
            if (samples.Length == 0)
                continue;
            stream.AcceptWaveform(audio.SampleRate, samples);
            total += piece.Duration;
        }
        stream.InputFinished();

        // Compute must not be called before the stream is ready; the model can also return NaN.
        if (!extractor.IsReady(stream))
            return null;
        float[] embedding = extractor.Compute(stream);
        return embedding.Any(float.IsNaN) ? null : embedding;
    }
}
