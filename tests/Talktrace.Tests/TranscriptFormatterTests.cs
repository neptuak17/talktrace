using Talktrace.Core.Transcripts;

namespace Talktrace.Tests;

public class TranscriptFormatterTests
{
    private static TranscriptResult Result(params Turn[] turns) => new(
        SourceFileName: "Q3 planning session.m4a",
        TranscribedAt: new DateTime(2026, 10, 29, 14, 5, 0),
        Duration: TimeSpan.FromSeconds(52 * 60 + 14),
        ModelName: "Fast (English)",
        SpeakerLabels: false,
        Turns: turns,
        Timings: [],
        ChunkCount: 0,
        SavedPath: "");

    [Fact]
    public void Writes_the_header_then_one_paragraph_per_turn()
    {
        string text = TranscriptFormatter.Format(
            Result(new Turn(null, 1, 5, "Okay, let's get started."), new Turn(null, 192, 196, "Sounds good.")),
            includeTimestamps: false);

        Assert.Equal(
            "Transcript: Q3 planning session.m4a\r\n" +
            "Transcribed: 2026-10-29 · Duration: 52:14 · Model: Fast (English)\r\n" +
            "\r\n" +
            "Okay, let's get started.\r\n" +
            "\r\n" +
            "Sounds good.\r\n",
            text);
    }

    [Fact]
    public void Starts_each_turn_with_its_time_when_timestamps_are_on()
    {
        string text = TranscriptFormatter.Format(Result(new Turn(null, 192.7, 196, "Sounds good.")), includeTimestamps: true);

        Assert.EndsWith("\r\n[00:03:12] Sounds good.\r\n", text);
    }

    [Theory]
    [InlineData(59, "0:59")]
    [InlineData(52 * 60 + 14, "52:14")]
    [InlineData(3600 + 2 * 60 + 14, "1:02:14")]
    [InlineData(3599.6, "1:00:00")]
    public void Formats_durations(double seconds, string expected)
    {
        Assert.Equal(expected, TranscriptFormatter.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(192.9, "00:03:12")]
    [InlineData(3725.5, "01:02:05")]
    public void Formats_timestamps(double seconds, string expected)
    {
        Assert.Equal(expected, TranscriptFormatter.FormatTimestamp(seconds));
    }
}
