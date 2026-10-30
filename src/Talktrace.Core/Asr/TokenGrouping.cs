using System.Text;

namespace Talktrace.Core.Asr;

/// <summary>Turns Parakeet's sub-word tokens and their times into whole words.</summary>
internal static class TokenGrouping
{
    // SentencePiece marks the first piece of a word with U+2581; accept a plain space too.
    private const char WordStartMarker = '▁';

    // Parakeet emits one frame every 80 ms, so a token lasts at least that long.
    internal const double MinimumTokenSeconds = 0.08;

    /// <param name="tokens">Tokens in the order the recognizer emitted them.</param>
    /// <param name="starts">Start time of each token, in seconds from the start of the chunk.</param>
    /// <param name="durations">Duration of each token in seconds, or null if the model doesn't report them.</param>
    /// <param name="offsetSeconds">Where the chunk starts in the recording.</param>
    public static IReadOnlyList<Word> ToWords(
        IReadOnlyList<string> tokens,
        IReadOnlyList<float> starts,
        IReadOnlyList<float>? durations,
        double offsetSeconds)
    {
        if (starts.Count != tokens.Count)
            throw new ArgumentException("Every token needs a start time.", nameof(starts));
        if (durations is not null && durations.Count != tokens.Count)
            throw new ArgumentException("Every token needs a duration.", nameof(durations));

        var words = new List<Word>();
        var text = new StringBuilder();
        double wordStart = 0;
        double wordEnd = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            bool startsWord = token.Length > 0 && (token[0] == WordStartMarker || token[0] == ' ');
            string piece = startsWord ? token[1..] : token;

            if (startsWord)
                Flush();

            double start = offsetSeconds + starts[i];
            double length = durations is null ? MinimumTokenSeconds : Math.Max(durations[i], MinimumTokenSeconds);

            if (text.Length == 0)
                wordStart = start;
            text.Append(piece);
            wordEnd = start + length;
        }

        Flush();
        return words;

        void Flush()
        {
            string word = text.ToString().Trim();
            if (word.Length > 0)
            {
                // A token's duration can run past the next word's start; end the earlier word there.
                if (words.Count > 0 && words[^1].End > wordStart)
                    words[^1] = words[^1] with { End = Math.Max(words[^1].Start, wordStart) };
                words.Add(new Word(word, wordStart, wordEnd));
            }
            text.Clear();
        }
    }
}
