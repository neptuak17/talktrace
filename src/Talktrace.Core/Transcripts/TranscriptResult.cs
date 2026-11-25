using System.Text.Json.Serialization;

namespace Talktrace.Core.Transcripts;

/// <summary>Wall-clock time spent in one pipeline stage.</summary>
public readonly record struct StageTiming(TranscribeStage Stage, TimeSpan Elapsed);

/// <summary>A finished transcription.</summary>
/// <param name="SourceFileName">The recording's file name, without its folder.</param>
/// <param name="SavedPath">Where the transcript was auto-saved, or null if saving failed.</param>
/// <param name="SaveError">Why saving failed, in plain language; null once saved.</param>
/// <param name="YourName">
/// For a call recording, the name my turns carry (speaker <see cref="YourSpeaker"/>); null for any other recording.
/// </param>
public sealed record TranscriptResult(
    string SourceFileName,
    DateTime TranscribedAt,
    TimeSpan Duration,
    string ModelName,
    bool SpeakerLabels,
    IReadOnlyList<Turn> Turns,
    IReadOnlyList<StageTiming> Timings,
    int ChunkCount,
    string? SavedPath,
    string? SaveError = null,
    string? YourName = null)
{
    /// <summary>In a call recording, the speaker number of my turns, from the microphone track. The others count from 1.</summary>
    public const int YourSpeaker = 0;

    /// <summary>
    /// Speaker numbers in order of first appearance, except that in a call recording I come first; empty with speaker
    /// labels off.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<int> Speakers =>
        Turns.Where(t => t.Speaker is not null).Select(t => t.Speaker!.Value).Distinct()
            .OrderBy(s => s == YourSpeaker && YourName is not null ? 0 : 1)
            .ToList();

    /// <summary>The label a speaker's turns start with: my name in a call recording, otherwise Speaker N.</summary>
    public string SpeakerName(int speaker) =>
        speaker == YourSpeaker && YourName is not null ? YourName : TranscriptFormatter.SpeakerName(speaker);

    [JsonIgnore]
    public TimeSpan TotalElapsed => Timings.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Elapsed);

    /// <summary>Processing time divided by audio duration; below 1 is faster than real time.</summary>
    [JsonIgnore]
    public double RealTimeFactor => Duration > TimeSpan.Zero ? TotalElapsed / Duration : 0;
}
