using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;

namespace Talktrace.Tests;

/// <summary>Runs the real model. Skipped unless TALKTRACE_MODELS_DIR points at the models.</summary>
public class ParakeetIntegrationTests
{
    [Fact]
    public void Transcribes_the_sample_wav_with_word_times()
    {
        string? modelsDir = Environment.GetEnvironmentVariable(ModelLocator.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelsDir), $"{ModelLocator.EnvironmentVariable} is not set.");

        string modelDirectory = Path.Combine(modelsDir!, ModelProfile.Fast.AsrDirectory);
        AudioClip audio = WavReader.Read(Path.Combine(modelDirectory, "test_wavs", "0.wav"));

        using var recognizer = new ParakeetRecognizer(modelDirectory, numThreads: 4);
        RecognizedText result = recognizer.Recognize(audio.Samples, audio.SampleRate);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.True(result.HasWordTimes);
        Assert.NotEmpty(result.Words);
        Assert.All(result.Words, w => Assert.InRange(w.Start, 0, audio.DurationSeconds + 0.5));
        Assert.Equal(result.Words.OrderBy(w => w.Start), result.Words);
    }
}
