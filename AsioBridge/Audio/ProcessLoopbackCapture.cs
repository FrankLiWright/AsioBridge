using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AsioBridge.Audio;

/// <summary>
/// Per-process WASAPI loopback capture (Windows 10 2004+ / Windows 11).
/// Uses VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK + AUDIOCLIENT_ACTIVATION_PARAMS.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private readonly ProcessLoopbackSource _source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public event EventHandler<WaveInEventArgs>? DataAvailable
    {
        add => _source.DataAvailable += value;
        remove => _source.DataAvailable -= value;
    }

    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add => _source.RecordingStopped += value;
        remove => _source.RecordingStopped -= value;
    }

    /// <param name="desiredFormat">Capture format. If null, 48 kHz float stereo is used.</param>
    public ProcessLoopbackCapture(int processId, bool includeChildProcesses, WaveFormat? desiredFormat = null)
    {
        _source = new ProcessLoopbackSource(processId, includeChildProcesses, desiredFormat);
    }

    public void StartRecording() => _source.Start();
    public void StopRecording() => _source.Stop();
    public void Dispose() => _source.Dispose();
}

internal sealed class ProcessLoopbackSource : IDisposable
{
    /// <summary>Device path required for process-loopback activation.</summary>
    private const string VadProcessLoopback = @"VAD\Process_Loopback";

    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntStreamflagsEventcallback = 0x00040000;

    private readonly int _pid;
    private readonly bool _includeChildren;
    private readonly WaveFormat _waveFormat;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private IntPtr _eventHandle;
    private Thread? _thread;
    private volatile bool _running;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat => _waveFormat;

