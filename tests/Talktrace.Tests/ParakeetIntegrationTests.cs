using Talktrace.Core;
using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;
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
                recording, new TranscribeOptions { NumThreads = 4 }, null, TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(folder, "sample - transcript.txt"), result.SavedPath);
            string saved = await File.ReadAllTextAsync(result.SavedPath, TestContext.Current.CancellationToken);
            Assert.StartsWith("Transcript: sample.wav\r\n", saved);
            Assert.Contains("portrait", saved);
            Assert.NotEmpty(result.Turns);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
