using Talktrace.Core.Models;

namespace Talktrace.Core;

/// <summary>Whether to label speakers, and how many to look for.</summary>
public readonly record struct SpeakerSetting(bool Label, int? Count)
{
    public static readonly SpeakerSetting None = new(false, null);
    public static readonly SpeakerSetting Auto = new(true, null);

    public static SpeakerSetting Exactly(int count) => new(true, count);
}

/// <summary>
/// Everything a transcription job can be told, including every pipeline threshold so they can be
/// tuned in one place (DESIGN.md section 7). Times are in seconds.
/// </summary>
public sealed record TranscribeOptions
{
    public ModelProfile Profile { get; init; } = ModelProfile.Fast;
    public SpeakerSetting Speakers { get; init; } = SpeakerSetting.Auto;
    public bool IncludeTimestamps { get; init; }

    /// <summary>Where to save the transcript. Null means next to the recording, never overwriting a file.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Recognizer threads. 6 was fastest on the Snapdragon X Elite in the Milestone 2 benchmark (4, 6, 8 and 11 tried).</summary>
    public int NumThreads { get; init; } = Math.Min(6, Environment.ProcessorCount);

    // Finding speech
    public float VadThreshold { get; init; } = 0.5f;
    public double VadMinSilenceSeconds { get; init; } = 0.5;
    public double VadMaxSpeechSeconds { get; init; } = 20;
    public double MinSpeechSeconds { get; init; } = 0.3;

    // Building chunks
    public double MaxChunkSeconds { get; init; } = 25;
    public double MaxGapInChunkSeconds { get; init; } = 2;
    public double ChunkPaddingSeconds { get; init; } = 0.15;

    // Identifying speakers
    /// <summary>
    /// sherpa-onnx's clustering threshold (cosine distance), set low so it over-splits; the voice merge
    /// then decides who is who. Lower makes more groups, and each costs memory: on a 98-minute recording,
    /// 1.0 made 27 groups and peaked at 1.8 GB, while 0.9 pushed the whole job to 2.7 GB.
    /// </summary>
    public float SplitThreshold { get; init; } = 1.0f;
    /// <summary>
    /// Two voices whose cosine distance is below this are the same speaker. Smaller finds more speakers,
    /// larger fewer. With a chosen count, voices this close still merge first.
    /// </summary>
    public double ClusteringThreshold { get; init; } = 0.6;
    /// <summary>
    /// A group with less speech than this joins the closest voice rather than counting as a speaker,
    /// unless a chosen count needs it.
    /// </summary>
    public double MinSpeakerSeconds { get; init; } = 15;
    /// <summary>The most speech used for one voice print.</summary>
    public double VoicePrintSeconds { get; init; } = 60;
    public double MinSpeakerSegmentSeconds { get; init; } = 0.3;
    public double SpeakerSegmentMergeSeconds { get; init; } = 0.5;

    // Assigning speakers to words
    public double SpeakerReachSeconds { get; init; } = 1;
    public double ShortRunSeconds { get; init; } = 0.5;
    public double ShortRunPauseSeconds { get; init; } = 0.5;

    // Assembling the transcript
    public double ParagraphPauseSeconds { get; init; } = 2;
}
