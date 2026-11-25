using Talktrace.Core.Recordings;

namespace Talktrace.Tests;

public sealed class RecordingSidecarTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Sits_beside_the_recording_with_its_own_extension()
    {
        string recording = Path.Combine(_folder, "Recording 2026-10-29 1405.wav");

        Assert.Equal(Path.Combine(_folder, "Recording 2026-10-29 1405.talktrace.json"), RecordingSidecar.PathFor(recording));
        Assert.Equal(recording, RecordingSidecar.RecordingPathFor(RecordingSidecar.PathFor(recording)));
    }

    [Fact]
    public void Round_trips_every_field()
    {
        string recording = Path.Combine(_folder, "call.wav");
        var sidecar = new RecordingSidecar
        {
            Mode = RecordingMode.CallSpeakers,
            Status = RecordingStatus.Complete,
            StartedAt = new DateTimeOffset(2026, 11, 25, 14, 5, 0, TimeSpan.FromHours(-8)),
            MicrophoneChannel = 0,
            SystemChannel = 1,
            Microphone = "Microphone (Speakerphone)",
            Playback = "Speakers (Speakerphone)",
        };

        sidecar.Write(recording);

        Assert.Equal(sidecar, RecordingSidecar.TryRead(recording));
        Assert.True(sidecar.IsCall);
        string json = File.ReadAllText(RecordingSidecar.PathFor(recording));
        Assert.Contains("\"mode\": \"callSpeakers\"", json);
        Assert.Contains("\"status\": \"complete\"", json);
    }

    [Fact]
    public void In_person_is_not_a_call()
    {
        Assert.False(new RecordingSidecar { Mode = RecordingMode.InPerson }.IsCall);
    }

    [Fact]
    public void A_missing_or_damaged_sidecar_reads_as_none()
    {
        string recording = Path.Combine(_folder, "call.wav");
        Assert.Null(RecordingSidecar.TryRead(recording));

        File.WriteAllText(RecordingSidecar.PathFor(recording), "{ not json");
        Assert.Null(RecordingSidecar.TryRead(recording));
    }
}
