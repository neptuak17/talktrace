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
}
