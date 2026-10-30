namespace Talktrace.Core.Pipeline;

/// <summary>
/// Turns speech regions into chunks for the recognizer (DESIGN.md section 7, step 3): drop very
/// short regions, split long ones at their quietest point, pack the rest into chunks of at most
/// about 25 s, and pad each chunk without overlapping its neighbours.
/// </summary>
internal static class Chunker
{
    private const double EnergyFrameSeconds = 0.01;
    private const int EnergySmoothingFrames = 10; // 100 ms

    public static IReadOnlyList<TimeRange> Build(
        IReadOnlyList<TimeRange> speech, float[] samples, int sampleRate, TranscribeOptions options)
    {
        double duration = (double)samples.Length / sampleRate;

        var pieces = new List<TimeRange>();
        foreach (TimeRange region in speech.OrderBy(r => r.Start))
        {
            if (region.Duration < options.MinSpeechSeconds)
                continue;
            SplitLongRegion(region, samples, sampleRate, options.MaxChunkSeconds, pieces);
        }

        var packed = new List<TimeRange>();
        foreach (TimeRange piece in pieces)
        {
            if (packed.Count > 0)
            {
                TimeRange current = packed[^1];
                bool fits = piece.End - current.Start <= options.MaxChunkSeconds;
                bool close = piece.Start - current.End <= options.MaxGapInChunkSeconds;
                if (fits && close)
                {
                    packed[^1] = current with { End = Math.Max(current.End, piece.End) };
                    continue;
                }
            }
            packed.Add(piece);
        }

        var padded = new List<TimeRange>(packed.Count);
        for (int i = 0; i < packed.Count; i++)
        {
            double before = i == 0 ? packed[i].Start : (packed[i].Start - packed[i - 1].End) / 2;
            double after = i == packed.Count - 1 ? duration - packed[i].End : (packed[i + 1].Start - packed[i].End) / 2;
            double start = packed[i].Start - Math.Clamp(before, 0, options.ChunkPaddingSeconds);
            double end = packed[i].End + Math.Clamp(after, 0, options.ChunkPaddingSeconds);
            padded.Add(new TimeRange(Math.Max(0, start), Math.Min(duration, end)));
        }

        return padded;
    }

    /// <summary>Splits a region longer than <paramref name="maxSeconds"/> at quiet points, so each piece is between half and all of it.</summary>
    private static void SplitLongRegion(TimeRange region, float[] samples, int sampleRate, double maxSeconds, List<TimeRange> pieces)
    {
        TimeRange rest = region;
        while (rest.Duration > maxSeconds)
        {
            double split = QuietestPoint(samples, sampleRate, rest.Start + maxSeconds / 2, rest.Start + maxSeconds);
            pieces.Add(rest with { End = split });
            rest = rest with { Start = split };
        }
        pieces.Add(rest);
    }

    /// <summary>The centre of the quietest 100 ms between two times.</summary>
    internal static double QuietestPoint(float[] samples, int sampleRate, double from, double to)
    {
        int frame = Math.Max(1, (int)(sampleRate * EnergyFrameSeconds));
        int firstFrame = (int)(from * sampleRate) / frame;
        int lastFrame = Math.Min((int)(to * sampleRate) / frame, samples.Length / frame) - 1;
        if (lastFrame - firstFrame < EnergySmoothingFrames)
            return (from + to) / 2;

        var energy = new double[lastFrame - firstFrame + 1];
        for (int f = 0; f < energy.Length; f++)
        {
            int offset = (firstFrame + f) * frame;
            double sum = 0;
            for (int i = 0; i < frame; i++)
                sum += samples[offset + i] * samples[offset + i];
            energy[f] = sum;
        }

        double window = 0;
        for (int f = 0; f < EnergySmoothingFrames; f++)
            window += energy[f];

        double quietest = window;
        int quietestFrame = 0;
        for (int f = EnergySmoothingFrames; f < energy.Length; f++)
        {
            window += energy[f] - energy[f - EnergySmoothingFrames];
            if (window < quietest)
            {
                quietest = window;
                quietestFrame = f - EnergySmoothingFrames + 1;
            }
        }

        int centreSample = (firstFrame + quietestFrame) * frame + frame * EnergySmoothingFrames / 2;
        return (double)centreSample / sampleRate;
    }
}
