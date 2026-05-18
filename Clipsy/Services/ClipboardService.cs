using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Clipsy.Services;

public static class ClipboardService
{
    public static async Task SetImageAsync(byte[] pngBytes)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        var dp = new DataPackage();
        dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(ras));
        Clipboard.SetContent(dp);
        Clipboard.Flush();
    }
}
