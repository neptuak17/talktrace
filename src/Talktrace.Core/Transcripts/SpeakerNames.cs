using System.Text.RegularExpressions;

namespace Talktrace.Core.Transcripts;

/// <summary>
/// Renames speakers in transcript text the user may have edited (DESIGN.md section 10, item 6). A name is replaced only
/// where a paragraph starts, after any timestamp, so edits elsewhere are kept.
/// </summary>
public static class SpeakerNames
{
    private const string SpeakersLabel = "Speakers: ";

    /// <summary>
    /// Replaces <paramref name="oldName"/> with <paramref name="newName"/> at the start of each paragraph of the body, and
    /// rewrites the header's Speakers line as <paramref name="speakers"/>, every speaker's current name in order.
    /// </summary>
    public static string Rename(string text, string oldName, string newName, IReadOnlyList<string> speakers)
    {
        // The header ends at the first blank line. Keeping the two apart means a speaker named "Speakers" or
        // "Transcript" can't be mistaken for a header line.
        Match blank = Regex.Match(text, @"\r?\n\r?\n");
        string header = blank.Success ? text[..blank.Index] : text;
        string body = blank.Success ? text[blank.Index..] : "";

        header = Regex.Replace(
            header, $@"^{Regex.Escape(SpeakersLabel)}[^\r\n]*", _ => SpeakersLabel + string.Join(", ", speakers),
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        body = Regex.Replace(
            body, $@"^(\[\d{{2}}:\d{{2}}:\d{{2}}\] )?{Regex.Escape(oldName)}: ", match => $"{match.Groups[1].Value}{newName}: ",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return header + body;
    }

    /// <summary>Whether <paramref name="name"/> matches another speaker's current name, ignoring case.</summary>
    public static bool IsTaken(string name, IEnumerable<string> otherNames) =>
        otherNames.Any(other => string.Equals(other, name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// The turn to show and play for a speaker in the speakers panel: their first turn with at least
    /// <paramref name="minWords"/> words, or failing that their longest.
    /// </summary>
    public static Turn? SampleTurn(IReadOnlyList<Turn> turns, int speaker, int minWords = 8)
    {
        List<Turn> theirs = turns.Where(t => t.Speaker == speaker).ToList();
        return theirs.FirstOrDefault(t => WordCount(t.Text) >= minWords) ?? theirs.MaxBy(t => WordCount(t.Text));
    }

    /// <summary>
    /// The turn's first sentence, with more sentences added until it has <paramref name="minWords"/> words; at most
    /// <paramref name="maxLength"/> characters, cut at a word with an ellipsis.
    /// </summary>
    public static string SampleSentence(Turn turn, int minWords = 8, int maxLength = 200)
    {
        string[] sentences = Regex.Split(turn.Text.Trim(), @"(?<=[.?!])\s+");
        string sample = "";
        foreach (string sentence in sentences)
        {
            sample = sample.Length == 0 ? sentence : $"{sample} {sentence}";
            if (WordCount(sample) >= minWords)
                break;
        }

        if (sample.Length <= maxLength)
            return sample;
        int cut = sample.LastIndexOf(' ', maxLength);
        return sample[..(cut > 0 ? cut : maxLength)].TrimEnd(',', ';', ':') + "…";
    }

    private static int WordCount(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
