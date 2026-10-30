namespace Talktrace.Core.Speakers;

/// <summary>A stretch of the recording that speaker identification gave to one speaker, in seconds from its start.</summary>
/// <param name="Speaker">The speaker's cluster number from 0, in no particular order.</param>
public readonly record struct SpeakerSegment(double Start, double End, int Speaker);
