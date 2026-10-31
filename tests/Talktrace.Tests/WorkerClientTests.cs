using System.Diagnostics;
using NAudio.Wave;
using Talktrace.Core;
using Talktrace.Core.Models;
using Talktrace.Core.Transcripts;
using Talktrace.Core.Worker;

namespace Talktrace.Tests;

/// <summary>
/// Runs jobs in a real Talktrace.Cli worker process, copied next to the tests by the project reference.
/// Tests that transcribe skip unless TALKTRACE_MODELS_DIR points at the models.
/// </summary>
public sealed class WorkerClientTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Reports_a_failed_job_as_a_plain_error()
    {
        string missing = Path.Combine(_folder, "missing.m4a");

        var error = await Assert.ThrowsAsync<WorkerException>(() =>
            WorkerClient.RunAsync(missing, new TranscribeOptions(), null, TestContext.Current.CancellationToken));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Contains(nameof(TalktraceException), error.Details);
    }

    [Fact]
    public async Task Transcribes_in_the_worker_and_saves_the_transcript()
    {
        string recording = CopySample("sample.wav");
        var stages = new List<TranscribeStage>();

        TranscriptResult result = await WorkerClient.RunAsync(
            recording, new TranscribeOptions { Speakers = SpeakerSetting.None, NumThreads = 4 },
            new SyncProgress(p => stages.Add(p.Stage)), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_folder, "sample - transcript.txt"), result.SavedPath);
        Assert.Equal(TranscriptFormatter.Format(result, includeTimestamps: false), await File.ReadAllTextAsync(result.SavedPath!, TestContext.Current.CancellationToken));
        Assert.NotEmpty(result.Turns);
        Assert.Equal([TranscribeStage.ReadingAudio, TranscribeStage.FindingSpeech, TranscribeStage.Transcribing, TranscribeStage.Saving], stages.Distinct());
    }

    [Fact]
    public async Task Cancelling_stops_the_worker_at_the_next_chunk_and_saves_nothing()
    {
        string recording = CopySample("sample.wav");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new Stopwatch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerClient.RunAsync(
            recording, new TranscribeOptions { Speakers = SpeakerSetting.None, NumThreads = 4 },
            new SyncProgress(_ =>
            {
                clock.Start();
                cancel.Cancel();
            }),
            cancel.Token));

        Assert.True(clock.Elapsed < WorkerClient.CancelGrace, $"Took {clock.Elapsed.TotalSeconds:F1} s to stop.");
        Assert.False(File.Exists(Path.Combine(_folder, "sample - transcript.txt")));
    }

    [Fact]
    public async Task Cancelling_during_speaker_identification_kills_the_worker()
    {
        string recording = Repeat(CopySample("sample.wav"), "long.wav", times: 30);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new Stopwatch();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerClient.RunAsync(
            recording, new TranscribeOptions { Speakers = SpeakerSetting.Auto, NumThreads = 4 },
            new SyncProgress(p =>
            {
                if (p.Stage != TranscribeStage.IdentifyingSpeakers || clock.IsRunning)
                    return;
                clock.Start();
                cancel.Cancel();
            }),
            cancel.Token));

        Assert.True(clock.IsRunning, "Speaker identification never started.");
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, WorkerClient.CancelGrace + TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(Path.Combine(_folder, "long - transcript.txt")));
    }

    private string CopySample(string name)
    {
        string? modelsDir = Environment.GetEnvironmentVariable(ModelLocator.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelsDir), $"{ModelLocator.EnvironmentVariable} is not set.");
        string path = Path.Combine(_folder, name);
        File.Copy(Path.Combine(modelsDir!, ModelProfile.Fast.AsrDirectory, "test_wavs", "0.wav"), path);
        return path;
    }

    private string Repeat(string source, string name, int times)
    {
        string path = Path.Combine(_folder, name);
        using var reader = new WaveFileReader(source);
        byte[] audio = new byte[reader.Length];
        reader.ReadExactly(audio);
        using var writer = new WaveFileWriter(path, reader.WaveFormat);
        for (int i = 0; i < times; i++)
            writer.Write(audio, 0, audio.Length);
        return path;
    }

    /// <summary>Reports on the worker's reading thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SyncProgress(Action<TranscribeProgress> report) : IProgress<TranscribeProgress>
    {
        public void Report(TranscribeProgress value) => report(value);
    }
}
