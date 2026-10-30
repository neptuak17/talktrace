using Talktrace.Core;
using Talktrace.Core.Models;
using Talktrace.Core.Transcripts;
using Talktrace.Core.Worker;

namespace Talktrace.Tests;

public class WorkerProtocolTests
{
    [Fact]
    public void A_job_round_trips_with_every_option()
    {
        var options = new TranscribeOptions
        {
            Profile = ModelProfile.Multilingual,
            Speakers = SpeakerSetting.Exactly(3),
            IncludeTimestamps = true,
            OutputPath = @"C:\Users\Zoë\Documents\Q3 – planning.txt",
            NumThreads = 4,
            ClusteringThreshold = 0.55,
            MaxChunkSeconds = 20,
            VadThreshold = 0.4f,
        };
        var job = new WorkerJob(@"C:\Recordings\Café meeting.m4a", options);

        string line = WorkerProtocol.Serialize(job);

        Assert.DoesNotContain('\n', line);
        Assert.Equal(job, WorkerProtocol.ParseCommand(line));
        Assert.Contains("\"profile\":\"multilingual\"", line);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, 5)]
    public void Speaker_settings_round_trip(bool label, int? count)
    {
        var job = new WorkerJob("a.wav", new TranscribeOptions { Speakers = new SpeakerSetting(label, count) });

        var parsed = Assert.IsType<WorkerJob>(WorkerProtocol.ParseCommand(WorkerProtocol.Serialize(job)));

        Assert.Equal(new SpeakerSetting(label, count), parsed.Options.Speakers);
    }

    [Fact]
    public void A_cancel_round_trips()
    {
        Assert.IsType<WorkerCancel>(WorkerProtocol.ParseCommand(WorkerProtocol.Serialize(new WorkerCancel())));
    }

    [Fact]
    public void A_result_round_trips()
    {
        var result = new TranscriptResult(
            SourceFileName: "Q3 planning.m4a",
            TranscribedAt: new DateTime(2026, 10, 29, 14, 5, 0),
            Duration: TimeSpan.FromSeconds(3134.5),
            ModelName: "Fast (English)",
            SpeakerLabels: true,
            Turns: [new Turn(1, 0.5, 4.25, "Okay, let's \"start\"."), new Turn(2, 5, 6, "Sounds good.")],
            Timings: [new StageTiming(TranscribeStage.ReadingAudio, TimeSpan.FromSeconds(13.2))],
            ChunkCount: 2,
            SavedPath: @"C:\Recordings\Q3 planning - transcript.txt");

        var parsed = Assert.IsType<WorkerResult>(WorkerProtocol.ParseMessage(WorkerProtocol.Serialize(new WorkerResult(result)))).Result;

        Assert.Equal(result.Turns, parsed.Turns);
        Assert.Equal(result.Timings, parsed.Timings);
        Assert.Equal(result.TranscribedAt, parsed.TranscribedAt);
        Assert.Equal(TranscriptFormatter.Format(result, includeTimestamps: true), TranscriptFormatter.Format(parsed, includeTimestamps: true));
        Assert.Equal(result with { Turns = parsed.Turns, Timings = parsed.Timings }, parsed);
    }

    [Fact]
    public void Progress_and_errors_round_trip()
    {
        WorkerMessage[] messages =
        [
            new WorkerProgress(TranscribeStage.Transcribing, 42, 310),
            new WorkerError("Talktrace can't read this MP3 file.", "System.Runtime.InteropServices.COMException ..."),
        ];

        foreach (WorkerMessage message in messages)
            Assert.Equal(message, WorkerProtocol.ParseMessage(WorkerProtocol.Serialize(message)));
        Assert.Contains("\"stage\":\"Transcribing\"", WorkerProtocol.Serialize(messages[0]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Some native library's own output")]
    [InlineData("{not json")]
    [InlineData("{\"stage\":\"Transcribing\"}")]
    [InlineData("{\"type\":\"nonsense\"}")]
    public void Lines_that_are_not_messages_are_ignored(string? line)
    {
        Assert.Null(WorkerProtocol.ParseMessage(line));
        Assert.Null(WorkerProtocol.ParseCommand(line));
    }

    [Fact]
    public void An_unknown_profile_is_not_a_job()
    {
        string line = WorkerProtocol.Serialize(new WorkerJob("a.wav", new TranscribeOptions())).Replace("\"fast\"", "\"turbo\"");

        Assert.Null(WorkerProtocol.ParseCommand(line));
    }
}
