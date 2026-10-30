using Talktrace.Core;
using Talktrace.Core.Audio;
using Talktrace.Core.Pipeline;
using Talktrace.Core.Speakers;

namespace Talktrace.Tests;

public sealed class SpeakerDiarizerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Reports_models_that_fail_to_load_instead_of_crashing()
    {
        string segmentation = Path.Combine(_folder, "segmentation.onnx");
        string embedding = Path.Combine(_folder, "embedding.onnx");
        File.WriteAllText(segmentation, "not a model");
        File.WriteAllText(embedding, "not a model");
        var audio = new AudioClip(new float[AudioDecoder.SampleRate], AudioDecoder.SampleRate);

        Assert.Throws<TalktraceException>(() => SpeakerDiarizer.FindSpeakers(
            audio, segmentation, embedding, new TranscribeOptions(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Reports_an_embedding_model_that_fails_to_load_instead_of_crashing()
    {
        string embedding = Path.Combine(_folder, "embedding.onnx");
        File.WriteAllText(embedding, "not a model");
        var audio = new AudioClip(new float[AudioDecoder.SampleRate], AudioDecoder.SampleRate);

        Assert.Throws<TalktraceException>(() => SpeakerDiarizer.VoicePrints(
            audio, [new SpeakerSegment(0, 1, 0)], embedding, new TranscribeOptions()));
    }

    [Fact]
    public void Clean_speech_leaves_out_the_parts_another_speaker_overlaps()
    {
        SpeakerSegment[] others = [new(1, 2, 1), new(3, 4, 2), new(9, 12, 1)];

        IEnumerable<TimeRange> clean = SpeakerDiarizer.Subtract(new TimeRange(0, 10), others);

        Assert.Equal([new TimeRange(0, 1), new TimeRange(2, 3), new TimeRange(4, 9)], clean);
    }

    [Fact]
    public void Clean_speech_is_empty_when_another_speaker_covers_it_all()
    {
        Assert.Empty(SpeakerDiarizer.Subtract(new TimeRange(2, 3), [new SpeakerSegment(1, 4, 1)]));
    }
}
