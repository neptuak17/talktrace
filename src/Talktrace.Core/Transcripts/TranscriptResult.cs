using System.Text.Json.Serialization;

namespace Talktrace.Core.Transcripts;

/// <summary>Wall-clock time spent in one pipeline stage.</summary>
public readonly record struct StageTiming(TranscribeStage Stage, TimeSpan Elapsed);

/// <summary>A finished transcription.</summary>
/// <param name="SourceFileName">The recording's file name, without its folder.</param>
/// <param name="SavedPath">Where the transcript was auto-saved.</param>
public sealed record TranscriptResult(
    string SourceFileName,
    DateTime TranscribedAt,
    TimeSpan Duration,
    string ModelName,
    bool SpeakerLabels,
    IReadOnlyList<Turn> Turns,
    IReadOnlyList<StageTiming> Timings,
    int ChunkCount,
    string SavedPath)
{
    /// <summary>Speaker numbers in order of first appearance; empty with speaker labels off.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> Speakers => Turns.Where(t => t.Speaker is not null).Select(t => t.Speaker!.Value).Distinct().ToList();

    [JsonIgnore]
    public TimeSpan TotalElapsed => Timings.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Elapsed);

    /// <summary>Processing time divided by audio duration; below 1 is faster than real time.</summary>
    [JsonIgnore]
    public double RealTimeFactor => Duration > TimeSpan.Zero ? TotalElapsed / Duration : 0;
}
