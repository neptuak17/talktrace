using System.Text;

namespace Talktrace.Core.Transcripts;

/// <summary>Saves transcripts as UTF-8 text.</summary>
public static class TranscriptFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary><c>&lt;recording name&gt; - transcript.txt</c> in the recording's folder.</summary>
    public static string DefaultPath(string recordingPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(recordingPath))!;
        return Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(recordingPath)} - transcript.txt");
    }

    /// <summary>Writes to <paramref name="path"/>, or if it exists, to "name (2).txt", "name (3).txt" and so on. Returns the path used.</summary>
    public static string SaveNew(string path, string text)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int n = 1; ; n++)
        {
            string candidate = n == 1 ? path : Path.Combine(folder, $"{name} ({n}){extension}");
            try
            {
                using var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, Utf8);
                writer.Write(text);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Taken; try the next number.
            }
        }
    }

    /// <summary>Writes to <paramref name="path"/>, replacing any existing file.</summary>
    public static void Overwrite(string path, string text) => File.WriteAllText(path, text, Utf8);
}
