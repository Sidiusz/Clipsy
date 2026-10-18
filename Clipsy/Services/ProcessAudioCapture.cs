using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Clipsy.Services;

/// <summary>Records only what one process tree plays (WASAPI process loopback) as raw 48 kHz stereo s16le,
/// aligned to the video timeline: nothing before <see cref="Resume"/>, paused time cut, silent gaps filled.</summary>
public sealed class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * 2;
    // Gaps shorter than this are packet jitter, not silence.
    private const long GapToleranceFrames = SampleRate / 50;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly FileStream _out;
    private readonly AutoResetEvent _packetReady = new(false);
    private readonly Thread _thread;
    private readonly object _gate = new();
    private volatile bool _running = true;

    private long _timelineStart = -1;   // Stopwatch ticks when the video started
    private long _pausedSince = -1;
    private long _pausedTotal;
    private long _framesWritten;

    public string OutputPath { get; }

    /// <summary>Process loopback exists from Windows 10 2004 (build 19041).</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

    private ProcessAudioCapture(IAudioClient client, IAudioCaptureClient capture, string outputPath)
    {
        _client = client;
        _capture = capture;
        OutputPath = outputPath;
        _out = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        Check(_client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle()));
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Clipsy.AppAudio", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Opens the loopback stream for <paramref name="processId"/> and its children; throws when Windows refuses.</summary>
    public static ProcessAudioCapture Start(int processId, string outputPath)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Process audio capture needs Windows 10 2004 or newer.");
        var client = Activate(processId);
        var wfx = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = Channels, nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * BytesPerFrame, nBlockAlign = BytesPerFrame, wBitsPerSample = 16,
        };
        IntPtr pwfx = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        try
        {
            Marshal.StructureToPtr(wfx, pwfx, false);
            Check(client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                2_000_000, 0, pwfx, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(pwfx); }

        var iid = typeof(IAudioCaptureClient).GUID;
        Check(client.GetService(ref iid, out var service));
        var capture = new ProcessAudioCapture(client, (IAudioCaptureClient)service, outputPath);
        Check(client.Start());
        capture._thread.Start();
        return capture;
    }

    /// <summary>The video is (again) recording: audio from now on belongs to the timeline.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_timelineStart < 0) _timelineStart = now;
            else if (_pausedSince >= 0) _pausedTotal += now - _pausedSince;
            _pausedSince = -1;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_timelineStart >= 0 && _pausedSince < 0) _pausedSince = Stopwatch.GetTimestamp();
        }
    }

    private void CaptureLoop()
    {
        try
        {
            while (_running)
            {
                _packetReady.WaitOne(100);
                while (_running && _capture.GetNextPacketSize(out uint frames) >= 0 && frames > 0)
                {
                    Check(_capture.GetBuffer(out IntPtr data, out uint count, out uint flags, out _, out ulong qpc100ns));
                    try
                    {
                        long at = qpc100ns != 0
                            ? (long)(qpc100ns * (double)Stopwatch.Frequency / 10_000_000)
                            : Stopwatch.GetTimestamp();
                        Write(data, (int)count, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0, at);
                    }
                    finally { _capture.ReleaseBuffer(count); }
                }
            }
        }
        catch (Exception ex) when (_running)
        {
            Diagnostics.Log("App audio capture stopped", ex);
        }
    }

    private void Write(IntPtr data, int frames, bool silent, long at)
    {
        long expected;
        lock (_gate)
        {
            if (_timelineStart < 0 || _pausedSince >= 0) return;
            expected = (at - _timelineStart - _pausedTotal) * SampleRate / Stopwatch.Frequency;
        }
        if (expected + frames <= 0) return;
        // Process loopback delivers nothing while the app is quiet: fill that time with silence.
        if (expected - _framesWritten > GapToleranceFrames) WriteSilence(expected - _framesWritten);
        int bytes = frames * BytesPerFrame;
        if (silent) WriteSilence(frames);
        else
        {
            unsafe { _out.Write(new ReadOnlySpan<byte>((void*)data, bytes)); }
            _framesWritten += frames;
        }
    }

    private void WriteSilence(long frames)
    {
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        long bytes = frames * BytesPerFrame;
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, zeros.Length);
            _out.Write(zeros[..n]);
            bytes -= n;
        }
        _framesWritten += frames;
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        _packetReady.Set();
        try { _thread.Join(1000); } catch { }
        try { _client.Stop(); } catch { }
        try { _out.Dispose(); } catch (Exception ex) { Diagnostics.Log("App audio file close failed", ex); }
        try { Marshal.ReleaseComObject(_capture); } catch { }
        try { Marshal.ReleaseComObject(_client); } catch { }
        _packetReady.Dispose();
    }

    // ---------- Activation ----------

    private static IAudioClient Activate(int processId)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };
        int paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new PROPVARIANT_BLOB { vt = VT_BLOB, cbSize = (uint)paramsSize, pBlobData = pParams }, pVariant, false);
            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, ref iid, pVariant, handler, out var op);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Process audio activation timed out.");
            Check(op.GetActivateResult(out int hr, out object client));
            Check(hr);
            return (IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [ComVisible(true)]
    public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Done.Set();
            return 0;
        }
    }

    private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";
    private const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    private const ushort VT_BLOB = 65;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT_BLOB
    {
        public ushort vt, wReserved1, wReserved2, wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }
}
