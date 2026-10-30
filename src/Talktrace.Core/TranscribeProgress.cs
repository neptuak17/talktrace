namespace Talktrace.Core;

public enum TranscribeStage
{
    ReadingAudio,
    FindingSpeech,
    IdentifyingSpeakers,
    Transcribing,
    Saving,
}

/// <summary>Progress within the current stage: <see cref="Done"/> out of <see cref="Total"/>.</summary>
public readonly record struct TranscribeProgress(TranscribeStage Stage, int Done, int Total);
