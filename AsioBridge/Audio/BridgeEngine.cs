using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AsioBridge.Audio;

public enum CaptureMode
{
    SystemLoopback,
    ProcessLoopback,
}

public sealed class BridgeOptions
{
    public CaptureMode Mode { get; set; } = CaptureMode.SystemLoopback;
    public int TargetProcessId { get; set; }
    public bool IncludeChildProcesses { get; set; } = true;
    public string? AsioDriverName { get; set; }
    public int BufferMs { get; set; } = 50;
}

public sealed class BridgeStatus
{
    public bool IsRunning { get; init; }
    public string CaptureFormat { get; init; } = "";
    public string AsioFormat { get; init; } = "";
    public int BufferedMs { get; init; }
    public int Underruns { get; init; }
    public float PeakLeft { get; init; }
    public float PeakRight { get; init; }
    public float CapturePeak { get; init; }
    public long PullCount { get; init; }
    public long CaptureCount { get; init; }
}

/// <summary>
/// Bridges WASAPI capture (system or per-process) into an ASIO output device
/// through a ring buffer, resampling when sample rates differ.
/// </summary>
public sealed class BridgeEngine : IDisposable
{
    private readonly object _gate = new();

    private CircularSampleBuffer? _ring;
    private ProcessLoopbackCapture? _processCapture;
    private WasapiLoopbackCapture? _systemCapture;
    private AsioOut? _asioOut;
    private WaveFormat? _captureFormat;
    private int _asioRate;
    private int _asioChannels;
    private int _targetBufferMs = 50;
    private int _underruns;
    private float _peakL, _peakR;
    private float _capPeak;
    private long _pullCount;
    private long _capCount;
    private long _peakTicks;
    private volatile bool _running;
    private int _stopPending;

    public bool IsRunning => _running;

    public event Action<BridgeStatus>? StatusChanged;
    public event Action<string>? Error;

