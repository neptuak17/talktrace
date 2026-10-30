namespace Talktrace.Core.Asr;

/// <summary>A recognised word with its start and end time in seconds from the start of the recording.</summary>
public readonly record struct Word(string Text, double Start, double End);
