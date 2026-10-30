using Talktrace.Core.Transcripts;

namespace Talktrace.Tests;

public sealed class TranscriptFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Default_path_is_next_to_the_recording()
    {
        string recording = Path.Combine(_folder, "Q3 planning.m4a");

        Assert.Equal(Path.Combine(_folder, "Q3 planning - transcript.txt"), TranscriptFile.DefaultPath(recording));
    }

    [Fact]
    public void Never_overwrites_an_existing_transcript()
    {
        string path = Path.Combine(_folder, "meeting - transcript.txt");

        string first = TranscriptFile.SaveNew(path, "first");
        string second = TranscriptFile.SaveNew(path, "second");
        string third = TranscriptFile.SaveNew(path, "third");

        Assert.Equal(path, first);
        Assert.Equal(Path.Combine(_folder, "meeting - transcript (2).txt"), second);
        Assert.Equal(Path.Combine(_folder, "meeting - transcript (3).txt"), third);
        Assert.Equal("first", File.ReadAllText(first));
    }

    [Fact]
    public void Writes_utf8_without_a_byte_order_mark()
    {
        string path = TranscriptFile.SaveNew(Path.Combine(_folder, "t.txt"), "Café · 52:14");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("Café · 52:14", File.ReadAllText(path));
    }
}
