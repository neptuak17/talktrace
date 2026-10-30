using Talktrace.Core.Asr;
using Talktrace.Core.Transcripts;

namespace Talktrace.Tests;

public class TurnBuilderTests
{
    [Fact]
    public void Starts_a_new_paragraph_after_a_pause_longer_than_the_limit()
    {
        Word[] words = [new("Okay,", 0.0, 0.5), new("thanks.", 0.6, 1.0), new("Next", 3.5, 3.8), new("item.", 3.9, 4.2)];

        IReadOnlyList<Turn> turns = TurnBuilder.Paragraphs(words, pauseSeconds: 2);

        Assert.Equal(2, turns.Count);
        Assert.Equal(new Turn(null, 0.0, 1.0, "Okay, thanks."), turns[0]);
        Assert.Equal(new Turn(null, 3.5, 4.2, "Next item."), turns[1]);
    }

    [Fact]
    public void Keeps_shorter_pauses_in_one_paragraph()
    {
        Word[] words = [new("One", 0, 0.3), new("two", 2.0, 2.3)];

        IReadOnlyList<Turn> turns = TurnBuilder.Paragraphs(words, pauseSeconds: 2);

        Assert.Equal("One two", Assert.Single(turns).Text);
    }

    [Fact]
    public void Never_splits_a_paragraph_on_length_alone()
    {
        Word[] words = Enumerable.Range(0, 500).Select(i => new Word("word.", i * 0.4, i * 0.4 + 0.3)).ToArray();

        Assert.Single(TurnBuilder.Paragraphs(words, pauseSeconds: 2));
    }

    [Fact]
    public void Returns_no_paragraphs_for_no_words()
    {
        Assert.Empty(TurnBuilder.Paragraphs([], pauseSeconds: 2));
    }
}
