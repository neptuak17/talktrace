using System.Diagnostics;
using Talktrace.Core.Logging;
using Talktrace.Core.Transcripts;

namespace Talktrace.Core.Worker;

/// <summary>
/// The worker's side of the protocol, run by <c>Talktrace.Cli --worker</c>: reads one job, runs it, and writes
/// progress, then a result or an error.
/// </summary>
public static class WorkerHost
{
    // Exit codes
    public const int Succeeded = 0;
    public const int Failed = 1;
    public const int NoJob = 2;
    public const int Cancelled = 3;

    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        var writer = new MessageWriter(output);
        if (WorkerProtocol.ParseCommand(await input.ReadLineAsync()) is not WorkerJob job)
        {
            writer.Write(new WorkerError("Talktrace's worker was started without a job.", "The first line on stdin wasn't a job."));
            return NoJob;
        }

        using var cancel = new CancellationTokenSource();
        _ = WatchForCancelAsync(input, cancel);

        try
        {
            TranscriptResult result = await Transcriber.TranscribeAsync(job.InputPath, job.Options, new ProgressWriter(writer), cancel.Token);
            writer.Write(new WorkerResult(result));
            return Succeeded;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Log.Info("Job cancelled.");
            return Cancelled;
        }
        catch (TalktraceException e)
        {
            Log.Error($"Job failed: {e.Message}", e);
            writer.Write(new WorkerError(e.Message, e.ToString()));
            return Failed;
        }
        catch (Exception e)
        {
            Log.Error("Job failed with an unexpected error.", e);
            writer.Write(new WorkerError("Transcription failed because of an unexpected error.", e.ToString()));
            return Failed;
        }
    }

    /// <summary>
    /// Cancels on a cancel command. When stdin closes, the app has gone, and the job carries on so that its
    /// transcript is still saved.
    /// </summary>
    private static async Task WatchForCancelAsync(TextReader input, CancellationTokenSource cancel)
    {
        try
        {
            while (await input.ReadLineAsync() is string line)
            {
                if (WorkerProtocol.ParseCommand(line) is WorkerCancel)
                {
                    cancel.Cancel();
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // stdin closed, or the job has already finished
        }
    }

    /// <summary>Writes one message per line. Once the app has gone and the pipe is closed, messages are dropped.</summary>
    private sealed class MessageWriter(TextWriter output)
    {
        private readonly object _lock = new();
        private bool _closed;

        public void Write(WorkerMessage message)
        {
            lock (_lock)
            {
                if (_closed)
                    return;
                try
                {
                    output.WriteLine(WorkerProtocol.Serialize(message));
                    output.Flush();
                }
                catch (IOException)
                {
                    _closed = true;
                }
            }
        }
    }

    /// <summary>Sends each stage's start and end, and at most ten updates a second in between.</summary>
    private sealed class ProgressWriter(MessageWriter writer) : IProgress<TranscribeProgress>
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
        private readonly object _lock = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TranscribeProgress? _last;
        private TimeSpan _lastSent;

        public void Report(TranscribeProgress value)
        {
            lock (_lock)
            {
                if (value == _last)
                    return;
                bool milestone = _last?.Stage != value.Stage || value.Done >= value.Total;
                if (!milestone && _clock.Elapsed - _lastSent < Interval)
                    return;
                _last = value;
                _lastSent = _clock.Elapsed;
                writer.Write(new WorkerProgress(value.Stage, value.Done, value.Total));
            }
        }
    }
}
