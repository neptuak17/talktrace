using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Talktrace.App;

/// <summary>Plays a few seconds of a recording on the default playback device, for the speakers panel.</summary>
public sealed class SamplePlayer : IDisposable
{
    private MediaFoundationReader? _reader;
    private WasapiPlayer? _output;

    /// <summary>Raised on the UI thread when a sample ends by itself.</summary>
    public event Action? Finished;

    public bool IsPlaying => _output is not null;

    /// <summary>Plays <paramref name="seconds"/> of <paramref name="path"/> from <paramref name="start"/>, stopping any sample already playing.</summary>
    public void Play(string path, double start, double seconds)
    {
        Stop();
        var reader = new MediaFoundationReader(path);
        WasapiPlayer? output = null;
        try
        {
            reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, start));
            var sample = new OffsetSampleProvider(reader.ToSampleProvider()) { Take = TimeSpan.FromSeconds(seconds) };
            output = new WasapiPlayerBuilder().WithSharedMode().Build();
            output.Init(new SampleToWaveProvider(sample));
            WasapiPlayer player = output;
            output.PlaybackStopped += (_, _) => OnPlaybackStopped(player);
            output.Play();
        }
        catch
        {
            output?.Dispose();
            reader.Dispose();
            throw;
        }
        _reader = reader;
        _output = output;
    }

    public void Stop()
    {
        if (_output is WasapiPlayer output)
        {
            _output = null;
            output.Stop();
            output.Dispose();
        }
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();

    private void OnPlaybackStopped(WasapiPlayer player)
    {
        if (!ReferenceEquals(player, _output))
            return; // stopped by Stop, or replaced by a later sample
        Stop();
        Finished?.Invoke();
    }
}
