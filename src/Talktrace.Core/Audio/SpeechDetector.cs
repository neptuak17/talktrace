using SherpaOnnx;
using Talktrace.Core.Pipeline;

namespace Talktrace.Core.Audio;

/// <summary>Finds the speech in a recording with Silero VAD.</summary>
internal static class SpeechDetector
{
    private const int BlockSamples = AudioDecoder.SampleRate; // feed one second at a time

    public static IReadOnlyList<TimeRange> FindSpeech(
        AudioClip audio, string modelPath, TranscribeOptions options, Action<double>? reportFraction, CancellationToken cancellationToken)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = options.VadThreshold;
        config.SileroVad.MinSilenceDuration = (float)options.VadMinSilenceSeconds;
        config.SileroVad.MinSpeechDuration = (float)options.MinSpeechSeconds;
        // Past this length the VAD splits at the next short pause, keeping regions near chunk size.
        config.SileroVad.MaxSpeechDuration = (float)options.VadMaxSpeechSeconds;
        config.SampleRate = audio.SampleRate;
        config.NumThreads = 1;

        var regions = new List<TimeRange>();
        // Sized well past the longest expected region; sherpa-onnx grows it (with a warning) if needed.
        using var vad = new VoiceActivityDetector(config, bufferSizeInSeconds: 180);

        float[] block = new float[BlockSamples];
        float[] samples = audio.Samples;
        for (int offset = 0; offset < samples.Length; offset += BlockSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(BlockSamples, samples.Length - offset);
            if (count < BlockSamples)
                block = new float[count];
            Array.Copy(samples, offset, block, 0, count);
            vad.AcceptWaveform(block);
            Drain();

            if (offset % (BlockSamples * 60) == 0)
                reportFraction?.Invoke((double)offset / samples.Length);
        }

        vad.Flush();
        Drain();
        reportFraction?.Invoke(1);
        return regions;

        void Drain()
        {
            while (!vad.IsEmpty())
            {
                SpeechSegment segment = vad.Front();
                double start = (double)segment.Start / audio.SampleRate;
                regions.Add(new TimeRange(start, start + (double)segment.Samples.Length / audio.SampleRate));
                vad.Pop();
            }
        }
    }
}
