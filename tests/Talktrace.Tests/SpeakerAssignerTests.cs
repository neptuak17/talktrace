using Talktrace.Core.Asr;
using Talktrace.Core.Speakers;

namespace Talktrace.Tests;

public class SpeakerAssignerTests
{
    private const double Reach = 1;

    private static int[] Speakers(IReadOnlyList<SpeakerWord> words) => words.Select(w => w.Speaker).ToArray();

    [Fact]
    public void A_word_takes_the_speaker_that_overlaps_it_most()
    {
        SpeakerSegment[] segments = [new(0, 1.2, 0), new(1.2, 3, 1)];
        Word[] words = [new("across", 1.0, 1.6)];

        Assert.Equal([1], Speakers(SpeakerAssigner.Assign(words, segments, Reach)));
    }

    [Fact]
    public void Overlap_from_several_segments_of_one_speaker_adds_up()
    {
        SpeakerSegment[] segments = [new(0, 1.1, 0), new(1.1, 1.35, 1), new(1.35, 3, 0)];
        Word[] words = [new("word", 1.0, 1.6)];

        Assert.Equal([0], Speakers(SpeakerAssigner.Assign(words, segments, Reach)));
    }

    [Fact]
    public void Overlapping_speakers_go_to_the_one_covering_more_of_the_word()
    {
        SpeakerSegment[] segments = [new(0, 5, 0), new(2.1, 2.3, 1)];
        Word[] words = [new("word", 2.0, 2.5)];

        Assert.Equal([0], Speakers(SpeakerAssigner.Assign(words, segments, Reach)));
    }

    [Fact]
    public void A_word_in_a_gap_takes_the_nearest_segment_within_reach()
    {
        SpeakerSegment[] segments = [new(0, 1, 0), new(3, 4, 1)];
        Word[] words = [new("early", 1.2, 1.5), new("late", 2.4, 2.8)];

        Assert.Equal([0, 1], Speakers(SpeakerAssigner.Assign(words, segments, Reach)));
    }

    [Fact]
    public void A_word_out_of_reach_takes_the_previous_words_speaker()
    {
        SpeakerSegment[] segments = [new(0, 1, 0), new(10, 11, 1)];
        Word[] words = [new("in", 0.2, 0.5), new("far", 5, 5.3), new("next", 10.2, 10.5)];

        Assert.Equal([0, 0, 1], Speakers(SpeakerAssigner.Assign(words, segments, Reach)));
    }

    [Fact]
    public void Leading_words_out_of_reach_take_the_first_speaker_found()
    {
        SpeakerSegment[] segments = [new(10, 11, 3)];
        Word[] words = [new("early", 0, 0.3), new("in", 10.2, 10.5)];

        Assert.Equal([3, 3], Speakers(SpeakerAssigner.Assign(words, segments, Reach)));
    }

    [Fact]
    public void Every_word_gets_speaker_0_when_no_speakers_were_found()
    {
        Word[] words = [new("one", 0, 0.3), new("two", 0.4, 0.7)];

        Assert.Equal([0, 0], Speakers(SpeakerAssigner.Assign(words, [], Reach)));
    }

    [Fact]
    public void Returns_words_in_time_order()
    {
        Word[] words = [new("second", 2, 2.3), new("first", 0, 0.3)];

        IReadOnlyList<SpeakerWord> assigned = SpeakerAssigner.Assign(words, [new(0, 3, 0)], Reach);

        Assert.Equal(["first", "second"], assigned.Select(w => w.Word.Text));
    }

    private static SpeakerWord W(int speaker, double start, double end) => new(new Word("w", start, end), speaker);

    [Fact]
    public void Smoothing_gives_a_short_run_inside_another_speakers_run_to_that_speaker()
    {
        SpeakerWord[] words = [W(0, 0, 0.4), W(0, 0.5, 0.9), W(1, 1.0, 1.3), W(0, 1.4, 1.8), W(0, 1.9, 2.2)];

        Assert.Equal([0, 0, 0, 0, 0], Speakers(SpeakerAssigner.Smooth(words, 0.5, 0.5)));
    }

    [Fact]
    public void Smoothing_keeps_a_short_run_after_a_pause()
    {
        SpeakerWord[] words = [W(0, 0, 0.4), W(1, 1.0, 1.3), W(0, 1.4, 1.8)];

        Assert.Equal([0, 1, 0], Speakers(SpeakerAssigner.Smooth(words, 0.5, 0.5)));
    }

    [Fact]
    public void Smoothing_keeps_a_short_run_before_a_pause()
    {
        SpeakerWord[] words = [W(0, 0, 0.4), W(1, 0.5, 0.8), W(0, 1.4, 1.8)];

        Assert.Equal([0, 1, 0], Speakers(SpeakerAssigner.Smooth(words, 0.5, 0.5)));
    }

    [Fact]
    public void Smoothing_keeps_a_run_that_is_long_enough()
    {
        SpeakerWord[] words = [W(0, 0, 0.4), W(1, 0.5, 0.8), W(1, 0.9, 1.2), W(0, 1.3, 1.6)];

        Assert.Equal([0, 1, 1, 0], Speakers(SpeakerAssigner.Smooth(words, 0.5, 0.5)));
    }

    [Fact]
    public void Smoothing_keeps_a_short_run_between_two_different_speakers()
    {
        SpeakerWord[] words = [W(0, 0, 0.4), W(1, 0.5, 0.8), W(2, 0.9, 1.2)];

        Assert.Equal([0, 1, 2], Speakers(SpeakerAssigner.Smooth(words, 0.5, 0.5)));
    }

    [Fact]
    public void Smoothing_keeps_short_runs_at_the_start_and_end()
    {
        SpeakerWord[] words = [W(1, 0, 0.2), W(0, 0.3, 0.6), W(0, 0.7, 1.4), W(2, 1.5, 1.7)];

        Assert.Equal([1, 0, 0, 2], Speakers(SpeakerAssigner.Smooth(words, 0.5, 0.5)));
    }

    [Fact]
    public void Smoothing_does_not_swap_neighbouring_short_runs()
    {
        // The short run of 1 joins speaker 0; the short run of 0 after it is then part of 0's run.
        SpeakerWord[] words = [W(0, 0, 1), W(1, 1.1, 1.3), W(0, 1.4, 1.6), W(1, 1.7, 3)];

        Assert.Equal([0, 0, 0, 1], Speakers(SpeakerAssigner.Smooth(words, 0.5, 0.5)));
    }
}
