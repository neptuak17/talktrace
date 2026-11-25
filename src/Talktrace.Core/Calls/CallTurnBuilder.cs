using Talktrace.Core.Asr;
using Talktrace.Core.Speakers;
using Talktrace.Core.Transcripts;

namespace Talktrace.Core.Calls;

/// <summary>
/// Merges a call recording's two tracks into turns (DESIGN.md section 9, How recordings are transcribed). Each track is
/// first split into phrases (one speaker's words between pauses), and phrases are put in order of their start, so
/// that when two people talk at once, each one's phrase stays whole rather than their words interleaving.
/// </summary>
internal static class CallTurnBuilder
{
    private const int Me = -1; // diarization's speaker numbers start at 0

    private sealed record Phrase(int Speaker, List<Word> Words)
    {
        public double Start => Words[0].Start;
        public double End => Words[^1].End;
    }

    /// <summary>
    /// With speaker labels on: my words are speaker <see cref="TranscriptResult.YourSpeaker"/>; the others are renumbered
    /// from 1 in order of first appearance. Consecutive phrases from one speaker form one turn, however long.
    /// </summary>
    public static IReadOnlyList<Turn> Turns(
        IReadOnlyList<Word> micWords, IReadOnlyList<SpeakerWord> systemWords, double phrasePauseSeconds)
    {
        var turns = new List<Turn>();
        var numbers = new Dictionary<int, int> { [Me] = TranscriptResult.YourSpeaker };
        Phrase? current = null;

        foreach (Phrase phrase in Ordered(micWords, systemWords, phrasePauseSeconds))
        {
            if (current is not null && current.Speaker == phrase.Speaker)
            {
                current.Words.AddRange(phrase.Words);
                continue;
            }
            Flush();
            current = phrase with { Words = [.. phrase.Words] };
        }

        Flush();
        return turns;

        void Flush()
        {
            if (current is null)
                return;
            if (!numbers.TryGetValue(current.Speaker, out int number))
            {
                number = numbers.Count; // Me holds 0, so the others count from 1
                numbers.Add(current.Speaker, number);
            }
            turns.Add(new Turn(number, current.Start, current.End, Text(current.Words)));
        }
    }

    /// <summary>
    /// With speaker labels off: phrases from both tracks in order, with a new paragraph after a pause longer than
    /// <paramref name="paragraphPauseSeconds"/> in which nobody speaks.
    /// </summary>
    public static IReadOnlyList<Turn> Paragraphs(
        IReadOnlyList<Word> micWords, IReadOnlyList<Word> systemWords, double phrasePauseSeconds, double paragraphPauseSeconds)
    {
        var turns = new List<Turn>();
        var words = new List<Word>();
        double start = 0, end = 0;

        foreach (Phrase phrase in Ordered(micWords, systemWords.Select(w => new SpeakerWord(w, 0)).ToList(), phrasePauseSeconds))
        {
            if (words.Count > 0 && phrase.Start - end > paragraphPauseSeconds)
                Flush();
            if (words.Count == 0)
                start = phrase.Start;
            words.AddRange(phrase.Words);
            end = Math.Max(end, phrase.End);
        }

        Flush();
        return turns;

        void Flush()
        {
            if (words.Count > 0)
                turns.Add(new Turn(null, start, end, Text(words)));
            words.Clear();
        }
    }

    private static IEnumerable<Phrase> Ordered(
        IReadOnlyList<Word> micWords, IReadOnlyList<SpeakerWord> systemWords, double pauseSeconds)
    {
        IEnumerable<SpeakerWord> mine = micWords.Select(w => new SpeakerWord(w, Me));
        return Phrases(mine, pauseSeconds)
            .Concat(Phrases(systemWords, pauseSeconds))
            .OrderBy(p => p.Start)
            .ThenBy(p => p.Speaker == Me ? 1 : 0); // the call first if both start together
    }

    /// <summary>One track's words as phrases: a new phrase at each change of speaker or pause of <paramref name="pauseSeconds"/>.</summary>
    private static List<Phrase> Phrases(IEnumerable<SpeakerWord> words, double pauseSeconds)
    {
        var phrases = new List<Phrase>();
        foreach (SpeakerWord word in words.Where(w => w.Word.Text.Trim().Length > 0).OrderBy(w => w.Word.Start))
        {
            Phrase? last = phrases.Count > 0 ? phrases[^1] : null;
            if (last is not null && last.Speaker == word.Speaker && word.Word.Start - last.End < pauseSeconds)
                last.Words.Add(word.Word);
            else
                phrases.Add(new Phrase(word.Speaker, [word.Word]));
        }
        return phrases;
    }

    private static string Text(IEnumerable<Word> words) => string.Join(' ', words.Select(w => w.Text.Trim()));
}
