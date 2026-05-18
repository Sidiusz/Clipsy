using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Clipsy.Services;

public static class SaveDialogService
{
    public static async Task<StorageFile?> PickPngSaveAsync(IntPtr hwnd, string suggestedFolder, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedName,
            DefaultFileExtension = ".png",
        };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });

        try
        {
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            if (!string.IsNullOrEmpty(suggestedFolder) && System.IO.Directory.Exists(suggestedFolder))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(suggestedFolder);
                picker.SuggestedSaveFile = null;
                // SuggestedStartLocation drives initial folder; no API to force absolute path
                // but we set SuggestedFileName so user sees a sensible default name.
                _ = folder;
            }
        }
        catch { /* ignore */ }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSaveFileAsync();
    }

    public static string MakeTimestampName(string prefix, string extension)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return $"{prefix}_{ts}{ext}";
    }
}
