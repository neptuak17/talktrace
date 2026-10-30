namespace Talktrace.Core.Models;

/// <summary>
/// Finds model files on disk. The app looks in <c>models\</c> next to the exe, then in
/// <c>%LOCALAPPDATA%\Talktrace\models\</c>. Setting TALKTRACE_MODELS_DIR replaces both,
/// for development and tests.
/// </summary>
public static class ModelLocator
{
    public const string EnvironmentVariable = "TALKTRACE_MODELS_DIR";

    public static IReadOnlyList<string> SearchRoots()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return [Path.GetFullPath(overrideRoot)];

        return
        [
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talktrace", "models"),
        ];
    }

    /// <summary>Returns the full path of <paramref name="relativePath"/> under the first root that has it, or null.</summary>
    public static string? Find(string relativePath)
    {
        foreach (string root in SearchRoots())
        {
            string candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Returns <paramref name="path"/> if the file exists and sherpa-onnx can open it.</summary>
    internal static string CheckOpenable(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model file not found: {path}", path);

        // sherpa-onnx takes paths as ANSI strings, so a path outside ASCII may not open.
        if (path.Any(c => c > 127))
            throw new NotSupportedException(
                $"The model path contains characters sherpa-onnx can't open: {path}. " +
                $"Move the models to a plain path and set {EnvironmentVariable} to it.");

        return path;
    }
}
