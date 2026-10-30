using SherpaOnnx;

namespace Talktrace.Core.Asr;

/// <summary>Text and word times for one piece of audio.</summary>
/// <param name="HasWordTimes">False if the model returned no token timestamps.</param>
public sealed record RecognizedText(string Text, IReadOnlyList<Word> Words, bool HasWordTimes);

/// <summary>
/// One sherpa-onnx recognizer for a Parakeet TDT model, created once and reused for every chunk.
/// </summary>
public sealed class ParakeetRecognizer : IDisposable
{
    private readonly OfflineRecognizer _recognizer;

    public ParakeetRecognizer(string modelDirectory, int numThreads)
    {
        string encoder = ModelFile(modelDirectory, "encoder.int8.onnx");
        string decoder = ModelFile(modelDirectory, "decoder.int8.onnx");
        string joiner = ModelFile(modelDirectory, "joiner.int8.onnx");
        string tokens = ModelFile(modelDirectory, "tokens.txt");

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        // A generic "transducer" type fails on Parakeet's vocabulary-size metadata lookup.
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = numThreads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
    }

    /// <param name="samples">Mono audio in the range -1 to 1.</param>
    /// <param name="offsetSeconds">Where this audio starts in the recording, added to every word time.</param>
    public RecognizedText Recognize(float[] samples, int sampleRate, double offsetSeconds = 0)
    {
        using OfflineStream stream = _recognizer.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        _recognizer.Decode(stream);
        OfflineRecognizerResult result = stream.Result;

        if (result.Timestamps is null)
            return new RecognizedText(result.Text.Trim(), [], HasWordTimes: false);

        IReadOnlyList<Word> words = TokenGrouping.ToWords(result.Tokens, result.Timestamps, result.Durations, offsetSeconds);
        return new RecognizedText(result.Text.Trim(), words, HasWordTimes: true);
    }

    public void Dispose() => _recognizer.Dispose();

    private static string ModelFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model file not found: {path}", path);

        // sherpa-onnx takes paths as ANSI strings, so a path outside ASCII may not open.
        if (path.Any(c => c > 127))
            throw new NotSupportedException(
                $"The model path contains characters sherpa-onnx can't open: {path}. " +
                $"Move the models to a plain path and set {Models.ModelLocator.EnvironmentVariable} to it.");

        return path;
    }
}
