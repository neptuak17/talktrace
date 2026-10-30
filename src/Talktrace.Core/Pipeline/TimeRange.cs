namespace Talktrace.Core.Pipeline;

/// <summary>A stretch of the recording, in seconds from its start.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
}
