using Talktrace.Core.Transcripts;

namespace Talktrace.Tests;

public class SpeakerNamesTests
{
    private const string Transcript =
        "Transcript: Q3 planning session.m4a\r\n" +
        "Transcribed: 2026-10-29 · Duration: 52:14 · Model: Fast (English)\r\n" +
        "Speakers: Speaker 1, Speaker 2\r\n" +
        "\r\n" +
        "Speaker 1: Okay, let's get started. Speaker 2: is next.\r\n" +
        "\r\n" +
        "Speaker 2: Sounds good.\r\n" +
        "\r\n" +
        "Speaker 1: Go ahead.\r\n";

    [Fact]
    public void Renames_a_speaker_at_the_start_of_each_turn_and_in_the_speakers_line()
    {
        string text = SpeakerNames.Rename(Transcript, "Speaker 1", "Dana", ["Dana", "Speaker 2"]);

        Assert.Equal(
            "Transcript: Q3 planning session.m4a\r\n" +
            "Transcribed: 2026-10-29 · Duration: 52:14 · Model: Fast (English)\r\n" +
            "Speakers: Dana, Speaker 2\r\n" +
            "\r\n" +
            "Dana: Okay, let's get started. Speaker 2: is next.\r\n" +
            "\r\n" +
            "Speaker 2: Sounds good.\r\n" +
            "\r\n" +
            "Dana: Go ahead.\r\n",
            text);
    }

    [Fact]
    public void Keeps_edits_made_to_the_text()
    {
        string edited = Transcript.Replace("Sounds good.", "Sounds very good.");

        string text = SpeakerNames.Rename(edited, "Speaker 2", "Raj", ["Speaker 1", "Raj"]);

        Assert.Contains("\r\nRaj: Sounds very good.\r\n", text);
    }

    [Fact]
    public void Renames_after_a_timestamp()
    {
        string text = SpeakerNames.Rename(
            "Transcript: a.m4a\r\nSpeakers: Speaker 1\r\n\r\n[00:03:12] Speaker 1: Hello.\r\n", "Speaker 1", "Dana", ["Dana"]);

        Assert.Equal("Transcript: a.m4a\r\nSpeakers: Dana\r\n\r\n[00:03:12] Dana: Hello.\r\n", text);
    }

    [Fact]
    public void Renames_again_from_the_current_name()
    {
        string once = SpeakerNames.Rename(Transcript, "Speaker 1", "Dana", ["Dana", "Speaker 2"]);

        string twice = SpeakerNames.Rename(once, "Dana", "Dana Smith", ["Dana Smith", "Speaker 2"]);

        Assert.Equal(Transcript.Replace("Speaker 1", "Dana Smith"), twice);
    }

    [Fact]
    public void Never_mistakes_a_header_line_for_a_turn()
    {
        string named = SpeakerNames.Rename(Transcript, "Speaker 1", "Speakers", ["Speakers", "Speaker 2"]);

        string renamed = SpeakerNames.Rename(named, "Speakers", "Dana", ["Dana", "Speaker 2"]);

        Assert.Equal(SpeakerNames.Rename(Transcript, "Speaker 1", "Dana", ["Dana", "Speaker 2"]), renamed);
    }

    [Fact]
    public void Takes_names_literally()
    {
        string text = SpeakerNames.Rename(Transcript, "Speaker 1", "$1 (A.B.)", ["$1 (A.B.)", "Speaker 2"]);

        Assert.Contains("\r\n$1 (A.B.): Go ahead.\r\n", text);
        Assert.Contains("\r\nSpeakers: $1 (A.B.), Speaker 2\r\n", text);
    }

    [Fact]
    public void A_name_is_taken_when_another_speaker_has_it_in_any_case()
    {
        Assert.True(SpeakerNames.IsTaken("dana", ["Dana", "Speaker 2"]));
        Assert.True(SpeakerNames.IsTaken("Speaker 2", ["Dana", "Speaker 2"]));
        Assert.False(SpeakerNames.IsTaken("Raj", ["Dana", "Speaker 2"]));
    }

    [Fact]
    public void The_sample_turn_is_the_first_with_enough_words_or_else_the_longest()
    {
        Turn[] turns =
        [
            new(1, 0, 1, "Yeah."),
            new(2, 1, 9, "One two three four five six seven eight."),
            new(1, 9, 12, "Right, that makes sense to me."),
            new(1, 12, 20, "So the plan is that we ship the first part next week."),
            new(1, 20, 30, "And then a much longer turn after that one, with many more words in it than the others."),
        ];

        Assert.Same(turns[3], SpeakerNames.SampleTurn(turns, 1));
        Assert.Same(turns[1], SpeakerNames.SampleTurn(turns, 2));
        Assert.Same(turns[2], SpeakerNames.SampleTurn(turns[..3], 1));
        Assert.Null(SpeakerNames.SampleTurn(turns, 3));
    }

    [Fact]
    public void The_sample_sentence_adds_sentences_until_it_has_enough_words()
    {
        Assert.Equal(
            "Okay. Let's get started with the budget today.",
            SpeakerNames.SampleSentence(new Turn(1, 0, 9, "Okay. Let's get started with the budget today. Then the timeline.")));
        Assert.Equal(
            "So the plan is that we ship next week.",
            SpeakerNames.SampleSentence(new Turn(1, 0, 9, "So the plan is that we ship next week. Then the rest.")));
    }

    [Fact]
    public void A_long_sample_sentence_is_cut_at_a_word()
    {
        string sample = SpeakerNames.SampleSentence(new Turn(1, 0, 9, "alpha beta gamma delta epsilon zeta eta theta"), maxLength: 20);

        Assert.Equal("alpha beta gamma…", sample);
    }
}