    public ProcessLoopbackSource(int pid, bool includeChildren, WaveFormat? desiredFormat)
    {
        _pid = pid;
        _includeChildren = includeChildren;
        _waveFormat = desiredFormat != null
            ? Normalize(desiredFormat)
            : WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    private static WaveFormat Normalize(WaveFormat fmt)
    {
        // Shared-mode WASAPI process loopback works most reliably with float32.
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
            return fmt;
        return WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, fmt.Channels);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"ProcessLoopback({_pid})",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_eventHandle != IntPtr.Zero)
        {
            NativeMethods.SetEvent(_eventHandle);
        }
        if (_thread != null)
        {
            _thread.Join(3000);
            _thread = null;
        }
    }

    public void Dispose()
    {
        Stop();
        CleanupCom();
    }

    private void CaptureLoop()
    {
        try
        {
            Initialize();

            var buffer = new byte[_waveFormat.AverageBytesPerSecond / 4]; // ~250ms max chunk
            while (_running)
            {
                NativeMethods.WaitForSingleObject(_eventHandle, 200);
                if (!_running) break;

                int hr = _captureClient!.GetNextPacketSize(out uint packetLength);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                while (packetLength > 0)
                {
                    hr = _captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out AudioClientBufferFlags flags, out _, out _);
                    if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                    int bytes = (int)(numFrames * (uint)_waveFormat.BlockAlign);
                    if (bytes > buffer.Length) buffer = new byte[bytes];

                    if ((flags & AudioClientBufferFlags.Silent) != 0 || dataPtr == IntPtr.Zero)
                    {
                        Array.Clear(buffer, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(dataPtr, buffer, 0, bytes);
                    }

                    hr = _captureClient.ReleaseBuffer(numFrames);
                    if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                    if (bytes > 0 && DataAvailable != null)
                    {
                        DataAvailable(this, new WaveInEventArgs(buffer, bytes));
                    }

                    hr = _captureClient.GetNextPacketSize(out packetLength);
                    if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                }
            }

            try { _audioClient?.Stop(); } catch { /* ignore */ }
            RecordingStopped?.Invoke(this, new StoppedEventArgs(null));
        }
        catch (Exception ex)
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
        }
    }

    private void Initialize()
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopback = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)_pid,
                ProcessLoopbackMode = _includeChildren
                    ? ProcessLoopbackMode.IncludeTargetProcessTree
                    : ProcessLoopbackMode.ExcludeTargetProcessTree,
            },
        };

        int paramSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramPtr = Marshal.AllocHGlobal(paramSize);
        try
        {
            Marshal.StructureToPtr(activationParams, paramPtr, false);
            var propVariant = new PropVariant
            {
                vt = 65, // VT_BLOB
                blob = new Blob { cbSize = (uint)paramSize, pBlobData = paramPtr },
            };

            Guid iid = typeof(IAudioClient).GUID;
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new ActivateCompletionHandler(tcs);

            int hr = NativeMethods.ActivateAudioInterfaceAsync(
                VadProcessLoopback,
                ref iid,
                ref propVariant,
                handler,
                out IntPtr operationPtr);

            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            if (operationPtr == IntPtr.Zero && handler.OperationPtr == IntPtr.Zero)
                throw new InvalidOperationException("ActivateAudioInterfaceAsync returned null operation.");

            if (!tcs.Task.Wait(5000))
                throw new TimeoutException("ActivateAudioInterfaceAsync timed out.");

            // Prefer the pointer captured by the completion callback; fall back to the out param.
            IntPtr op = handler.OperationPtr != IntPtr.Zero ? handler.OperationPtr : operationPtr;

            // IActivateAudioInterfaceAsyncOperation::GetActivateResult is vtable slot 3.
            IntPtr vtable = Marshal.ReadIntPtr(op, 0);
            IntPtr slot = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            var getActivateResult = Marshal.GetDelegateForFunctionPointer<GetActivateResultFn>(slot);

            hr = getActivateResult(op, out int activateHr, out IntPtr activatedPtr);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            if (activateHr != 0) Marshal.ThrowExceptionForHR(activateHr);
            if (activatedPtr == IntPtr.Zero)
                throw new InvalidOperationException("Process loopback activation returned null IAudioClient.");

            _audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(activatedPtr);
            Marshal.Release(activatedPtr);

            // Format is chosen by us — GetMixFormat is E_NOTIMPL on this virtual device.
            IntPtr fmtPtr = WaveFormat.MarshalToPtr(_waveFormat);
            try
            {
                hr = _audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    (AudioClientStreamFlags)(AudclntStreamflagsLoopback | AudclntStreamflagsEventcallback),
                    50_000_000, // 500ms buffer (100-ns units)
                    0,
                    fmtPtr,
                    Guid.Empty);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(fmtPtr);
            }

            _eventHandle = NativeMethods.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
            if (_eventHandle == IntPtr.Zero)
                throw new InvalidOperationException("CreateEvent failed.");

            hr = _audioClient.SetEventHandle(_eventHandle);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            Guid iidCapture = typeof(IAudioCaptureClient).GUID;
            hr = _audioClient.GetService(ref iidCapture, out IntPtr capPtr);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capPtr);
            Marshal.Release(capPtr);

            hr = _audioClient.Start();
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(paramPtr);
        }
    }

    private void CleanupCom()
    {
        if (_eventHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }

        if (_captureClient != null)
        {
            try { Marshal.ReleaseComObject(_captureClient); } catch { /* ignore */ }
            _captureClient = null;
        }
        if (_audioClient != null)
        {
            try { _audioClient.Stop(); } catch { /* ignore */ }
            try { Marshal.ReleaseComObject(_audioClient); } catch { /* ignore */ }
            _audioClient = null;
        }
    }
}

#region COM / PInvoke

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    ExcludeTargetProcessTree = 0,
    IncludeTargetProcessTree = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopback;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Blob
{
    public uint cbSize;
    public IntPtr pBlobData;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public short vt;
    [FieldOffset(8)] public Blob blob;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetActivateResultFn(IntPtr thisPtr, out int activateResult, out IntPtr activatedInterface);

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long bufferDuration, long periodicity, IntPtr streamFormat, Guid audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint numBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out int numPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, out IntPtr service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out AudioClientBufferFlags bufferFlags, out long devicePosition, out long qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport]
[Guid("CDFACE3A-0F93-4F0C-81C3-B55B2A14B86C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IntPtr activateOperation);
}

internal sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
{
    private readonly TaskCompletionSource<int> _tcs;

    public IntPtr OperationPtr;

    public ActivateCompletionHandler(TaskCompletionSource<int> tcs) => _tcs = tcs;

    public int ActivateCompleted(IntPtr activateOperation)
    {
        OperationPtr = activateOperation;
        _tcs.TrySetResult(0);
        return 0;
    }
}

internal static class NativeMethods
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IntPtr activationOperation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

#endregion
