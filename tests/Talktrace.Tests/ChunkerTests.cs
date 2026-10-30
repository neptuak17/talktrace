using Talktrace.Core;
using Talktrace.Core.Pipeline;

namespace Talktrace.Tests;

public class ChunkerTests
{
    private const int Rate = 16000;
    private static readonly TranscribeOptions Options = new();

    [Fact]
    public void Drops_regions_shorter_than_the_minimum()
    {
        var chunks = Build([new(1.0, 1.2), new(5, 6)], seconds: 10);

        AssertRanges(chunks, (4.85, 6.15));
    }

    [Fact]
    public void Packs_close_regions_into_one_chunk()
    {
        var chunks = Build([new(1, 3), new(3.5, 6), new(7, 9)], seconds: 10);

        AssertRanges(chunks, (0.85, 9.15));
    }

    [Fact]
    public void Starts_a_new_chunk_after_a_long_pause()
    {
        var chunks = Build([new(1, 3), new(6, 8)], seconds: 10);

        AssertRanges(chunks, (0.85, 3.15), (5.85, 8.15));
    }

    [Fact]
    public void Starts_a_new_chunk_when_the_next_region_would_go_past_the_limit()
    {
        var chunks = Build([new(0, 15), new(16, 30)], seconds: 40);

        AssertRanges(chunks, (0, 15.15), (15.85, 30.15));
    }

    [Fact]
    public void Splits_a_long_region_at_its_quietest_point_without_padding_across_the_split()
    {
        float[] samples = Noise(seconds: 60);
        Array.Clear(samples, (int)(18.0 * Rate), (int)(0.2 * Rate)); // quiet from 18.0 to 18.2 s

        var chunks = Chunker.Build([new TimeRange(0, 40)], samples, Rate, Options);

        Assert.Equal(2, chunks.Count);
        Assert.InRange(chunks[0].End, 18.0, 18.2);
        Assert.Equal(chunks[0].End, chunks[1].Start);
        Assert.Equal(40.15, chunks[1].End, precision: 6);
    }

    [Fact]
    public void Keeps_every_piece_of_a_very_long_region_within_the_limit()
    {
        var chunks = Chunker.Build([new TimeRange(0, 200)], Noise(seconds: 200), Rate, Options);

        Assert.All(chunks, c => Assert.True(c.Duration <= Options.MaxChunkSeconds + Options.ChunkPaddingSeconds));
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(200, chunks[^1].End, precision: 6);
    }

    [Fact]
    public void Clamps_padding_to_the_recording()
    {
        var chunks = Build([new(0.05, 2.0)], seconds: 2.1);

        AssertRanges(chunks, (0, 2.1));
    }

    private static IReadOnlyList<TimeRange> Build(TimeRange[] speech, double seconds) =>
        Chunker.Build(speech, new float[(int)(seconds * Rate)], Rate, Options);

    private static float[] Noise(double seconds)
    {
        var random = new Random(42);
        var samples = new float[(int)(seconds * Rate)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(random.NextDouble() - 0.5);
        return samples;
    }

    private static void AssertRanges(IReadOnlyList<TimeRange> actual, params (double Start, double End)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Start, actual[i].Start, precision: 6);
            Assert.Equal(expected[i].End, actual[i].End, precision: 6);
        }
    }
}
