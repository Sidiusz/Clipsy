using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Clipsy.Services;

/// <summary>Clipboard writes. Runs synchronously on the caller's thread: OLE clipboard needs
/// the STA thread that called it, and an await would resume elsewhere in the CLI.</summary>
public static class ClipboardService
{
    private const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);
    private const int Attempts = 6;

    public static Task SetTextAsync(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        SetWithRetry(dp);
        return Task.CompletedTask;
    }

    public static Task SetImageAsync(byte[] pngBytes)
    {
        var ras = new InMemoryRandomAccessStream();
        // Not disposed: closing the adapter would close the stream the DataPackage reads.
        var s = ras.AsStreamForWrite();
        s.Write(pngBytes, 0, pngBytes.Length);
        s.Flush();
        ras.Seek(0);
        var dp = new DataPackage();
        dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(ras));
        SetWithRetry(dp);
        return Task.CompletedTask;
    }

    // Another app (clipboard managers, RDP) may hold the clipboard open for a moment.
    private static void SetWithRetry(DataPackage dp)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(dp);
                Clipboard.Flush();
                return;
            }
            catch (COMException ex) when (ex.HResult == CLIPBRD_E_CANT_OPEN && attempt < Attempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }
}
