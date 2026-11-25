using Talktrace.Core.Logging;
using Talktrace.Recording;

namespace Talktrace.App;

/// <summary>
/// Opens the chosen microphone, and for a call the PC's sound, only to show their levels before a recording starts
/// (DESIGN.md section 9, item 2). Nothing is kept.
/// </summary>
public sealed class LevelMonitor : IDisposable
{
    private readonly AudioInput? _microphone;
    private readonly AudioInput? _systemAudio;

    private LevelMonitor(AudioDevice? microphone, bool systemAudio)
    {
        string? micProblem = null, systemProblem = null;
        _microphone = microphone is null ? null : TryOpen(() => AudioInput.OpenMicrophone(microphone), out micProblem);
        _systemAudio = systemAudio ? TryOpen(AudioInput.OpenSystemAudio, out systemProblem) : null;
        Problem = micProblem ?? systemProblem;
    }

    /// <summary>Opens the meters off the UI thread: Windows activates system audio asynchronously.</summary>
    public static Task<LevelMonitor> OpenAsync(AudioDevice? microphone, bool systemAudio) =>
        Task.Run(() => new LevelMonitor(microphone, systemAudio));

    /// <summary>Why a device couldn't be opened, in plain language, or null.</summary>
    public string? Problem { get; }

    public (float Microphone, float System) TakeLevels() => (_microphone?.TakePeak() ?? 0, _systemAudio?.TakePeak() ?? 0);

    public void Dispose()
    {
        _microphone?.Dispose();
        _systemAudio?.Dispose();
    }

    private static AudioInput? TryOpen(Func<AudioInput> open, out string? problem)
    {
        problem = null;
        AudioInput? input = null;
        try
        {
            input = open();
            input.Start();
            return input;
        }
        catch (RecordingException e)
        {
            input?.Dispose();
            Log.Warning("A level meter couldn't open its device.", e);
            problem = e.Message;
            return null;
        }
    }
}
