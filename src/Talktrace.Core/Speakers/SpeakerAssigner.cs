using Talktrace.Core.Asr;

namespace Talktrace.Core.Speakers;

/// <summary>A recognised word and the speaker it was given.</summary>
/// <param name="Speaker">The speaker's cluster number from <see cref="SpeakerSegment.Speaker"/>.</param>
public readonly record struct SpeakerWord(Word Word, int Speaker);

/// <summary>Gives each word a speaker from the diarization segments (DESIGN.md section 7, step 6).</summary>
internal static class SpeakerAssigner
{
    /// <summary>
    /// Each word takes the speaker whose segments overlap it most. A word in a gap between segments
    /// takes the nearest segment within <paramref name="reachSeconds"/>, otherwise the previous word's
    /// speaker. With no segments at all, every word gets speaker 0.
    /// </summary>
    /// <returns>The words in time order.</returns>
    public static IReadOnlyList<SpeakerWord> Assign(
        IReadOnlyList<Word> words, IReadOnlyList<SpeakerSegment> segments, double reachSeconds)
    {
        List<Word> ordered = words.OrderBy(w => w.Start).ToList();
        List<SpeakerSegment> sorted = segments.OrderBy(s => s.Start).ToList();
        int?[] found = ordered.Select(w => MostOverlap(w, sorted) ?? Nearest(w, sorted, reachSeconds)).ToArray();

        // Leading words with nothing nearby take the first speaker found.
        int speaker = found.FirstOrDefault(s => s.HasValue) ?? 0;
        var result = new List<SpeakerWord>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            speaker = found[i] ?? speaker;
            result.Add(new SpeakerWord(ordered[i], speaker));
        }
        return result;
    }

    /// <summary>
    /// Gives a run of words shorter than <paramref name="maxRunSeconds"/> to the speaker on both sides
    /// of it, unless a pause of at least <paramref name="pauseSeconds"/> separates it from either side.
    /// Such a run is usually a diarization slip at a boundary, not a real interjection.
    /// </summary>
    /// <param name="words">Words in time order, as <see cref="Assign"/> returns them.</param>
    public static IReadOnlyList<SpeakerWord> Smooth(IReadOnlyList<SpeakerWord> words, double maxRunSeconds, double pauseSeconds)
    {
        SpeakerWord[] result = words.ToArray();
        int first = 0;
        while (first < result.Length)
        {
            int last = first;
            while (last + 1 < result.Length && result[last + 1].Speaker == result[first].Speaker)
                last++;

            if (first > 0 && last + 1 < result.Length)
            {
                SpeakerWord before = result[first - 1];
                SpeakerWord after = result[last + 1];
                bool surrounded = before.Speaker == after.Speaker;
                bool isShort = result[last].Word.End - result[first].Word.Start < maxRunSeconds;
                bool noPause = result[first].Word.Start - before.Word.End < pauseSeconds
                               && after.Word.Start - result[last].Word.End < pauseSeconds;
                if (surrounded && isShort && noPause)
                {
                    for (int i = first; i <= last; i++)
                        result[i] = result[i] with { Speaker = before.Speaker };
                }
            }

            first = last + 1;
        }
        return result;
    }

    /// <summary>The speaker whose segments overlap the word for longest, or null if none overlaps it.</summary>
    private static int? MostOverlap(Word word, List<SpeakerSegment> segments)
    {
        var overlap = new Dictionary<int, double>();
        foreach (SpeakerSegment segment in segments)
        {
            if (segment.Start >= word.End)
                break;
            double seconds = Math.Min(word.End, segment.End) - Math.Max(word.Start, segment.Start);
            if (seconds > 0)
                overlap[segment.Speaker] = overlap.GetValueOrDefault(segment.Speaker) + seconds;
        }
        return overlap.Count == 0 ? null : overlap.MaxBy(p => p.Value).Key;
    }

    /// <summary>The speaker of the segment nearest the word, if it is within <paramref name="reachSeconds"/>.</summary>
    private static int? Nearest(Word word, List<SpeakerSegment> segments, double reachSeconds)
    {
        int? speaker = null;
        double best = double.MaxValue;
        foreach (SpeakerSegment segment in segments)
        {
            double gap = Math.Max(0, Math.Max(segment.Start - word.End, word.Start - segment.End));
            if (gap < best)
                (best, speaker) = (gap, segment.Speaker);
        }
        return best <= reachSeconds ? speaker : null;
    }
}
