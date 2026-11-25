using NAudio.Wave;
using Talktrace.Core.Recordings;
using Talktrace.Recording;

namespace Talktrace.Tests;

/// <summary>The recording's WAV file, crash recovery and file names. No devices needed.</summary>
public sealed class RecordingFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static AlignedFrames Stereo(int count, float mic, float system) =>
        new(Enumerable.Repeat(mic, count).ToArray(), Enumerable.Repeat(system, count).ToArray());

    [Fact]
    public void Writes_a_stereo_wav_with_the_microphone_on_the_left()
    {
        string path = Path.Combine(_folder, "call.wav");
        using (var file = new RecordingWavFile(path, channels: 2))
        {
            file.Write(Stereo(16000, 0.5f, -0.25f));
            Assert.Equal(TimeSpan.FromSeconds(1), file.Duration);
        }

        using var reader = new WaveFileReader(path);
        Assert.Equal(new WaveFormat(16000, 16, 2), reader.WaveFormat);
        Assert.Equal(1.0, reader.TotalTime.TotalSeconds, tolerance: 0.001);
        float[] frame = new float[2];
        Assert.Equal(2, reader.ToSampleProvider().Read(frame));
        Assert.Equal(0.5, frame[0], tolerance: 0.001);
        Assert.Equal(-0.25, frame[1], tolerance: 0.001);
    }

    [Fact]
    public void Writes_a_mono_wav_for_in_person()
    {
        string path = Path.Combine(_folder, "room.wav");
        using (var file = new RecordingWavFile(path, channels: 1))
            file.Write(new AlignedFrames(new float[8000], null));

        using var reader = new WaveFileReader(path);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(0.5, reader.TotalTime.TotalSeconds, tolerance: 0.001);
    }

    [Fact]
    public void A_checkpoint_leaves_a_playable_file_while_recording_continues()
    {
        string path = Path.Combine(_folder, "call.wav");
        using var file = new RecordingWavFile(path, channels: 2);
        file.Write(Stereo(32000, 0.1f, 0.1f));
        file.Checkpoint();

        using var copy = new MemoryStream(ReadShared(path));
        using var reader = new WaveFileReader(copy);
        Assert.Equal(2.0, reader.TotalTime.TotalSeconds, tolerance: 0.001);
    }

    [Fact]
    public void Recovers_a_file_cut_off_mid_write()
    {
        string path = Path.Combine(_folder, "call.wav");
        string crashedPath = Path.Combine(_folder, "crashed.wav");
        using (var file = new RecordingWavFile(path, channels: 2))
        {
            file.Write(Stereo(16000, 0.1f, 0.1f));
            file.Checkpoint();
            file.Write(Stereo(16000, 0.1f, 0.1f)); // written after the last checkpoint...
            File.WriteAllBytes(crashedPath, [.. ReadShared(path), 1, 2, 3]); // ...and a half-written frame at the end
        }

        TimeSpan recovered = RecordingWavFile.Recover(crashedPath);

        Assert.True(recovered >= TimeSpan.FromSeconds(1), $"Recovered only {recovered}.");
        using var reader = new WaveFileReader(crashedPath);
        Assert.Equal(recovered.TotalSeconds, reader.TotalTime.TotalSeconds, tolerance: 0.001);
        Assert.Equal(0, (new FileInfo(crashedPath).Length - 44) % 4);
    }

    [Fact]
    public void Refuses_to_recover_a_file_that_isnt_a_talktrace_recording()
    {
        string path = Path.Combine(_folder, "notes.wav");
        File.WriteAllBytes(path, new byte[100]);

        Assert.Throws<InvalidDataException>(() => RecordingWavFile.Recover(path));
    }

    [Fact]
    public void Finds_and_recovers_an_unfinished_recording()
    {
        string path = Path.Combine(_folder, "Recording 2026-11-25 1405.wav");
        using (var file = new RecordingWavFile(path, channels: 2))
            file.Write(Stereo(16000 * 3, 0.1f, 0.1f));
        var started = new DateTimeOffset(2026, 11, 25, 14, 5, 0, TimeSpan.Zero);
        new RecordingSidecar
        {
            Mode = RecordingMode.CallHeadphones, Status = RecordingStatus.Recording, StartedAt = started, SystemChannel = 1,
        }.Write(path);
        string finished = Path.Combine(_folder, "Recording 2026-11-25 1300.wav");
        using (var file = new RecordingWavFile(finished, channels: 1))
            file.Write(new AlignedFrames(new float[16000], null));
        new RecordingSidecar { Mode = RecordingMode.InPerson, Status = RecordingStatus.Complete }.Write(finished);

        UnfinishedRecording unfinished = Assert.Single(RecordingRecovery.Find(_folder));
        Assert.Equal(path, unfinished.Path);
        Assert.Equal(3.0, unfinished.Recorded.TotalSeconds, tolerance: 0.01);

        TimeSpan length = RecordingRecovery.Recover(unfinished);

        Assert.Equal(3.0, length.TotalSeconds, tolerance: 0.01);
        Assert.Equal(RecordingStatus.Complete, RecordingSidecar.TryRead(path)!.Status);
        Assert.Empty(RecordingRecovery.Find(_folder));
    }

    [Fact]
    public void A_missing_recordings_folder_has_nothing_to_recover()
    {
        Assert.Empty(RecordingRecovery.Find(Path.Combine(_folder, "nowhere")));
    }

    [Fact]
    public void Names_recordings_by_date_and_time_without_overwriting()
    {
        var now = new DateTime(2026, 10, 29, 14, 5, 30);
        string first = MeetingRecorder.NewRecordingPath(_folder, now);
        Assert.Equal(Path.Combine(_folder, "Recording 2026-10-29 1405.wav"), first);

        File.WriteAllBytes(first, []);
        Assert.Equal(Path.Combine(_folder, "Recording 2026-10-29 1405 (2).wav"), MeetingRecorder.NewRecordingPath(_folder, now));
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