    public static IReadOnlyList<string> GetAsioDriverNames()
    {
        try
        {
            return AsioOut.GetDriverNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Start(BridgeOptions options)
    {
        lock (_gate)
        {
            if (_running) return;
            if (string.IsNullOrWhiteSpace(options.AsioDriverName))
                throw new InvalidOperationException("请选择 ASIO 设备。");

            try
            {
                StartCore(options);
                _running = true;
            }
            catch (Exception ex)
            {
                StopCore();
                Error?.Invoke(ex.Message);
                throw;
            }
        }
    }

    /// <summary>
    /// Stop capture and ASIO. Safe to call from the UI thread: the potentially
    /// blocking driver/COM teardown runs on a worker with a bounded wait.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopPending, 1) == 1)
            return;

        try
        {
            // Detach live objects under the lock, then tear down outside it.
            ProcessLoopbackCapture? proc;
            WasapiLoopbackCapture? sys;
            AsioOut? asio;
            lock (_gate)
            {
                _running = false;
                proc = _processCapture;
                sys = _systemCapture;
                asio = _asioOut;
                _processCapture = null;
                _systemCapture = null;
                _asioOut = null;
                _ring?.Clear();
                _ring = null;
            }

            // Producer side first so it stops filling the ring.
            if (proc != null)
            {
                proc.DataAvailable -= OnCaptureData;
                try { proc.StopRecording(); } catch { /* ignore */ }
            }
            if (sys != null)
            {
                sys.DataAvailable -= OnCaptureData;
                try { sys.StopRecording(); } catch { /* ignore */ }
            }

            // ASIO Stop/Dispose is the usual freeze point (driver waits on its callback).
            if (asio != null)
            {
                using var done = new ManualResetEventSlim(false);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { asio.Stop(); } catch { /* ignore */ }
                    try { asio.Dispose(); } catch { /* ignore */ }
                    done.Set();
                });
                // Bounded wait: never block the caller (UI) indefinitely.
                done.Wait(2000);
            }

            try { proc?.Dispose(); } catch { /* ignore */ }
            try { sys?.Dispose(); } catch { /* ignore */ }

            StatusChanged?.Invoke(new BridgeStatus { IsRunning = false });
        }
        finally
        {
            Interlocked.Exchange(ref _stopPending, 0);
        }
    }

    private void StartCore(BridgeOptions options)
    {
        _underruns = 0;
        _peakL = _peakR = 0;
        _capPeak = 0;
        _pullCount = 0;
        _capCount = 0;
        _targetBufferMs = Math.Clamp(options.BufferMs, 10, 500);

        // 1) Open ASIO first so we know the target sample rate / channel count.
        _asioOut = new AsioOut(options.AsioDriverName!);
        _asioChannels = Math.Min(2, Math.Max(1, _asioOut.DriverOutputChannelCount));

        // 2) Capture at a rate the ASIO driver supports (avoids resampling when possible).
        int preferredRate = PickAsioSampleRate(_asioOut, 48000);
        WaveFormat captureFormat = WaveFormat.CreateIeeeFloatWaveFormat(preferredRate, _asioChannels);
        StartCapture(options, captureFormat);

        _asioRate = _captureFormat!.SampleRate;
        if (!_asioOut.IsSampleRateSupported(_asioRate))
        {
            _asioRate = PickAsioSampleRate(_asioOut, _captureFormat.SampleRate);
        }

        // 3) Ring buffer: ~4x the requested buffer window, min 100ms.
        int capacityFrames = Math.Clamp(
            (int)(_captureFormat.SampleRate * Math.Max(options.BufferMs, 25) / 1000.0 * 4),
            _captureFormat.SampleRate / 10,
            _captureFormat.SampleRate * 2);
        _ring = new CircularSampleBuffer(_asioChannels, capacityFrames);

        // 4) Pull path: ring → resample (if needed) → ASIO
        ISampleProvider source = new RingSampleProvider(_ring, _captureFormat.SampleRate, _asioChannels, OnUnderrun);
        if (_captureFormat.SampleRate != _asioRate)
        {
            source = new WdlResamplingSampleProvider(source, _asioRate);
        }

        IWaveProvider waveProvider = new MeteredWaveProvider(source, this);

        // 5) Start ASIO using this format (sample rate is taken from WaveFormat).
        _asioOut.Init(waveProvider);
        _asioOut.Play();

        PublishStatus();
    }

    private static int PickAsioSampleRate(AsioOut asio, int preferred)
    {
        foreach (int rate in new[] { preferred, 48000, 44100, 96000, 88200, 192000 })
        {
            try
            {
                if (asio.IsSampleRateSupported(rate)) return rate;
            }
            catch
            {
                // some drivers throw on unknown rates
            }
        }
        return preferred;
    }

    private void StartCapture(BridgeOptions options, WaveFormat preferredFormat)
    {
        if (options.Mode == CaptureMode.ProcessLoopback && options.TargetProcessId > 0)
        {
            try
            {
                var proc = new ProcessLoopbackCapture(options.TargetProcessId, options.IncludeChildProcesses, preferredFormat);
                proc.DataAvailable += OnCaptureData;
                proc.StartRecording();
                _processCapture = proc;
                _captureFormat = proc.WaveFormat;
                return;
            }
            catch (Exception ex)
            {
                Error?.Invoke($"按进程捕获失败（{ex.Message}），已回退到系统全局 loopback。");
            }
        }

        var capture = new WasapiLoopbackCapture();
        capture.DataAvailable += OnCaptureData;
        capture.RecordingStopped += (_, e) =>
        {
            if (_running && e.Exception != null)
                Error?.Invoke($"捕获中断: {e.Exception.Message}");
        };
        capture.StartRecording();
        _systemCapture = capture;
        _captureFormat = capture.WaveFormat;
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        var ring = _ring;
        var fmt = _captureFormat;
        if (ring == null || fmt == null || e.BytesRecorded == 0) return;

        int bytesPerSample = fmt.BitsPerSample / 8;
        if (bytesPerSample <= 0) return;

        int sampleCount = e.BytesRecorded / bytesPerSample;
        if (sampleCount <= 0) return;

        var floats = new float[sampleCount];
        DecodeToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, floats);

        int srcCh = fmt.Channels;
        int dst = ring.Channels;
        if (srcCh == dst)
        {
            ring.Write(floats);
            UpdateCapPeak(floats);
        }
        else
        {
            int frames = sampleCount / srcCh;
            var mapped = new float[frames * dst];
            int copyCh = Math.Min(srcCh, dst);
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < copyCh; c++)
                    mapped[f * dst + c] = floats[f * srcCh + c];
            }
            ring.Write(mapped);
            UpdateCapPeak(mapped);
        }

        // Keep latency near the requested window: if the ring backs up, drop oldest frames.
        int targetFrames = Math.Max(1, fmt.SampleRate * Math.Max(_targetBufferMs, 10) / 1000);
        int maxFrames = targetFrames * 3;
        if (ring.BufferedFrames > maxFrames)
        {
            ring.DropOldestFrames(ring.BufferedFrames - targetFrames);
        }
    }

    private static void DecodeToFloat(ReadOnlySpan<byte> src, WaveFormat format, Span<float> dst)
    {
        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32:
            {
                MemoryMarshal.Cast<byte, float>(src).CopyTo(dst);
                break;
            }
            case WaveFormatEncoding.Pcm when format.BitsPerSample == 16:
            {
                var s = MemoryMarshal.Cast<byte, short>(src);
                int n = Math.Min(s.Length, dst.Length);
                for (int i = 0; i < n; i++)
                    dst[i] = s[i] / 32768f;
                break;
            }
            case WaveFormatEncoding.Pcm when format.BitsPerSample == 24:
            {
                int n = Math.Min(src.Length / 3, dst.Length);
                for (int i = 0; i < n; i++)
                {
                    int v = src[i * 3] | (src[i * 3 + 1] << 8) | (src[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    dst[i] = v / 8388608f;
                }
                break;
            }
            case WaveFormatEncoding.Pcm when format.BitsPerSample == 32:
            {
                var s = MemoryMarshal.Cast<byte, int>(src);
                int n = Math.Min(s.Length, dst.Length);
                for (int i = 0; i < n; i++)
                    dst[i] = s[i] / 2147483648f;
                break;
            }
            default:
                dst.Clear();
                break;
        }
    }

    private void OnUnderrun() => _underruns++;

    private void UpdateCapPeak(ReadOnlySpan<float> samples)
    {
        _capCount++;
        float p = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float a = Math.Abs(samples[i]);
            if (a > p) p = a;
        }
        if (p > _capPeak) _capPeak = p;
    }

    internal void OnPull(ReadOnlySpan<float> samples)
    {
        _pullCount++;
        float pL = 0, pR = 0;
        int ch = _asioChannels;
        for (int i = 0; i + ch <= samples.Length; i += ch)
        {
            float l = samples[i];
            float r = ch > 1 ? samples[i + 1] : l;
            float al = Math.Abs(l);
            float ar = Math.Abs(r);
            if (al > pL) pL = al;
            if (ar > pR) pR = ar;
        }

        long now = Environment.TickCount64;
        if (now - _peakTicks > 100)
        {
            _peakTicks = now;
            _peakL = Math.Max(pL, _peakL * 0.65f);
            _peakR = Math.Max(pR, _peakR * 0.65f);
            if (_running) PublishStatus();
        }
        else
        {
            if (pL > _peakL) _peakL = pL;
            if (pR > _peakR) _peakR = pR;
        }
    }

    private void PublishStatus()
    {
        var ring = _ring;
        int bufferedMs = 0;
        if (ring != null && _captureFormat != null)
        {
            bufferedMs = (int)(ring.BufferedFrames * 1000.0 / Math.Max(1, _captureFormat.SampleRate));
        }

        StatusChanged?.Invoke(new BridgeStatus
        {
            IsRunning = _running,
            CaptureFormat = _captureFormat?.ToString() ?? "",
            AsioFormat = _asioOut != null ? $"{_asioRate} Hz / {_asioChannels} ch / {_asioOut.DriverName}" : "",
            BufferedMs = bufferedMs,
            Underruns = _underruns,
            PeakLeft = _peakL,
            PeakRight = _peakR,
            CapturePeak = _capPeak,
            PullCount = _pullCount,
            CaptureCount = _capCount,
        });
    }

    private void StopCore()
    {
        // Used by Start() failure path — objects were just created, teardown is cheap.
        try { _asioOut?.Stop(); } catch { /* ignore */ }
        try { _asioOut?.Dispose(); } catch { /* ignore */ }
        _asioOut = null;

        if (_processCapture != null)
        {
            _processCapture.DataAvailable -= OnCaptureData;
            try { _processCapture.StopRecording(); } catch { /* ignore */ }
            try { _processCapture.Dispose(); } catch { /* ignore */ }
            _processCapture = null;
        }

        if (_systemCapture != null)
        {
            _systemCapture.DataAvailable -= OnCaptureData;
            try { _systemCapture.StopRecording(); } catch { /* ignore */ }
            try { _systemCapture.Dispose(); } catch { /* ignore */ }
            _systemCapture = null;
        }

        _ring?.Clear();
        _ring = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Pulls interleaved float frames out of the ring buffer.</summary>
internal sealed class RingSampleProvider : ISampleProvider
{
    private readonly CircularSampleBuffer _ring;
    private readonly Action _onUnderrun;

    public WaveFormat WaveFormat { get; }

    public RingSampleProvider(CircularSampleBuffer ring, int sampleRate, int channels, Action onUnderrun)
    {
        _ring = ring;
        _onUnderrun = onUnderrun;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = WaveFormat.Channels;
        count -= count % ch;
        if (count <= 0) return 0;

        bool ok = _ring.Read(buffer.AsSpan(offset, count));
        if (!ok) _onUnderrun();
        return count;
    }
}

/// <summary>Converts sample provider to 32-bit float wave provider and meters peaks.</summary>
internal sealed class MeteredWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly BridgeEngine _engine;
    private readonly WaveFormat _waveFormat;

    public WaveFormat WaveFormat => _waveFormat;

    public MeteredWaveProvider(ISampleProvider source, BridgeEngine engine)
    {
        _source = source;
        _engine = engine;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.Channels);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / 4;
        if (samples <= 0) return 0;

        float[] tmp = count <= 16 * 1024
            ? new float[samples]
            : new float[samples];

        int read = _source.Read(tmp, 0, samples);
        if (read <= 0)
        {
            Array.Clear(buffer, offset, count);
            _engine.OnPull(ReadOnlySpan<float>.Empty);
            return count;
        }

        MemoryMarshal.AsBytes(tmp.AsSpan(0, read)).CopyTo(buffer.AsSpan(offset, read * 4));
        _engine.OnPull(tmp.AsSpan(0, read));
        return read * 4;
    }
}
