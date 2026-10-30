using Talktrace.Core.Asr;
using Talktrace.Core.Speakers;
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

    private static SpeakerWord W(string text, double start, double end, int speaker) => new(new Word(text, start, end), speaker);

    [Fact]
    public void Consecutive_words_from_one_speaker_form_one_turn()
    {
        SpeakerWord[] words = [W("Okay,", 0, 0.5, 4), W("thanks.", 0.6, 1.0, 4), W("Sure.", 1.2, 1.5, 7), W("Right.", 1.7, 2.0, 4)];

        IReadOnlyList<Turn> turns = TurnBuilder.Turns(words);

        Assert.Equal(
            [new Turn(1, 0, 1.0, "Okay, thanks."), new Turn(2, 1.2, 1.5, "Sure."), new Turn(1, 1.7, 2.0, "Right.")],
            turns);
    }

    [Fact]
    public void Numbers_speakers_in_order_of_first_appearance()
    {
        SpeakerWord[] words = [W("a", 0, 0.2, 2), W("b", 0.3, 0.5, 0), W("c", 0.6, 0.8, 1), W("d", 0.9, 1.0, 0)];

        Assert.Equal([1, 2, 3, 2], TurnBuilder.Turns(words).Select(t => t.Speaker));
    }

    [Fact]
    public void Keeps_one_speakers_turn_whole_however_long()
    {
        SpeakerWord[] words = Enumerable.Range(0, 500).Select(i => W("word.", i * 0.4, i * 0.4 + 0.3, 0)).ToArray();

        Assert.Single(TurnBuilder.Turns(words));
    }

    [Fact]
    public void Drops_empty_words_without_splitting_a_turn()
    {
        SpeakerWord[] words = [W("one", 0, 0.2, 0), W(" ", 0.3, 0.4, 1), W("two", 0.5, 0.7, 0)];

        Assert.Equal(new Turn(1, 0, 0.7, "one two"), Assert.Single(TurnBuilder.Turns(words)));
    }

    [Fact]
    public void Returns_no_turns_for_no_words()
    {
        Assert.Empty(TurnBuilder.Turns([]));
    }
}
