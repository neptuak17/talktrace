using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Talktrace.Core.Logging;

/// <summary>
/// The shared log (DESIGN.md section 12): one file a day, kept for 14 days, written by the app, the worker and the CLI.
/// Never give it transcript text or audio. Paths passed to <see cref="Redact"/> are replaced in every line, so a
/// recording's name never reaches the log even inside an exception message. Logging never throws, and until
/// <see cref="Start"/> is called nothing is written.
/// </summary>
public static class Log
{
    /// <summary>Points the log somewhere else, for development and tests.</summary>
    public const string EnvironmentVariable = "TALKTRACE_LOG_DIR";

    public const int KeepDays = 14;

    private const string FilePrefix = "talktrace-";
    private const string Redacted = "<recording>";

    // A value shorter than this is too likely to appear in ordinary words to replace.
    private const int ShortestRedaction = 4;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object Lock = new();
    private static readonly List<string> Redactions = [];
    private static string? _folder;
    private static string _source = "";
    private static Mutex? _fileMutex;

    /// <summary><c>%LOCALAPPDATA%\Talktrace\logs</c>, or TALKTRACE_LOG_DIR when it is set.</summary>
    public static string Folder
    {
        get
        {
            string? overrideFolder = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return !string.IsNullOrWhiteSpace(overrideFolder)
                ? Path.GetFullPath(overrideFolder)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talktrace", "logs");
        }
    }

    /// <summary>Starts logging for this process and deletes logs older than <see cref="KeepDays"/> days.</summary>
    /// <param name="source">Tags every line: app, worker or cli.</param>
    public static void Start(string source)
    {
        string folder = Folder;
        lock (Lock)
        {
            _source = source;
            _folder = folder;
            // Every process writes to the same file, one line at a time.
            _fileMutex ??= new Mutex(initiallyOwned: false, @"Local\Talktrace.Log");
        }
        try
        {
            Directory.CreateDirectory(folder);
            DeleteOldFiles(folder, DateTime.Today);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop Talktrace.
        }

        Version? version = typeof(Log).Assembly.GetName().Version;
        Info(string.Create(CultureInfo.InvariantCulture,
            $"Talktrace {version?.ToString(3)} started: {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, " +
            $"{Environment.ProcessorCount} processors, .NET {Environment.Version}."));
    }

    /// <summary>Stops logging and forgets the redactions. For tests.</summary>
    internal static void Stop()
    {
        lock (Lock)
        {
            _folder = null;
            Redactions.Clear();
        }
    }

    /// <summary>
    /// Keeps a file's path out of the log: the full path, its folder, its name and its name without extension are
    /// each replaced by &lt;recording&gt;.
    /// </summary>
    public static void Redact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        lock (Lock)
        {
            foreach (string? value in new[] { full, Path.GetDirectoryName(full), Path.GetFileName(full), Path.GetFileNameWithoutExtension(full) })
            {
                if (value is { Length: >= ShortestRedaction } && !Redactions.Contains(value, StringComparer.OrdinalIgnoreCase))
                    Redactions.Add(value);
            }
            // Longest first, so a full path is replaced whole rather than piece by piece.
            Redactions.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception?.ToString());

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception?.ToString());

    /// <summary>An error whose details were already turned into text, such as a worker's.</summary>
    public static void Error(string message, string? details) => Write("ERROR", message, details);

    private static void Write(string level, string message, string? details)
    {
        DateTime now = DateTime.Now;
        string folder;
        string line;
        lock (Lock)
        {
            if (_folder is null)
                return;
            folder = _folder;
            string text = string.IsNullOrEmpty(details) ? message : $"{message}{Environment.NewLine}{details}";
            line = string.Create(CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-dd HH:mm:ss.fff} {_source} {Environment.ProcessId} {level} {RedactText(text)}{Environment.NewLine}");
        }

        string path = Path.Combine(folder, FileName(now));
        bool owned = false;
        try
        {
            try
            {
                owned = _fileMutex!.WaitOne(TimeSpan.FromSeconds(1));
            }
            catch (AbandonedMutexException)
            {
                owned = true; // another process died holding it; the file is still fine to append to
            }
            File.AppendAllText(path, line, Utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop Talktrace.
        }
        finally
        {
            if (owned)
                _fileMutex!.ReleaseMutex();
        }
    }

    private static string RedactText(string text)
    {
        foreach (string value in Redactions)
            text = text.Replace(value, Redacted, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    internal static string FileName(DateTime day) => $"{FilePrefix}{day:yyyy-MM-dd}.log";

    /// <summary>Deletes log files dated more than <see cref="KeepDays"/> days before <paramref name="today"/>.</summary>
    internal static void DeleteOldFiles(string folder, DateTime today)
    {
        foreach (string file in Directory.EnumerateFiles(folder, $"{FilePrefix}*.log"))
        {
            string date = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day)
                && day < today.Date.AddDays(-KeepDays))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Open in another process; try again next time.
                }
            }
        }
    }
}
