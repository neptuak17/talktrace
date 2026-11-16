using NAudio.MediaFoundation;
using NAudio.Wave;
using Talktrace.Core;
using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;
using Talktrace.Core.Speakers;
using Talktrace.Core.Transcripts;

namespace Talktrace.Tests;

/// <summary>Runs the real models. Skipped unless TALKTRACE_MODELS_DIR points at them.</summary>
public class ParakeetIntegrationTests
{
    private static string RequireModels()
    {
        string? modelsDir = Environment.GetEnvironmentVariable(ModelLocator.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelsDir), $"{ModelLocator.EnvironmentVariable} is not set.");
        return modelsDir!;
    }

    private static string SampleWav(string modelsDir) =>
        Path.Combine(modelsDir, ModelProfile.Fast.AsrDirectory, "test_wavs", "0.wav");

    [Fact]
    public void Transcribes_the_sample_wav_with_word_times()
    {
        string modelsDir = RequireModels();
        AudioClip audio = AudioDecoder.Decode(SampleWav(modelsDir), TestContext.Current.CancellationToken);

        using var recognizer = new ParakeetRecognizer(Path.Combine(modelsDir, ModelProfile.Fast.AsrDirectory), numThreads: 4);
        RecognizedText result = recognizer.Recognize(audio.Samples, audio.SampleRate);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.True(result.HasWordTimes);
        Assert.NotEmpty(result.Words);
        Assert.All(result.Words, w => Assert.InRange(w.Start, 0, audio.DurationSeconds + 0.5));
        Assert.Equal(result.Words.OrderBy(w => w.Start), result.Words);
    }

    [Fact]
    public async Task Pipeline_transcribes_and_saves_without_speaker_labels()
    {
        string modelsDir = RequireModels();
        string folder = Directory.CreateTempSubdirectory("talktrace-").FullName;
        try
        {
            string recording = Path.Combine(folder, "sample.wav");
            File.Copy(SampleWav(modelsDir), recording);

            TranscriptResult result = await Transcriber.TranscribeAsync(
                recording, new TranscribeOptions { Speakers = SpeakerSetting.None, NumThreads = 4 }, null,
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(folder, "sample - transcript.txt"), result.SavedPath);
            string saved = await File.ReadAllTextAsync(result.SavedPath!, TestContext.Current.CancellationToken);
            Assert.StartsWith("Transcript: sample.wav\r\n", saved);
            Assert.Contains("portrait", saved);
            Assert.DoesNotContain("Speaker", saved);
            Assert.NotEmpty(result.Turns);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("wma")]
    [InlineData("m4a")]
    public async Task Transcribes_the_sample_the_same_from_compressed_formats(string extension)
    {
        string modelsDir = RequireModels();
        string folder = Directory.CreateTempSubdirectory("talktrace-").FullName;
        try
        {
            string wav = Path.Combine(folder, "sample.wav");
            File.Copy(SampleWav(modelsDir), wav);

            // Windows' encoders don't all take 16 kHz mono (MP3 and AAC want 44.1 or 48 kHz, and WMA
            // stereo), so convert the sample to 48 kHz stereo first, as a meeting recording would be.
            string wav48 = Path.Combine(folder, "sample-48k.wav");
            using (var reader = new WaveFileReader(wav))
            using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(48000, 16, 2)))
                WaveFileWriter.CreateWaveFile(wav48, resampler);

            string encoded = Path.Combine(folder, $"sample.{extension}");
            try
            {
                using var reader = new WaveFileReader(wav48);
                switch (extension)
                {
                    case "mp3": MediaFoundationEncoder.EncodeToMp3(reader, encoded); break;
                    case "wma": MediaFoundationEncoder.EncodeToWma(reader, encoded); break;
                    default: MediaFoundationEncoder.EncodeToAac(reader, encoded); break;
                }
            }
            catch (InvalidOperationException e)
            {
                Assert.Skip($"Windows has no {extension} encoder here: {e.Message}");
            }

            var options = new TranscribeOptions { Speakers = SpeakerSetting.None, NumThreads = 4 };
            TranscriptResult fromWav = await Transcriber.TranscribeAsync(wav, options, null, TestContext.Current.CancellationToken);
            TranscriptResult fromEncoded = await Transcriber.TranscribeAsync(encoded, options, null, TestContext.Current.CancellationToken);

            Assert.NotEmpty(Words(fromWav));
            Assert.Equal(Words(fromWav), Words(fromEncoded));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string[] Words(TranscriptResult result) =>
        string.Join(' ', result.Turns.Select(t => t.Text))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(w => w.Length > 0)
            .ToArray();

    [Fact]
    public void Identifies_the_speaker_in_the_sample_wav()
    {
        string modelsDir = RequireModels();
        AudioClip audio = AudioDecoder.Decode(SampleWav(modelsDir), TestContext.Current.CancellationToken);
        var progress = new List<(int Done, int Total)>();

        IReadOnlyList<SpeakerSegment> segments = SpeakerDiarizer.FindSpeakers(
            audio,
            Path.Combine(modelsDir, SpeakerDiarizer.SegmentationModelFile),
            Path.Combine(modelsDir, SpeakerDiarizer.EmbeddingModelFile),
            new TranscribeOptions { NumThreads = 4 },
            (done, total) => progress.Add((done, total)),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.InRange(s.Start, 0, s.End));
        Assert.All(segments, s => Assert.InRange(s.End, s.Start, audio.DurationSeconds + 0.5));
        Assert.Equal(segments.OrderBy(s => s.Start), segments);
        Assert.NotEmpty(progress);
        Assert.Equal(progress[^1].Total, progress[^1].Done);
    }

    [Fact]
    public async Task Pipeline_labels_speakers()
    {
        string modelsDir = RequireModels();
        string folder = Directory.CreateTempSubdirectory("talktrace-").FullName;
        try
        {
            string recording = Path.Combine(folder, "sample.wav");
            File.Copy(SampleWav(modelsDir), recording);

            TranscriptResult result = await Transcriber.TranscribeAsync(
                recording, new TranscribeOptions { Speakers = SpeakerSetting.Auto, NumThreads = 4 }, null,
                TestContext.Current.CancellationToken);

            Assert.True(result.SpeakerLabels);
            Assert.NotEmpty(result.Turns);
            Assert.All(result.Turns, t => Assert.NotNull(t.Speaker));
            Assert.Equal(1, result.Turns[0].Speaker);
            Assert.Contains(result.Timings, t => t.Stage == TranscribeStage.IdentifyingSpeakers);
            string saved = await File.ReadAllTextAsync(result.SavedPath!, TestContext.Current.CancellationToken);
            Assert.Contains("\r\nSpeakers: Speaker 1", saved);
            Assert.Contains("\r\n\r\nSpeaker 1: ", saved);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
