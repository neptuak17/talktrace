using Talktrace.Core;
using Talktrace.Core.Logging;
using Talktrace.Core.Models;
using Talktrace.Core.Transcripts;
using Talktrace.Core.Worker;

namespace Talktrace.Tests;

[CollectionDefinition(nameof(LogTests), DisableParallelization = true)]
public sealed class LogTestsCollection;

/// <summary>The log is one per process, so these tests run on their own.</summary>
[Collection(nameof(LogTests))]
public sealed class LogTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;
    private readonly string? _previousFolder = Environment.GetEnvironmentVariable(Log.EnvironmentVariable);

    public LogTests()
    {
        Environment.SetEnvironmentVariable(Log.EnvironmentVariable, _folder);
    }

    public void Dispose()
    {
        Log.Stop();
        Environment.SetEnvironmentVariable(Log.EnvironmentVariable, _previousFolder);
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Writes_tagged_lines_to_todays_file()
    {
        Log.Start("test");
        Log.Info("Hello.");
        Log.Error("It broke.", new InvalidOperationException("Because."));

        string[] lines = File.ReadAllLines(Path.Combine(_folder, Log.FileName(DateTime.Now)));

        Assert.Contains(lines, l => l.Contains($" test {Environment.ProcessId} INFO Hello."));
        Assert.Contains(lines, l => l.Contains($" test {Environment.ProcessId} ERROR It broke."));
        Assert.Contains(lines, l => l.Contains("InvalidOperationException: Because."));
    }

    [Fact]
    public void Writes_nothing_until_started()
    {
        Log.Info("Hello.");

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Replaces_redacted_paths_wherever_they_appear()
    {
        string recording = Path.Combine(_folder, "Clients", "Acme budget review.m4a");
        Log.Start("test");
        Log.Redact(recording);

        Log.Error($"Couldn't read {recording}.", new FileNotFoundException("Missing", Path.GetFileName(recording)));
        Log.Info("acme budget review - transcript.txt was saved in Clients.");

        string log = File.ReadAllText(Path.Combine(_folder, Log.FileName(DateTime.Now)));
        Assert.DoesNotContain("Acme", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(_folder, "Clients"), log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Couldn't read <recording>.", log);
    }

    [Fact]
    public void Deletes_logs_older_than_fourteen_days()
    {
        var today = new DateTime(2026, 10, 31);
        foreach (int daysAgo in new[] { 0, 14, 15, 30 })
            File.WriteAllText(Path.Combine(_folder, Log.FileName(today.AddDays(-daysAgo))), "");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "");

        Log.DeleteOldFiles(_folder, today);

        Assert.Equal(
            ["notes.txt", "talktrace-2026-10-17.log", "talktrace-2026-10-31.log"],
            Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// Runs a real job in the worker on the public sherpa-onnx sample, then checks the log for every word of its
    /// transcript and for the recording's name. Skipped unless TALKTRACE_MODELS_DIR points at the models.
    /// </summary>
    [Fact]
    public async Task A_real_job_logs_no_transcript_text_and_no_file_name()
    {
        string? modelsDir = Environment.GetEnvironmentVariable(ModelLocator.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelsDir), $"{ModelLocator.EnvironmentVariable} is not set.");
        string recording = Path.Combine(_folder, "Acme budget review.wav");
        File.Copy(Path.Combine(modelsDir!, ModelProfile.Fast.AsrDirectory, "test_wavs", "0.wav"), recording);

        TranscriptResult result = await WorkerClient.RunAsync(
            recording, new TranscribeOptions { Speakers = SpeakerSetting.Auto, NumThreads = 4 }, null,
            TestContext.Current.CancellationToken);

        string log = File.ReadAllText(Path.Combine(_folder, Log.FileName(DateTime.Now)));
        Assert.Contains(" worker ", log);
        Assert.Contains("Job done:", log);
        Assert.DoesNotContain("Acme", log, StringComparison.OrdinalIgnoreCase);
        string[] words = result.Turns
            .SelectMany(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Trim('.', ',', '?', '!', '"', '\''))
            .Where(w => w.Length >= 5)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(words);
        Assert.All(words, w => Assert.DoesNotContain(w, log, StringComparison.OrdinalIgnoreCase));
    }
}
