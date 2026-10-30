using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Talktrace.Core.Transcripts;

namespace Talktrace.Core.Worker;

/// <summary>A job that failed or stopped. <see cref="Exception.Message"/> is plain language, safe to show as is.</summary>
public sealed class WorkerException(string message, string details) : Exception(message)
{
    /// <summary>What went wrong in technical terms, for Copy details. Never holds transcript text.</summary>
    public string Details { get; } = details;
}

/// <summary>
/// The app's side of the worker protocol (DESIGN.md section 6): runs one job in a <c>Talktrace.Cli --worker</c>
/// process at below-normal priority, launched from the app's own folder.
/// </summary>
public static class WorkerClient
{
    public const string WorkerFileName = "Talktrace.Cli.exe";

    /// <summary>How long a cancelled worker has to exit before it is killed; speaker identification can't stop midway.</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    private const int ErrorTailLength = 4000;

    public static string WorkerPath => Path.Combine(AppContext.BaseDirectory, WorkerFileName);

    /// <summary>Runs the job and returns its result. Cancelling throws <see cref="OperationCanceledException"/> and discards the job.</summary>
    /// <exception cref="WorkerException">The job failed, or the worker stopped without a result.</exception>
    public static async Task<TranscriptResult> RunAsync(
        string inputPath, TranscribeOptions options, IProgress<TranscribeProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string workerPath = WorkerPath;
        if (!File.Exists(workerPath))
            throw new WorkerException($"Talktrace is missing one of its files ({WorkerFileName}). Reinstall Talktrace.", $"Not found: {workerPath}");

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var start = new ProcessStartInfo(workerPath)
        {
            ArgumentList = { WorkerProtocol.WorkerArgument },
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };

        using var process = new Process { StartInfo = start };
        var errorTail = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (errorTail)
            {
                errorTail.AppendLine(e.Data);
                if (errorTail.Length > ErrorTailLength)
                    errorTail.Remove(0, errorTail.Length - ErrorTailLength);
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new WorkerException("Talktrace couldn't start transcribing.", e.ToString());
        }
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already exited; the missing result is reported below.
        }
        process.BeginErrorReadLine();

        Task stopping = Task.CompletedTask;
        TranscriptResult? result = null;
        WorkerError? error = null;
        int ignoredLines = 0;

        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.Serialize(new WorkerJob(inputPath, options)));
            await process.StandardInput.FlushAsync(CancellationToken.None);
        }
        catch (IOException)
        {
            // The worker exited before reading the job; the missing result is reported below.
        }

        using (cancellationToken.Register(() => stopping = StopAsync(process)))
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is string line)
            {
                switch (WorkerProtocol.ParseMessage(line))
                {
                    case WorkerProgress p:
                        progress?.Report(new TranscribeProgress(p.Stage, p.Done, p.Total));
                        break;
                    case WorkerResult r:
                        result = r.Result;
                        break;
                    case WorkerError e:
                        error = e;
                        break;
                    default:
                        ignoredLines++; // never kept: a garbled line could hold transcript text
                        break;
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await stopping;

        cancellationToken.ThrowIfCancellationRequested();
        if (result is not null)
            return result;
        if (error is not null)
            throw new WorkerException(error.Message, error.Details);

        string details;
        lock (errorTail)
        {
            details = $"{WorkerFileName} {WorkerProtocol.WorkerArgument} exited with code {process.ExitCode} (0x{process.ExitCode:X8}) " +
                      $"without a result. Lines on stdout that weren't messages: {ignoredLines}." +
                      (errorTail.Length > 0 ? $"{Environment.NewLine}stderr:{Environment.NewLine}{errorTail}" : "");
        }
        throw new WorkerException("Transcription stopped unexpectedly.", details);
    }

    /// <summary>Asks the worker to stop, and kills it if it hasn't exited within <see cref="CancelGrace"/>.</summary>
    private static async Task StopAsync(Process process)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(WorkerProtocol.Serialize(new WorkerCancel()));
            await process.StandardInput.FlushAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Already exited.
        }

        using var grace = new CancellationTokenSource(CancelGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // Exited at the last moment.
            }
        }
    }
}
