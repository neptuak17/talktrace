using Talktrace.Core.Logging;
using Talktrace.Core.Recordings;

namespace Talktrace.Recording;

/// <summary>A recording whose sidecar still says <see cref="RecordingStatus.Recording"/>: Talktrace stopped before finishing it.</summary>
/// <param name="Recorded">How much audio it holds, from its size.</param>
public sealed record UnfinishedRecording(string Path, RecordingSidecar Sidecar, TimeSpan Recorded);

/// <summary>Finds unfinished recordings and makes them playable again (DESIGN.md section 9, Files; section 3).</summary>
public static class RecordingRecovery
{
    /// <summary>The unfinished recordings in <paramref name="folder"/>, oldest first. Only that folder is checked.</summary>
    public static IReadOnlyList<UnfinishedRecording> Find(string folder)
    {
        var found = new List<UnfinishedRecording>();
        try
        {
            if (!Directory.Exists(folder))
                return found;
            foreach (string sidecarPath in Directory.EnumerateFiles(folder, "*" + RecordingSidecar.Extension))
            {
                if (RecordingSidecar.TryReadFile(sidecarPath) is not { Status: RecordingStatus.Recording } sidecar)
                    continue;
                string recording = RecordingSidecar.RecordingPathFor(sidecarPath);
                var file = new FileInfo(recording);
                if (!file.Exists)
                    continue;
                int channels = sidecar.SystemChannel is null ? 1 : 2;
                found.Add(new UnfinishedRecording(recording, sidecar, RecordingWavFile.DurationFromSize(file.Length, channels)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("The recordings folder couldn't be checked for unfinished recordings.", e);
        }
        return found.OrderBy(r => r.Sidecar.StartedAt).ToList();
    }

    /// <summary>Fixes the file's header from its size and marks it complete. Deletes nothing. Returns its length.</summary>
    public static TimeSpan Recover(UnfinishedRecording recording)
    {
        try
        {
            TimeSpan duration = RecordingWavFile.Recover(recording.Path);
            recording.Sidecar.With(RecordingStatus.Complete).Write(recording.Path);
            Log.Info($"Recovered an unfinished recording: {duration.TotalMinutes:0.0} minutes.");
            return duration;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning("An unfinished recording couldn't be recovered.", e);
            throw new RecordingException(
                e is InvalidDataException
                    ? "This recording is too damaged to recover."
                    : "The recording couldn't be recovered: it may be open in another app, or the folder is read-only.", e);
        }
    }

    private static RecordingSidecar With(this RecordingSidecar sidecar, RecordingStatus status) => sidecar with { Status = status };
}
