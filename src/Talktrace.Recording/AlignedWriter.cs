namespace Talktrace.Recording;

/// <summary>A stretch of recording ready to write: the microphone and, for a call, the same length of system audio.</summary>
public readonly record struct AlignedFrames(float[] Microphone, float[]? System)
{
    public int Length => Microphone.Length;
}

/// <summary>
/// Pairs the microphone and system-audio streams sample for sample, on the microphone's clock (DESIGN.md section 9,
/// Capture). The two devices run on separate clocks that drift apart, so the writer keeps a cushion of system audio
/// near <see cref="TargetSamples"/> by dropping or repeating a single sample now and then; a bigger gap (a device
/// change, or a stream that stopped for a while) is closed at once by dropping the surplus or writing zeros. The
/// microphone keeps the same cushion, so neither track is ahead of the other. Thread-safe: the capture threads add,
/// the writer thread takes.
/// </summary>
public sealed class AlignedWriter
{
    /// <summary>200 ms of audio held back on both tracks, so the system track can be kept level without running dry.</summary>
    public const int TargetSamples = SampleRate / 5;
    /// <summary>Within ±50 ms of the target, nothing is corrected: that's ordinary jitter between capture packets.</summary>
    public const int DeadbandSamples = SampleRate / 20;
    /// <summary>More than 150 ms off the target is closed in one step rather than one sample at a time.</summary>
    public const int JumpSamples = SampleRate * 3 / 20;
    /// <summary>System audio held while the microphone delivers nothing is capped at the target plus 2 s.</summary>
    public const int MaxSystemSamples = TargetSamples + 2 * SampleRate;

    public const int SampleRate = 16000;

    private readonly object _lock = new();
    private readonly SampleQueue _microphone = new();
    private readonly SampleQueue? _system;
    private bool _paused;

    /// <param name="twoTracks">True for the call modes (microphone and system audio), false for In person.</param>
    public AlignedWriter(bool twoTracks)
    {
        _system = twoTracks ? new SampleQueue() : null;
    }

    public bool TwoTracks => _system is not null;

    /// <summary>Single samples dropped from the system track because its clock ran fast.</summary>
    public int Drops { get; private set; }

    /// <summary>Single samples repeated on the system track because its clock ran slow.</summary>
    public int Repeats { get; private set; }

    /// <summary>Times a bigger gap was closed at once, by dropping system audio or writing zeros.</summary>
    public int Resyncs { get; private set; }

    public void AddMicrophone(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (!_paused)
                _microphone.Enqueue(samples);
        }
    }

    public void AddSystem(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (_paused || _system is null)
                return;
            _system.Enqueue(samples);
            if (_system.Count > MaxSystemSamples)
                _system.Discard(_system.Count - MaxSystemSamples); // the microphone has stalled; keep the newest
        }
    }

    /// <summary>While paused, nothing is kept; on resume, both tracks start again from empty.</summary>
    public bool Paused
    {
        get { lock (_lock) return _paused; }
        set
        {
            lock (_lock)
            {
                _paused = value;
                ResetLocked();
            }
        }
    }

    /// <summary>Empties both tracks, after a device change or on resume.</summary>
    public void Reset()
    {
        lock (_lock)
            ResetLocked();
    }

    /// <summary>
    /// Takes the microphone samples that have arrived beyond the cushion, and the same number of system samples. Returns
    /// an empty stretch if there is nothing to write yet.
    /// </summary>
    public AlignedFrames Take()
    {
        lock (_lock)
        {
            if (_paused)
                return new AlignedFrames([], _system is null ? null : []);

            if (_system is null)
                return new AlignedFrames(_microphone.Dequeue(_microphone.Count), null);

            int n = _microphone.Count - TargetSamples;
            if (n <= 0)
                return new AlignedFrames([], []);

            float[] microphone = _microphone.Dequeue(n);
            return new AlignedFrames(microphone, TakeSystem(n));
        }
    }

    private float[] TakeSystem(int n)
    {
        SampleQueue system = _system!;
        int after = system.Count - n - TargetSamples; // how far the cushion would end up from its target

        if (after > JumpSamples)
        {
            system.Discard(after);
            Resyncs++;
            return system.Dequeue(n);
        }

        if (after < -JumpSamples)
        {
            // Too little system audio: write zeros for the missing part and rebuild the cushion.
            int take = Math.Max(0, system.Count - TargetSamples);
            take = Math.Min(take, n);
            var output = new float[n];
            system.Dequeue(take).CopyTo(output, n - take);
            Resyncs++;
            return output;
        }

        if (after > DeadbandSamples)
        {
            float[] extra = system.Dequeue(n + 1);
            Drops++;
            int skip = n / 2;
            var output = new float[n];
            extra.AsSpan(0, skip).CopyTo(output);
            extra.AsSpan(skip + 1).CopyTo(output.AsSpan(skip));
            return output;
        }

        if (after < -DeadbandSamples)
        {
            float[] fewer = system.Dequeue(n - 1);
            Repeats++;
            var output = new float[n];
            fewer.CopyTo(output, 0);
            output[n - 1] = n > 1 ? fewer[^1] : 0;
            return output;
        }

        return system.Dequeue(n);
    }

    private void ResetLocked()
    {
        _microphone.Clear();
        _system?.Clear();
    }

    /// <summary>A growable ring buffer of samples.</summary>
    private sealed class SampleQueue
    {
        private float[] _buffer = new float[SampleRate * 4];
        private int _head;

        public int Count { get; private set; }

        public void Enqueue(ReadOnlySpan<float> samples)
        {
            if (Count + samples.Length > _buffer.Length)
                Grow(Count + samples.Length);
            int tail = (_head + Count) % _buffer.Length;
            int first = Math.Min(samples.Length, _buffer.Length - tail);
            samples[..first].CopyTo(_buffer.AsSpan(tail));
            samples[first..].CopyTo(_buffer);
            Count += samples.Length;
        }

        public float[] Dequeue(int count)
        {
            count = Math.Min(count, Count);
            var output = new float[count];
            int first = Math.Min(count, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(output);
            _buffer.AsSpan(0, count - first).CopyTo(output.AsSpan(first));
            Discard(count);
            return output;
        }

        public void Discard(int count)
        {
            count = Math.Min(count, Count);
            _head = (_head + count) % _buffer.Length;
            Count -= count;
        }

        public void Clear()
        {
            _head = 0;
            Count = 0;
        }

        private void Grow(int needed)
        {
            var bigger = new float[Math.Max(needed, _buffer.Length * 2)];
            int first = Math.Min(Count, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(bigger);
            _buffer.AsSpan(0, Count - first).CopyTo(bigger.AsSpan(first));
            _buffer = bigger;
            _head = 0;
        }
    }
}
