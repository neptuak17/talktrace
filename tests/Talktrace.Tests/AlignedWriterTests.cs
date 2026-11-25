using Talktrace.Recording;

namespace Talktrace.Tests;

public class AlignedWriterTests
{
    private const int Rate = AlignedWriter.SampleRate;

    /// <summary>
    /// Simulates two devices with their own clocks for <paramref name="minutes"/>: the microphone delivers 10 ms packets,
    /// system audio packets arrive with a few ms of jitter, and the writer takes every 100 ms. A marker sample is put in
    /// both streams at the same true time every minute; returns where each pair came out, in samples.
    /// </summary>
    private static (List<(long Mic, long System)> Markers, AlignedWriter Writer) Simulate(
        double minutes, double systemPpm, double microphonePpm = 0, int seed = 1)
    {
        var writer = new AlignedWriter(twoTracks: true);
        var random = new Random(seed);
        double micRate = Rate * (1 + microphonePpm / 1e6);
        double sysRate = Rate * (1 + systemPpm / 1e6);
        long micSent = 0, sysSent = 0, micOut = 0, sysOut = 0;
        var micMarkers = new List<long>();
        var sysMarkers = new List<long>();
        float[] packet = new float[Rate];
        int steps = (int)(minutes * 60 * 100); // 10 ms steps

        for (int step = 1; step <= steps; step++)
        {
            double t = step / 100.0;
            micSent = Send(micSent, (long)(t * micRate), micRate, writer.AddMicrophone);
            double late = random.NextDouble() * 0.008;
            sysSent = Send(sysSent, (long)(Math.Max(0, t - late) * sysRate), sysRate, writer.AddSystem);

            if (step % 10 == 0)
            {
                AlignedFrames frames = writer.Take();
                Assert.Equal(frames.Microphone.Length, frames.System!.Length);
                micOut = Scan(frames.Microphone, micOut, micMarkers);
                sysOut = Scan(frames.System, sysOut, sysMarkers);
            }
        }

        return (micMarkers.Zip(sysMarkers).ToList(), writer);

        long Send(long sent, long upTo, double rate, Action<ReadOnlySpan<float>> add)
        {
            int count = (int)(upTo - sent);
            if (count <= 0)
                return sent;
            Span<float> samples = packet.AsSpan(0, count);
            samples.Clear();
            for (long i = sent; i < upTo; i++)
            {
                double seconds = i / rate;
                double sinceMarker = seconds - Math.Floor(seconds / 60) * 60;
                if (seconds >= 60 && sinceMarker < 3.0 / rate)
                    samples[(int)(i - sent)] = 1; // a marker a few samples wide, once a minute
            }
            add(samples);
            return upTo;
        }

        static long Scan(float[] samples, long position, List<long> markers)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                if (samples[i] == 1 && (markers.Count == 0 || position + i - markers[^1] > Rate))
                    markers.Add(position + i);
            }
            return position + samples.Length;
        }
    }

    [Theory]
    [InlineData(100)]   // the system clock runs fast: 0.54 s over 90 minutes
    [InlineData(-100)]  // the system clock runs slow
    [InlineData(0)]
    public void Keeps_the_tracks_within_a_quarter_second_over_90_minutes(double systemPpm)
    {
        (List<(long Mic, long System)> markers, AlignedWriter writer) = Simulate(minutes: 90, systemPpm);

        Assert.Equal(89, markers.Count);
        long[] gaps = markers.Select(m => m.System - m.Mic).ToArray();
        Assert.All(gaps, gap => Assert.InRange(Math.Abs(gap), 0, Rate / 4));
        Assert.InRange(gaps.Max() - gaps.Min(), 0, Rate / 4);
        if (systemPpm > 0)
            Assert.True(writer.Drops > 0);
        if (systemPpm < 0)
            Assert.True(writer.Repeats > 0);
    }

    [Fact]
    public void Follows_a_microphone_clock_that_drifts_instead()
    {
        (List<(long Mic, long System)> markers, _) = Simulate(minutes: 30, systemPpm: 0, microphonePpm: 150);

        long[] gaps = markers.Select(m => m.System - m.Mic).ToArray();
        Assert.InRange(gaps.Max() - gaps.Min(), 0, Rate / 4);
    }

    [Fact]
    public void Holds_back_a_cushion_then_writes_both_tracks_at_the_same_length()
    {
        var writer = new AlignedWriter(twoTracks: true);
        writer.AddMicrophone(new float[AlignedWriter.TargetSamples]);
        writer.AddSystem(new float[AlignedWriter.TargetSamples]);
        Assert.Equal(0, writer.Take().Length);

        writer.AddMicrophone(Ones(1600));
        writer.AddSystem(Ones(1600));
        AlignedFrames frames = writer.Take();

        Assert.Equal(1600, frames.Length);
        Assert.Equal(1600, frames.System!.Length);
        Assert.Equal(0, writer.Drops + writer.Repeats + writer.Resyncs);
    }

    [Fact]
    public void Writes_zeros_when_system_audio_runs_dry()
    {
        var writer = new AlignedWriter(twoTracks: true);
        writer.AddMicrophone(Ones(AlignedWriter.TargetSamples + 1600));

        AlignedFrames frames = writer.Take();

        Assert.Equal(1600, frames.Length);
        Assert.All(frames.System!, s => Assert.Equal(0, s));
        Assert.Equal(1, writer.Resyncs);
    }

    [Fact]
    public void Drops_a_surplus_of_system_audio_at_once()
    {
        var writer = new AlignedWriter(twoTracks: true);
        writer.AddMicrophone(new float[AlignedWriter.TargetSamples + 1600]);
        writer.AddSystem(new float[AlignedWriter.TargetSamples + 1600 + Rate]); // a second too much

        writer.Take();
        writer.AddMicrophone(new float[1600]);
        writer.AddSystem(new float[1600]);
        writer.Take();

        Assert.Equal(1, writer.Resyncs);
        Assert.Equal(0, writer.Drops);
    }

    [Fact]
    public void Caps_system_audio_while_the_microphone_delivers_nothing()
    {
        var writer = new AlignedWriter(twoTracks: true);
        for (int i = 0; i < 10; i++)
            writer.AddSystem(new float[Rate]);

        writer.AddMicrophone(new float[AlignedWriter.TargetSamples + 1600]);
        AlignedFrames frames = writer.Take();

        Assert.Equal(1600, frames.Length);
        Assert.Equal(1, writer.Resyncs);
    }

    [Fact]
    public void Writes_everything_from_the_microphone_for_in_person()
    {
        var writer = new AlignedWriter(twoTracks: false);
        writer.AddMicrophone(Ones(500));

        AlignedFrames frames = writer.Take();

        Assert.Equal(500, frames.Length);
        Assert.Null(frames.System);
    }

    [Fact]
    public void Keeps_nothing_while_paused_and_starts_again_from_empty()
    {
        var writer = new AlignedWriter(twoTracks: false);
        writer.AddMicrophone(Ones(500));
        writer.Paused = true;
        writer.AddMicrophone(Ones(500));
        Assert.Equal(0, writer.Take().Length);

        writer.Paused = false;
        writer.AddMicrophone(Ones(300));

        Assert.Equal(300, writer.Take().Length);
    }

    private static float[] Ones(int count) => Enumerable.Repeat(1f, count).ToArray();
}
