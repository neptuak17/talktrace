namespace Talktrace.Core.Transcripts;

/// <summary>One paragraph of the transcript: a speaker turn, or with speaker labels off, a stretch between long pauses.</summary>
/// <param name="Speaker">Speaker number from 1 in order of first appearance, or null with speaker labels off.</param>
public sealed record Turn(int? Speaker, double Start, double End, string Text);
