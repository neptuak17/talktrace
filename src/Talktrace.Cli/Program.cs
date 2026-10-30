using System.Diagnostics;
using System.Globalization;
using Talktrace.Core.Asr;
using Talktrace.Core.Audio;
using Talktrace.Core.Models;

// Smoke test for now: transcribe one WAV file with the Fast profile and show word times.
// The full CLI (DESIGN.md, section 11) arrives with the pipeline in Milestones 2 and 3.

Console.OutputEncoding = System.Text.Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const string Usage = "Usage: Talktrace.Cli.exe <file.wav> [--threads <n>] [--words]";

string? input = null;
int threads = Math.Max(1, Environment.ProcessorCount - 1);
bool showWords = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--threads" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n > 0:
            threads = n;
            i++;
            break;
        case "--words":
            showWords = true;
            break;
        case "-h" or "--help":
            Console.WriteLine(Usage);
            return 0;
        default:
            if (input is null && !args[i].StartsWith('-'))
            {
                input = args[i];
                break;
            }
            Console.Error.WriteLine($"Unexpected argument: {args[i]}");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

if (input is null)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

ModelProfile profile = ModelProfile.Fast;
string? modelDirectory = ModelLocator.Find(profile.AsrDirectory);
if (modelDirectory is null)
{
    Console.Error.WriteLine($"The {profile.DisplayName} model isn't installed. Looked in:");
    foreach (string root in ModelLocator.SearchRoots())
        Console.Error.WriteLine($"  {root}");
    Console.Error.WriteLine(@"Run scripts\get-models.ps1 to download it.");
    return 1;
}

try
{
    AudioClip audio = WavReader.Read(input);

    var clock = Stopwatch.StartNew();
    using var recognizer = new ParakeetRecognizer(modelDirectory, threads);
    TimeSpan loadTime = clock.Elapsed;

    clock.Restart();
    RecognizedText result = recognizer.Recognize(audio.Samples, audio.SampleRate);
    TimeSpan decodeTime = clock.Elapsed;

    Console.WriteLine($"Model: {profile.DisplayName} · Threads: {threads}");
    Console.WriteLine($"Audio: {audio.DurationSeconds:F2} s at {audio.SampleRate} Hz");
    Console.WriteLine($"Load: {loadTime.TotalSeconds:F2} s · Decode: {decodeTime.TotalSeconds:F2} s · " +
                      $"Real-time factor: {decodeTime.TotalSeconds / audio.DurationSeconds:F3}");
    Console.WriteLine();
    Console.WriteLine(result.Text);

    if (showWords)
    {
        Console.WriteLine();
        if (!result.HasWordTimes)
        {
            Console.WriteLine("No word times: the model returned no token timestamps.");
        }
        else
        {
            Console.WriteLine("Word times (seconds):");
            foreach (Word word in result.Words)
                Console.WriteLine($"  {word.Start,7:F2} - {word.End,7:F2}  {word.Text}");
        }
    }

    return 0;
}
catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
