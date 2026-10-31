using System.Text.Json;
using System.Text.Json.Serialization;
using Talktrace.Core.Logging;

namespace Talktrace.Core.Settings;

/// <summary>Where and how big the window was, in device-independent units.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>
/// What the app remembers between launches (DESIGN.md section 10): the options of the last transcription and the
/// window's placement. Null means "use the default".
/// </summary>
public sealed record UserSettings
{
    /// <summary>none, auto, or a count from 2 to 8.</summary>
    public string? Speakers { get; init; }
    public bool IncludeTimestamps { get; init; }
    public double? ClusteringThreshold { get; init; }
    public int? Threads { get; init; }
    public double? MaxSpeechSeconds { get; init; }
    public double? MaxChunkSeconds { get; init; }
    public WindowPlacement? Window { get; init; }

    public static string ToText(SpeakerSetting speakers) => speakers switch
    {
        { Label: false } => "none",
        { Count: int n } => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "auto",
    };

    /// <summary>The setting <paramref name="text"/> names, or null if it names none.</summary>
    public static SpeakerSetting? ParseSpeakers(string? text) => text switch
    {
        "none" => SpeakerSetting.None,
        "auto" => SpeakerSetting.Auto,
        _ when int.TryParse(text, out int n) && n is >= 1 and <= 8 => SpeakerSetting.Exactly(n),
        _ => null,
    };
}

/// <summary>Reads and writes <c>settings.json</c> under <c>%LOCALAPPDATA%\Talktrace</c>.</summary>
public static class SettingsFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Talktrace", "settings.json");

    /// <summary>The saved settings, or the defaults if there are none or the file can't be read.</summary>
    public static UserSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new UserSettings();
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Json) ?? new UserSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("The settings couldn't be read, so the defaults are used.", e);
            return new UserSettings();
        }
    }

    /// <summary>Saves the settings, replacing the file in one step so a crash never leaves half of it. Never throws.</summary>
    public static void Save(string path, UserSettings settings)
    {
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("The settings couldn't be saved.", e);
        }
    }
}
