using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class ProcessAudioCaptureTests
{
    [Fact]
    public void Captures_only_after_resume_and_gets_this_process_audio()
    {
        // Plays an audible tone, so it only runs when asked for.
        if (Environment.GetEnvironmentVariable("CLIPSY_AUDIO_TEST") != "1" || !ProcessAudioCapture.IsSupported) return;
        var path = Path.Combine(Path.GetTempPath(), $"clipsy_test_{Guid.NewGuid():N}.pcm");
        ProcessAudioCapture capture;
        try { capture = ProcessAudioCapture.Start(Environment.ProcessId, path); }
        catch (COMException) { return; } // no audio endpoint (CI runners)
        try
        {
            var tone = GCHandle.Alloc(Tone(seconds: 1.5), GCHandleType.Pinned);
            try
            {
                PlaySound(tone.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC);
                Thread.Sleep(300);
                Assert.Equal(0, new FileInfo(path).Length); // nothing before the timeline starts
                capture.Resume();
                Thread.Sleep(800);
            }
            finally
            {
                PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                tone.Free();
            }
        }
        finally { capture.Dispose(); }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        Assert.True(bytes.Length > ProcessAudioCapture.SampleRate * 4 / 2, $"only {bytes.Length} bytes captured");
        int loud = 0;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
            if (Math.Abs(BitConverter.ToInt16(bytes, i)) > 20) loud++;
        Assert.True(loud > 1000, $"captured audio is silent ({loud} loud samples)");
    }

    private const uint SND_ASYNC = 0x1, SND_MEMORY = 0x4;

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    // 440 Hz at ~3% amplitude: enough to detect, barely audible.
    private static byte[] Tone(double seconds)
    {
        const int rate = 48000;
        int samples = (int)(rate * seconds);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 1000));
        return ms.ToArray();
    }
}
