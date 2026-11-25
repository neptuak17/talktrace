using System.Text.Json;
using System.Text.Json.Serialization;
using Talktrace.Core.Logging;

namespace Talktrace.Core.Recordings;

/// <summary>How a meeting was recorded (DESIGN.md section 9).</summary>
public enum RecordingMode
{
    /// <summary>Microphone only, one track; speakers found by diarization as usual.</summary>
    InPerson,

    /// <summary>Microphone and system audio as two tracks; the microphone hears only me.</summary>
    CallHeadphones,

    /// <summary>As <see cref="CallHeadphones"/>, but the microphone also picks up the others from the speaker, so echo is removed.</summary>
    CallSpeakers,
}

public enum RecordingStatus
{
    /// <summary>Still being written, or the app stopped before finishing it: an unfinished recording.</summary>
    Recording,
    Complete,
}

/// <summary>
/// The <c>&lt;recording&gt;.talktrace.json</c> file beside a recording Talktrace made (DESIGN.md section 9, Files). The
/// pipeline reads it to choose the two-track path; a stereo file without one is an ordinary recording.
/// </summary>
public sealed record RecordingSidecar
{
    public const string Extension = ".talktrace.json";

    public int Version { get; init; } = 1;
    public RecordingMode Mode { get; init; }
    public RecordingStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The channel holding the microphone: 0 (left) in every Talktrace recording.</summary>
    public int MicrophoneChannel { get; init; }

    /// <summary>The channel holding system audio, 1 (right), or null for a mono In person recording.</summary>
    public int? SystemChannel { get; init; }

    public string? Microphone { get; init; }
    public string? Playback { get; init; }

    [JsonIgnore]
    public bool IsCall => Mode is RecordingMode.CallHeadphones or RecordingMode.CallSpeakers;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary><c>Recording 2026-10-29 1405.wav</c> → <c>Recording 2026-10-29 1405.talktrace.json</c>.</summary>
    public static string PathFor(string recordingPath) => Path.ChangeExtension(recordingPath, null) + Extension;

    /// <summary>The recording a sidecar belongs to.</summary>
    public static string RecordingPathFor(string sidecarPath) =>
        sidecarPath[..^Extension.Length] + ".wav";

    /// <summary>The sidecar beside <paramref name="recordingPath"/>, or null if there is none or it can't be read.</summary>
    public static RecordingSidecar? TryRead(string recordingPath) => TryReadFile(PathFor(recordingPath));

    /// <summary>The sidecar at <paramref name="sidecarPath"/>, or null if there is none or it can't be read.</summary>
    public static RecordingSidecar? TryReadFile(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath))
                return null;
            return JsonSerializer.Deserialize<RecordingSidecar>(File.ReadAllText(sidecarPath), Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("A recording's sidecar couldn't be read, so it is treated as an ordinary recording.", e);
            return null;
        }
    }

    /// <summary>Writes the sidecar beside <paramref name="recordingPath"/>, replacing it in one step.</summary>
    public void Write(string recordingPath)
    {
        string path = PathFor(recordingPath);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
