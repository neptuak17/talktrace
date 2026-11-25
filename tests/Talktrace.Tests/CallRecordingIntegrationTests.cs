using NAudio.Wave;
using Talktrace.Core;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;
using Talktrace.Core.Recordings;
using Talktrace.Core.Transcripts;

namespace Talktrace.Tests;

/// <summary>
/// The two-track path on made-up call recordings built from the public sherpa-onnx sample. Skipped unless
/// TALKTRACE_MODELS_DIR points at the models.
/// </summary>
public sealed class CallRecordingIntegrationTests : IDisposable
{
    private const int Rate = AudioDecoder.SampleRate;
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static float[] Sample()
    {
        string? modelsDir = Environment.GetEnvironmentVariable(ModelLocator.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelsDir), $"{ModelLocator.EnvironmentVariable} is not set.");
        string wav = Path.Combine(modelsDir!, ModelProfile.Fast.AsrDirectory, "test_wavs", "0.wav");
        return AudioDecoder.Decode(wav, TestContext.Current.CancellationToken).Samples;
    }

    /// <summary>The sample played at 0.7× speed, which also lowers its pitch: a second voice.</summary>
    private static float[] Slowed(float[] samples, double speed = 0.7)
    {
        var slowed = new float[(int)(samples.Length / speed)];
        for (int i = 0; i < slowed.Length; i++)
        {
            double at = i * speed;
            int j = (int)at;
            float next = j + 1 < samples.Length ? samples[j + 1] : 0;
            slowed[i] = (float)(samples[j] + (next - samples[j]) * (at - j));
        }
        return slowed;
    }

    private static void Mix(float[] track, float[] sound, double atSeconds, float gain = 1)
    {
        int offset = (int)(atSeconds * Rate);
        for (int i = 0; i < sound.Length && offset + i < track.Length; i++)
            track[offset + i] += sound[i] * gain;
    }

    /// <summary>A 16 kHz stereo WAV, microphone left and system audio right, with its sidecar, as Talktrace records them.</summary>
    private string WriteCall(RecordingMode mode, float[] mic, float[] system)
    {
        string path = Path.Combine(_folder, "Recording 2026-11-25 1405.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 2)))
        {
            for (int i = 0; i < mic.Length; i++)
            {
                writer.WriteSample(Math.Clamp(mic[i], -1, 1));
                writer.WriteSample(Math.Clamp(system[i], -1, 1));
            }
        }
        new RecordingSidecar
        {
            Mode = mode, Status = RecordingStatus.Complete, StartedAt = DateTimeOffset.Now, MicrophoneChannel = 0, SystemChannel = 1,
        }.Write(path);
        return path;
    }

    private static Task<TranscriptResult> Transcribe(string path, SpeakerSetting speakers) =>
        Transcriber.TranscribeAsync(
            path, new TranscribeOptions { Speakers = speakers, NumThreads = 4, YourName = "Tester" }, null,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Headphones_labels_the_microphone_with_my_name_and_the_call_by_number()
    {
        float[] sample = Sample();
        float[] other = Slowed(sample);
        double length = sample.Length / (double)Rate;
        var mic = new float[(int)((length + 1 + other.Length / (double)Rate + 1) * Rate)];
        var system = new float[mic.Length];
        Mix(mic, sample, 0);              // I speak first...
        Mix(system, other, length + 1);   // ...then the other participant answers

        TranscriptResult result = await Transcribe(WriteCall(RecordingMode.CallHeadphones, mic, system), SpeakerSetting.Exactly(1));

        Assert.Equal("Tester", result.YourName);
        Assert.Equal([0, 1], result.Turns.Select(t => t.Speaker));
        Assert.Contains("portrait", result.Turns[0].Text);
        string saved = await File.ReadAllTextAsync(result.SavedPath!, TestContext.Current.CancellationToken);
        Assert.Contains("\r\nSpeakers: Tester, Speaker 1\r\n", saved);
        Assert.Contains("\r\n\r\nTester: ", saved);
    }

    [Fact]
    public async Task Speakers_removes_the_call_picked_up_by_the_microphone_and_keeps_my_own_speech()
    {
        float[] sample = Sample();
        float[] mine = Slowed(sample);
        double length = sample.Length / (double)Rate;
        var mic = new float[(int)((length + 1 + mine.Length / (double)Rate + 1) * Rate)];
        var system = new float[mic.Length];
        Mix(system, sample, 0);            // the other participant speaks...
        Mix(mic, sample, 0.25, gain: 0.5f); // ...and the microphone hears it from the speaker, quieter and a little late
        Mix(mic, mine, length + 1);        // then I answer

        TranscriptResult result = await Transcribe(WriteCall(RecordingMode.CallSpeakers, mic, system), SpeakerSetting.Exactly(1));

        Assert.Equal([1, 0], result.Turns.Select(t => t.Speaker));
        Assert.Contains("portrait", result.Turns[0].Text);
        Assert.True(result.Turns[1].Start > length + 0.5, "The echo was kept: my turn starts during the other participant's.");
    }

    [Fact]
    public async Task Headphones_keeps_what_the_microphone_hears_even_if_the_call_says_it_too()
    {
        float[] sample = Sample();
        double length = sample.Length / (double)Rate;
        var mic = new float[(int)((length + 2) * Rate)];
        var system = new float[mic.Length];
        Mix(system, sample, 0);
        Mix(mic, sample, 0.25, gain: 0.5f);

        TranscriptResult result = await Transcribe(WriteCall(RecordingMode.CallHeadphones, mic, system), SpeakerSetting.Exactly(1));

        Assert.Contains(result.Turns, t => t.Speaker == 0); // no echo removal with headphones
    }

    [Fact]
    public async Task Headphones_without_labels_gives_paragraphs_from_both_tracks()
    {
        float[] sample = Sample();
        double length = sample.Length / (double)Rate;
        var mic = new float[(int)((2 * length + 4) * Rate)];
        var system = new float[mic.Length];
        Mix(mic, sample, 0);
        Mix(system, sample, length + 3);

        TranscriptResult result = await Transcribe(WriteCall(RecordingMode.CallHeadphones, mic, system), SpeakerSetting.None);

        Assert.Equal(2, result.Turns.Count);
        Assert.All(result.Turns, t => Assert.Null(t.Speaker));
        Assert.All(result.Turns, t => Assert.Contains("portrait", t.Text));
    }

    [Fact]
    public async Task A_stereo_file_without_a_sidecar_is_an_ordinary_recording()
    {
        float[] sample = Sample();
        string path = WriteCall(RecordingMode.CallHeadphones, sample, new float[sample.Length]);
        File.Delete(RecordingSidecar.PathFor(path));

        TranscriptResult result = await Transcribe(path, SpeakerSetting.Exactly(1));

        Assert.Null(result.YourName);
        Assert.Equal(1, Assert.Single(result.Turns).Speaker);
    }
}
