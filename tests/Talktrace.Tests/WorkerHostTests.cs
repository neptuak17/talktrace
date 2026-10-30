using Talktrace.Core;
using Talktrace.Core.Worker;

namespace Talktrace.Tests;

/// <summary>The worker's side of the protocol, run in-process on strings. Needs no models.</summary>
public class WorkerHostTests
{
    [Fact]
    public async Task Reports_an_error_without_a_job()
    {
        var output = new StringWriter();

        int exitCode = await WorkerHost.RunAsync(new StringReader("hello\n"), output);

        Assert.Equal(WorkerHost.NoJob, exitCode);
        Assert.IsType<WorkerError>(Assert.Single(Messages(output)));
    }

    [Fact]
    public async Task Reports_a_failed_job_as_a_plain_error()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"talktrace-missing-{Guid.NewGuid():N}.m4a");
        string job = WorkerProtocol.Serialize(new WorkerJob(missing, new TranscribeOptions()));
        var output = new StringWriter();

        int exitCode = await WorkerHost.RunAsync(new StringReader(job + "\n"), output);

        Assert.Equal(WorkerHost.Failed, exitCode);
        var error = Assert.IsType<WorkerError>(Messages(output)[^1]);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Contains(nameof(TalktraceException), error.Details);
    }

    private static List<WorkerMessage> Messages(StringWriter output) =>
        output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => WorkerProtocol.ParseMessage(line) ?? throw new InvalidOperationException($"Not a message: {line}"))
            .ToList();
}
