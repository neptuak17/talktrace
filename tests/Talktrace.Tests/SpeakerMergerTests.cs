using Talktrace.Core.Speakers;

namespace Talktrace.Tests;

public class SpeakerMergerTests
{
    private const double Threshold = 0.6;
    private const double MinSeconds = 15;

    private static VoiceGroup G(int speaker, double seconds, params float[] embedding) => new(speaker, seconds, embedding);

    [Fact]
    public void Groups_with_the_same_voice_become_one_speaker()
    {
        VoiceGroup[] groups = [G(0, 100, 1, 0, 0), G(1, 50, 0.95f, 0.31f, 0), G(2, 80, 0, 1, 0)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: null, Threshold, MinSeconds);

        Assert.Equal(speakers[0], speakers[1]);
        Assert.NotEqual(speakers[0], speakers[2]);
    }

    [Fact]
    public void A_small_group_joins_the_closest_voice_rather_than_becoming_a_speaker()
    {
        VoiceGroup[] groups = [G(0, 100, 1, 0, 0), G(1, 80, 0, 1, 0), G(2, 3, 0, 0.6f, 0.8f)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: null, Threshold, MinSeconds);

        Assert.Equal(2, speakers.Values.Distinct().Count());
        Assert.Equal(speakers[1], speakers[2]);
    }

    [Fact]
    public void With_a_count_small_groups_merge_before_two_people_who_talk_a_lot()
    {
        // 0 and 1 sound closer to each other than either does to 2, but 2 barely speaks.
        VoiceGroup[] groups = [G(0, 1000, 1, 0, 0), G(1, 900, 0.2f, 0.98f, 0), G(2, 20, 0, 0, 1)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: 2, Threshold, MinSeconds);

        Assert.NotEqual(speakers[0], speakers[1]);
        Assert.Equal(2, speakers.Values.Distinct().Count());
    }

    [Fact]
    public void With_a_count_the_most_distinct_small_group_becomes_a_speaker()
    {
        VoiceGroup[] groups = [G(0, 100, 1, 0, 0), G(1, 100, 0, 1, 0), G(2, 3, 0.9f, 0.43f, 0), G(3, 3, 0, 0, 1)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: 3, Threshold, MinSeconds);

        Assert.Equal(3, speakers.Values.Distinct().Count());
        Assert.Equal(speakers[0], speakers[2]);
        Assert.NotEqual(speakers[3], speakers[0]);
        Assert.NotEqual(speakers[3], speakers[1]);
    }

    [Fact]
    public void A_count_above_the_groups_found_gives_one_speaker_per_group()
    {
        VoiceGroup[] groups = [G(0, 100, 1, 0, 0), G(1, 100, 0, 1, 0)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: 5, Threshold, MinSeconds);

        Assert.Equal(2, speakers.Values.Distinct().Count());
    }

    [Fact]
    public void Voices_this_close_merge_even_below_the_count()
    {
        VoiceGroup[] groups = [G(0, 100, 1, 0, 0), G(1, 100, 0.99f, 0.14f, 0)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: 2, Threshold, MinSeconds);

        Assert.Equal(speakers[0], speakers[1]);
    }

    [Fact]
    public void When_every_group_is_small_the_largest_still_makes_a_speaker()
    {
        VoiceGroup[] groups = [G(0, 3, 1, 0, 0), G(1, 5, 0, 1, 0)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: null, Threshold, MinSeconds);

        Assert.Equal(speakers[1], speakers[0]);
        Assert.Equal(0, speakers[1]);
    }

    [Fact]
    public void Groups_without_a_voice_print_are_left_out()
    {
        VoiceGroup[] groups = [G(0, 100, 1, 0, 0), new(1, 0.2, null)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: null, Threshold, MinSeconds);

        Assert.Equal([0], speakers.Keys);
    }

    [Fact]
    public void No_voice_prints_means_no_speakers()
    {
        Assert.Empty(SpeakerMerger.Merge([new VoiceGroup(0, 1, null)], count: null, Threshold, MinSeconds));
    }

    [Fact]
    public void Numbers_speakers_from_0()
    {
        VoiceGroup[] groups = [G(7, 100, 1, 0, 0), G(9, 100, 0, 1, 0)];

        IReadOnlyDictionary<int, int> speakers = SpeakerMerger.Merge(groups, count: null, Threshold, MinSeconds);

        Assert.Equal([0, 1], speakers.Values.Order());
    }
}
