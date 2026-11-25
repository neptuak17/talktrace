using Talktrace.Core;
using Talktrace.Core.Asr;
using Talktrace.Core.Calls;

namespace Talktrace.Tests;

public class EchoRemoverTests
{
    private static readonly TranscribeOptions Options = new();

    /// <summary>Words one every 0.3 s from <paramref name="start"/>, each 0.25 s long.</summary>
    private static Word[] Say(double start, string text) =>
        text.Split(' ').Select((w, i) => new Word(w, start + i * 0.3, start + i * 0.3 + 0.25)).ToArray();

    [Fact]
    public void Drops_a_microphone_phrase_the_call_also_holds()
    {
        Word[] call = Say(10, "Let's look at the budget for next quarter.");
        Word[] mic = Say(10.2, "let's look at the budget for next quarter"); // picked up from the speaker, a little late

        EchoRemoval result = EchoRemover.Remove(mic, call, Options);

        Assert.Empty(result.Kept);
        Assert.Equal(1, result.Phrases);
        Assert.Equal(1, result.DroppedPhrases);
    }

    [Fact]
    public void Keeps_my_own_speech_while_others_talk()
    {
        Word[] call = Say(10, "Let's look at the budget for next quarter.");
        Word[] mic = Say(11, "Sorry, can you share your screen?");

        EchoRemoval result = EchoRemover.Remove(mic, call, Options);

        Assert.Equal(mic, result.Kept);
        Assert.Equal(0, result.DroppedPhrases);
    }

    [Fact]
    public void Keeps_the_same_words_said_well_apart_in_time()
    {
        Word[] call = Say(10, "That sounds good to me.");
        Word[] mic = Say(30, "That sounds good to me.");

        Assert.Equal(mic, EchoRemover.Remove(mic, call, Options).Kept);
    }

    [Fact]
    public void Judges_each_phrase_on_its_own()
    {
        Word[] call = Say(10, "We ship on Friday.");
        Word[] echo = Say(10.1, "We ship on Friday.");
        Word[] mine = Say(13, "Great, thanks everyone."); // after a pause, so a phrase of its own

        EchoRemoval result = EchoRemover.Remove([.. echo, .. mine], call, Options);

        Assert.Equal(mine, result.Kept);
        Assert.Equal(2, result.Phrases);
    }

    [Fact]
    public void Keeps_a_phrase_with_fewer_than_the_matching_share_of_words()
    {
        Word[] call = Say(10, "the plan is fine");
        Word[] mic = Say(10, "the plan needs another week of testing"); // 2 of 7 words match

        Assert.Equal(mic, EchoRemover.Remove(mic, call, Options).Kept);
    }

    [Fact]
    public void Matches_words_ignoring_case_and_punctuation()
    {
        Word[] call = [new("Okay,", 5, 5.2), new("thanks!", 5.3, 5.6)];
        Word[] mic = [new("okay", 5.4, 5.6), new("Thanks.", 5.7, 6.0)];

        Assert.Empty(EchoRemover.Remove(mic, call, Options).Kept);
    }

    [Fact]
    public void Matching_share_and_window_are_tunable()
    {
        Word[] call = Say(10, "the plan is fine");
        Word[] mic = Say(12, "the plan is fine"); // 2 s late: outside the default 1.5 s window

        Assert.Equal(mic, EchoRemover.Remove(mic, call, Options).Kept);
        Assert.Empty(EchoRemover.Remove(mic, call, Options with { EchoWindowSeconds = 2.5 }).Kept);
    }
}
