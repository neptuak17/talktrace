using Talktrace.Core.Asr;

namespace Talktrace.Tests;

public class TokenGroupingTests
{
    [Fact]
    public void Joins_word_pieces_and_uses_token_durations()
    {
        string[] tokens = ["▁Hel", "lo", "▁there"];
        float[] starts = [0.24f, 0.40f, 0.72f];
        float[] durations = [0.16f, 0.16f, 0.32f];

        IReadOnlyList<Word> words = TokenGrouping.ToWords(tokens, starts, durations, offsetSeconds: 0);

        Assert.Equal(2, words.Count);
        AssertWord(words[0], "Hello", 0.24, 0.56);
        AssertWord(words[1], "there", 0.72, 1.04);
    }

    [Fact]
    public void Treats_a_leading_space_as_the_start_of_a_word()
    {
        IReadOnlyList<Word> words = TokenGrouping.ToWords([" Good", " morning"], [0f, 0.5f], null, 0);

        Assert.Equal(new[] { "Good", "morning" }, words.Select(w => w.Text));
    }

    [Fact]
    public void Attaches_punctuation_to_the_previous_word()
    {
        IReadOnlyList<Word> words = TokenGrouping.ToWords(["▁Well", ",", "▁yes", "."], [0f, 0.3f, 0.5f, 0.8f], null, 0);

        Assert.Equal(new[] { "Well,", "yes." }, words.Select(w => w.Text));
    }

    [Fact]
    public void Adds_the_chunk_offset_to_every_time()
    {
        IReadOnlyList<Word> words = TokenGrouping.ToWords(["▁Budget"], [1.0f], [0.4f], offsetSeconds: 120);

        AssertWord(words[0], "Budget", 121.0, 121.4);
    }

    [Fact]
    public void Gives_tokens_without_a_duration_the_minimum_length()
    {
        IReadOnlyList<Word> words = TokenGrouping.ToWords(["▁Hi"], [2.0f], durations: null, 0);

        AssertWord(words[0], "Hi", 2.0, 2.0 + TokenGrouping.MinimumTokenSeconds);
    }

    [Fact]
    public void Ends_a_word_where_the_next_one_starts_when_they_overlap()
    {
        IReadOnlyList<Word> words = TokenGrouping.ToWords(["▁don't", "▁wish"], [0.88f, 1.04f], [0.24f, 0.24f], 0);

        AssertWord(words[0], "don't", 0.88, 1.04);
        AssertWord(words[1], "wish", 1.04, 1.28);
    }

    [Fact]
    public void Returns_no_words_for_no_tokens()
    {
        Assert.Empty(TokenGrouping.ToWords([], [], [], 0));
    }

    private static void AssertWord(Word word, string text, double start, double end)
    {
        Assert.Equal(text, word.Text);
        Assert.Equal(start, word.Start, precision: 3);
        Assert.Equal(end, word.End, precision: 3);
    }
}
