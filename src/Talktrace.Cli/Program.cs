using System.Diagnostics;
using System.Globalization;
using Talktrace.Core;
using Talktrace.Core.Models;
using Talktrace.Core.Transcripts;
using Talktrace.Core.Worker;

// Talktrace command line (DESIGN.md section 11): transcribe one file and print per-stage timings.
// With --worker, it runs one job for the app instead (DESIGN.md section 6).

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args is [WorkerProtocol.WorkerArgument])
{
    var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
    using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8);
    return await WorkerHost.RunAsync(stdin, stdout);
}

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string Usage = """
    Usage: Talktrace.Cli.exe <file> [--speakers auto|none|1..8] [--model fast|multilingual]
                             [--timestamps] [--out <path>] [--threads <n>] [--threshold <x>]
           Talktrace.Cli.exe <file> --benchmark [--minutes <n>]

      --speakers   auto (default) finds the number of speakers; none leaves out speaker labels.
      --threshold  voices closer than this are one speaker, default 0.6: lower finds more speakers.
    """;

string? input = null;
var options = new TranscribeOptions();
bool benchmark = false;
double benchmarkMinutes = 10;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");

        switch (arg)
        {
            case "--speakers":
                options = options with
                {
                    Speakers = Value() switch
                    {
                        "auto" => SpeakerSetting.Auto,
                        "none" => SpeakerSetting.None,
                        var count when int.TryParse(count, out int n) && n is >= 1 and <= 8 => SpeakerSetting.Exactly(n),
                        var other => throw new ArgumentException($"Unknown speaker setting: {other}. Use auto, none or 1 to 8."),
                    },
                };
                break;
            case "--threshold":
                options = options with
                {
                    ClusteringThreshold = double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t is > 0 and < 2
                        ? t
                        : throw new ArgumentException("--threshold needs a number between 0 and 2, such as 0.6."),
                };
                break;
            case "--model":
                string model = Value();
                options = options with
                {
                    Profile = ModelProfile.FromId(model)
                              ?? throw new ArgumentException($"Unknown model: {model}. Use fast or multilingual."),
                };
                break;
            case "--timestamps":
                options = options with { IncludeTimestamps = true };
                break;
            case "--out":
                options = options with { OutputPath = Value() };
                break;
            case "--threads":
                options = options with { NumThreads = PositiveInt(Value(), arg) };
                break;
            case "--benchmark":
                benchmark = true;
                break;
            case "--minutes":
                benchmarkMinutes = PositiveInt(Value(), arg);
                break;
            case "-h" or "--help":
                Console.WriteLine(Usage);
                return 0;
            default:
                if (input is not null || arg.StartsWith('-'))
                    throw new ArgumentException($"Unexpected argument: {arg}");
                input = arg;
                break;
        }
    }

    if (input is null)
        throw new ArgumentException("No input file given.");
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

try
{
    if (benchmark)
        RunBenchmark(input, options, benchmarkMinutes);
    else
        await RunTranscription(input, options);
    return 0;
}
catch (TalktraceException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static async Task RunTranscription(string input, TranscribeOptions options)
{
    var progress = new ConsoleProgress();
    TranscriptResult result = await Transcriber.TranscribeAsync(input, options, progress, CancellationToken.None);
    progress.Finish();

    Console.WriteLine($"Saved: {result.SavedPath}");
    string paragraphs = result.SpeakerLabels
        ? $"Speakers: {result.Speakers.Count} · Turns: {result.Turns.Count}"
        : $"Paragraphs: {result.Turns.Count}";
    Console.WriteLine($"Audio: {TranscriptFormatter.FormatDuration(result.Duration)} · Chunks: {result.ChunkCount} · " +
                      $"{paragraphs} · Model: {result.ModelName} · Threads: {options.NumThreads}");
    Console.WriteLine();
    foreach (StageTiming timing in result.Timings)
        Console.WriteLine($"  {StageName(timing.Stage),-20}{Seconds(timing.Elapsed),9}");
    Console.WriteLine($"  {"Total",-20}{Seconds(result.TotalElapsed),9}   real-time factor {result.RealTimeFactor:F3}");
    Console.WriteLine();
    Console.WriteLine($"Peak memory: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024):N0} MB");
}

static void RunBenchmark(string input, TranscribeOptions options, double minutes)
{
    Console.WriteLine($"Preparing: reading the audio and finding speech in the first {minutes:0} minutes...");
    AsrBenchmark bench = AsrBenchmark.Prepare(input, options, minutes);
    Console.WriteLine($"Timing transcription of {bench.ChunkCount} chunks covering {bench.CoveredSeconds / 60:F1} minutes.");
    Console.WriteLine();
    Console.WriteLine("  Threads  Batch      Time   Real-time factor");

    int bestThreads = 0;
    TimeSpan bestTime = TimeSpan.MaxValue;
    foreach (int threads in new[] { 4, 6, 8, 11 })
    {
        TimeSpan time = bench.Measure(threads, batchSize: 1);
        Print(threads, 1, time);
        if (time < bestTime)
            (bestThreads, bestTime) = (threads, time);
    }

    foreach (int batch in new[] { 2, 4, 8 })
        Print(bestThreads, batch, bench.Measure(bestThreads, batch));

    Console.WriteLine();
    Console.WriteLine($"Peak memory: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024):N0} MB");

    void Print(int threads, int batch, TimeSpan time) =>
        Console.WriteLine($"  {threads,7}  {batch,5}  {Seconds(time),8}   {time.TotalSeconds / bench.CoveredSeconds,16:F3}");
}

static int PositiveInt(string value, string name) =>
    int.TryParse(value, out int n) && n > 0 ? n : throw new ArgumentException($"{name} needs a positive whole number.");

static string Seconds(TimeSpan time) => $"{time.TotalSeconds:F1} s";

static string StageName(TranscribeStage stage) => stage switch
{
    TranscribeStage.ReadingAudio => "Reading audio",
    TranscribeStage.FindingSpeech => "Finding speech",
    TranscribeStage.IdentifyingSpeakers => "Identifying speakers",
    TranscribeStage.Transcribing => "Transcribing",
    TranscribeStage.Saving => "Saving",
    _ => stage.ToString(),
};

/// <summary>Shows the current stage on one console line.</summary>
sealed class ConsoleProgress : IProgress<TranscribeProgress>
{
    private readonly object _lock = new();
    private TranscribeStage? _stage;

    public void Report(TranscribeProgress value)
    {
        lock (_lock)
        {
            if (Console.IsOutputRedirected)
            {
                if (value.Stage != _stage)
                    Console.WriteLine(Describe(value));
            }
            else
            {
                Console.Write($"\r{Describe(value),-60}");
            }
            _stage = value.Stage;
        }
    }

    public void Finish()
    {
        lock (_lock)
        {
            if (!Console.IsOutputRedirected)
                Console.WriteLine();
        }
    }

    private static string Describe(TranscribeProgress p) => p.Stage switch
    {
        TranscribeStage.ReadingAudio => "Reading audio...",
        TranscribeStage.FindingSpeech => $"Finding speech... {p.Done}%",
        TranscribeStage.IdentifyingSpeakers => $"Identifying speakers... {p.Done * 100 / Math.Max(1, p.Total)}%",
        TranscribeStage.Transcribing => $"Transcribing {p.Done} of {p.Total}",
        TranscribeStage.Saving => "Saving...",
        _ => p.Stage.ToString(),
    };
}
