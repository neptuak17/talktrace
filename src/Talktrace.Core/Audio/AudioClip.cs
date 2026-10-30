namespace Talktrace.Core.Audio;

/// <summary>Mono audio in the range -1 to 1.</summary>
public sealed record AudioClip(float[] Samples, int SampleRate)
{
    public double DurationSeconds => (double)Samples.Length / SampleRate;
}
