using Talktrace.Core.Asr;
using Talktrace.Core.Speakers;

namespace Talktrace.Core.Transcripts;

/// <summary>Assembles words into paragraphs (DESIGN.md section 7, step 7).</summary>
internal static class TurnBuilder
{
    /// <summary>With speaker labels off: one paragraph per stretch of speech between pauses longer than <paramref name="pauseSeconds"/>.</summary>
    public static IReadOnlyList<Turn> Paragraphs(IReadOnlyList<Word> words, double pauseSeconds)
    {
        var turns = new List<Turn>();
        var current = new List<Word>();

        foreach (Word word in words.OrderBy(w => w.Start))
        {
            if (current.Count > 0 && word.Start - current[^1].End > pauseSeconds)
                Flush();
            current.Add(word);
        }

        Flush();
        return turns;

        void Flush()
        {
            string text = string.Join(' ', current.Select(w => w.Text.Trim()).Where(t => t.Length > 0));
            if (text.Length > 0)
                turns.Add(new Turn(null, current[0].Start, current[^1].End, text));
            current.Clear();
        }
    }

    /// <summary>
    /// With speaker labels on: one turn per run of words from the same speaker, however long.
    /// Speakers are renumbered from 1 in order of first appearance.
    /// </summary>
    public static IReadOnlyList<Turn> Turns(IReadOnlyList<SpeakerWord> words)
    {
        var turns = new List<Turn>();
        var numbers = new Dictionary<int, int>();
        var current = new List<Word>();
        int speaker = 0;

        foreach (SpeakerWord word in words.Where(w => w.Word.Text.Trim().Length > 0).OrderBy(w => w.Word.Start))
        {
            if (current.Count > 0 && word.Speaker != speaker)
                Flush();
            speaker = word.Speaker;
            current.Add(word.Word);
        }

        Flush();
        return turns;

        void Flush()
        {
            if (current.Count == 0)
                return;
            if (!numbers.TryGetValue(speaker, out int number))
            {
                number = numbers.Count + 1;
                numbers.Add(speaker, number);
            }
            turns.Add(new Turn(number, current[0].Start, current[^1].End, string.Join(' ', current.Select(w => w.Text.Trim()))));
            current.Clear();
        }
    }
}
