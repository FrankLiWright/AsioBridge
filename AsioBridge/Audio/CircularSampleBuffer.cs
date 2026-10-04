namespace AsioBridge.Audio;

/// <summary>
/// Lock-free-ish interleaved float sample ring buffer.
/// Capture thread writes, ASIO callback thread reads.
/// </summary>
public sealed class CircularSampleBuffer
{
    private readonly float[] _buffer;
    private readonly object _lock = new();
    private int _writePos;
    private int _readPos;
    private int _count; // samples currently stored (not frames)

    public int CapacitySamples { get; }

    public int Channels { get; }

    /// <summary>Frames currently buffered (one frame = Channels samples).</summary>
    public int BufferedFrames
    {
        get { lock (_lock) return _count / Channels; }
    }

    public CircularSampleBuffer(int channels, int capacityFrames)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        Channels = channels;
        CapacitySamples = channels * capacityFrames;
        _buffer = new float[CapacitySamples];
    }

    /// <summary>
    /// Write interleaved samples. If the buffer would overflow, oldest samples are dropped
    /// so latency does not grow without bound.
    /// </summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        lock (_lock)
        {
            int toWrite = samples.Length;

            // Drop oldest data if it doesn't fit.
            int free = CapacitySamples - _count;
            if (toWrite > free)
            {
                int drop = toWrite - free;
                _readPos = (_readPos + drop) % CapacitySamples;
                _count -= drop;
            }

            int firstChunk = Math.Min(toWrite, CapacitySamples - _writePos);
            samples[..firstChunk].CopyTo(_buffer.AsSpan(_writePos));
            if (toWrite > firstChunk)
            {
                samples[firstChunk..].CopyTo(_buffer.AsSpan(0));
            }
            _writePos = (_writePos + toWrite) % CapacitySamples;
            _count += toWrite;
        }
    }

    /// <summary>
    /// Read exactly <paramref name="samples"/>.Length samples into the destination.
    /// Returns false (and zero-fills) if there is an underrun.
    /// </summary>
    public bool Read(Span<float> samples)
    {
        lock (_lock)
        {
            int want = samples.Length;
            bool ok = _count >= want;
            if (!ok)
            {
                samples.Clear();
                return false;
            }

            int firstChunk = Math.Min(want, CapacitySamples - _readPos);
            _buffer.AsSpan(_readPos, firstChunk).CopyTo(samples);
            if (want > firstChunk)
            {
                _buffer.AsSpan(0, want - firstChunk).CopyTo(samples[firstChunk..]);
            }
            _readPos = (_readPos + want) % CapacitySamples;
            _count -= want;
            return true;
        }
    }

    /// <summary>Drop the oldest <paramref name="frames"/> frames (used to cap latency).</summary>
    public void DropOldestFrames(int frames)
    {
        if (frames <= 0) return;
        lock (_lock)
        {
            int samples = Math.Min(frames * Channels, _count);
            _readPos = (_readPos + samples) % CapacitySamples;
            _count -= samples;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _readPos = 0;
            _writePos = 0;
            _count = 0;
        }
    }
}
