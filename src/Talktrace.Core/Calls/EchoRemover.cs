using Talktrace.Core.Asr;

namespace Talktrace.Core.Calls;

/// <summary>What echo removal kept, and how much it dropped (numbers only, for the log).</summary>
public sealed record EchoRemoval(IReadOnlyList<Word> Kept, int Phrases, int DroppedPhrases);

/// <summary>
/// Removes the other participants' voices that the microphone picked up from the speaker (DESIGN.md section 9, How
/// recordings are transcribed). A microphone phrase (words between pauses) is dropped when most of its words also
/// appear in the system-audio transcript at about the same time. My own speech while others talk is kept, because its
/// words don't match theirs.
/// </summary>
internal static class EchoRemover
{
    /// <param name="micWords">The microphone track's words, in time order.</param>
    /// <param name="systemWords">The system-audio track's words.</param>
    public static EchoRemoval Remove(IReadOnlyList<Word> micWords, IReadOnlyList<Word> systemWords, TranscribeOptions options)
    {
        // Each spelling's start times on the system track, sorted, for a quick search near each microphone word.
        Dictionary<string, List<double>> heard = systemWords
            .Select(w => (Text: Normalize(w.Text), w.Start))
            .Where(w => w.Text.Length > 0)
            .GroupBy(w => w.Text)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Start).Order().ToList());

        var kept = new List<Word>(micWords.Count);
        int phrases = 0, dropped = 0;
        foreach (List<Word> phrase in Phrases(micWords, options.PhrasePauseSeconds))
        {
            phrases++;
            int counted = 0, matched = 0;
            foreach (Word word in phrase)
            {
                string text = Normalize(word.Text);
                if (text.Length == 0)
                    continue;
                counted++;
                if (heard.TryGetValue(text, out List<double>? starts) && HeardNear(starts, word.Start, options.EchoWindowSeconds))
                    matched++;
            }

            if (counted > 0 && matched >= options.EchoMatchFraction * counted)
                dropped++;
            else
                kept.AddRange(phrase);
        }

        return new EchoRemoval(kept, phrases, dropped);
    }

    /// <summary>Splits words into runs with no pause of <paramref name="pauseSeconds"/> or more inside them.</summary>
    internal static IEnumerable<List<Word>> Phrases(IReadOnlyList<Word> words, double pauseSeconds)
    {
        var phrase = new List<Word>();
        foreach (Word word in words.OrderBy(w => w.Start))
        {
            if (phrase.Count > 0 && word.Start - phrase[^1].End >= pauseSeconds)
            {
                yield return phrase;
                phrase = [];
            }
            phrase.Add(word);
        }
        if (phrase.Count > 0)
            yield return phrase;
    }

    /// <summary>Lower case, letters and digits only, so "Okay," matches "okay".</summary>
    internal static string Normalize(string text) =>
        new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool HeardNear(List<double> starts, double start, double windowSeconds)
    {
        int index = starts.BinarySearch(start - windowSeconds);
        if (index < 0)
            index = ~index;
        return index < starts.Count && starts[index] <= start + windowSeconds;
    }
}
