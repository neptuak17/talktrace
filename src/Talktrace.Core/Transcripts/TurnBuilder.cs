using Talktrace.Core.Asr;

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
}
