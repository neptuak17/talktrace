using Talktrace.Core.Asr;
using Talktrace.Core.Calls;
using Talktrace.Core.Speakers;
using Talktrace.Core.Transcripts;

namespace Talktrace.Tests;

public class CallTurnBuilderTests
{
    private const double Pause = 0.5;

    private static Word W(string text, double start, double end) => new(text, start, end);

    private static SpeakerWord S(string text, double start, double end, int speaker) => new(new Word(text, start, end), speaker);

    [Fact]
    public void Gives_my_turns_speaker_0_and_numbers_the_others_from_1()
    {
        Word[] mic = [W("Hello", 0, 0.4), W("everyone.", 0.5, 0.9)];
        SpeakerWord[] call = [S("Hi.", 1.5, 1.8, 7), S("Morning.", 2.5, 2.9, 3), S("Hey.", 3.5, 3.8, 7)];

        IReadOnlyList<Turn> turns = CallTurnBuilder.Turns(mic, call, Pause);

        Assert.Equal([0, 1, 2, 1], turns.Select(t => t.Speaker));
        Assert.Equal(new Turn(0, 0, 0.9, "Hello everyone."), turns[0]);
    }

    [Fact]
    public void Numbers_the_others_from_1_even_when_I_speak_later()
    {
        Word[] mic = [W("Thanks.", 5, 5.4)];
        SpeakerWord[] call = [S("Welcome.", 0, 0.5, 2), S("Agreed.", 1, 1.5, 0)];

        Assert.Equal([1, 2, 0], CallTurnBuilder.Turns(mic, call, Pause).Select(t => t.Speaker));
    }

    [Fact]
    public void Keeps_each_persons_phrase_whole_when_we_talk_at_once()
    {
        // They talk from 0 to 2 s without a pause; I say "right" at 1 s, in the middle of their phrase.
        SpeakerWord[] call = [S("so", 0, 0.3, 0), S("the", 0.4, 0.6, 0), S("vendor", 0.7, 1.2, 0), S("said", 1.3, 1.6, 0), S("no", 1.7, 2.0, 0)];
        Word[] mic = [W("right", 1.0, 1.3)];

        IReadOnlyList<Turn> turns = CallTurnBuilder.Turns(mic, call, Pause);

        Assert.Equal(["so the vendor said no", "right"], turns.Select(t => t.Text));
        Assert.Equal([1, 0], turns.Select(t => t.Speaker));
    }

    [Fact]
    public void Joins_one_speakers_phrases_into_one_turn()
    {
        SpeakerWord[] call = [S("First", 0, 0.4, 0), S("point.", 0.5, 0.9, 0), S("Second", 3, 3.4, 0), S("point.", 3.5, 3.9, 0)];

        Assert.Equal(new Turn(1, 0, 3.9, "First point. Second point."), Assert.Single(CallTurnBuilder.Turns([], call, Pause)));
    }

    [Fact]
    public void Splits_a_turn_where_I_speak_between_the_others_phrases()
    {
        SpeakerWord[] call = [S("First.", 0, 0.5, 0), S("Second.", 3, 3.5, 0)];
        Word[] mic = [W("Yes.", 1.5, 1.8)];

        Assert.Equal(["First.", "Yes.", "Second."], CallTurnBuilder.Turns(mic, call, Pause).Select(t => t.Text));
    }

    [Fact]
    public void Returns_no_turns_for_no_words()
    {
        Assert.Empty(CallTurnBuilder.Turns([], [], Pause));
    }

    [Fact]
    public void Without_labels_merges_both_tracks_into_paragraphs_split_at_long_pauses()
    {
        Word[] mic = [W("Hello.", 0, 0.5), W("Bye.", 10, 10.4)];
        Word[] call = [W("Hi", 1, 1.3), W("there.", 1.4, 1.8)];

        IReadOnlyList<Turn> paragraphs = CallTurnBuilder.Paragraphs(mic, call, Pause, paragraphPauseSeconds: 2);

        Assert.Equal([new Turn(null, 0, 1.8, "Hello. Hi there."), new Turn(null, 10, 10.4, "Bye.")], paragraphs);
    }

    [Fact]
    public void Without_labels_keeps_phrases_whole_when_we_talk_at_once()
    {
        Word[] call = [W("so", 0, 0.3), W("the", 0.4, 0.6), W("vendor", 0.7, 1.2), W("said", 1.3, 1.6), W("no", 1.7, 2.0)];
        Word[] mic = [W("right", 1.0, 1.3)];

        Assert.Equal("so the vendor said no right", Assert.Single(CallTurnBuilder.Paragraphs(mic, call, Pause, 2)).Text);
    }
}
