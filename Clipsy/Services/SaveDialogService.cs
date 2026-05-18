using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// Save dialog backed by Win32 GetSaveFileNameW. We do not use
/// FileSavePicker because that API can't be pointed at an arbitrary
/// folder — only at the PickerLocationId enum.
/// </summary>
public static class SaveDialogService
{
    public sealed record SavePickResult(string Path);

    public static async Task<SavePickResult?> PickPngSaveAsync(IntPtr hwnd, string initialDir, string suggestedName)
    {
        return await Task.Run(() => PickSync(hwnd, initialDir, suggestedName, "PNG image (*.png)\0*.png\0\0", ".png"));
    }

    private static SavePickResult? PickSync(IntPtr hwnd, string initialDir, string suggestedName, string filter, string defExt)
    {
        const int bufCh = 32768;
        var fileBuf = Marshal.AllocHGlobal(bufCh * sizeof(char));
        try
        {
            byte[] empty = new byte[bufCh * sizeof(char)];
            Marshal.Copy(empty, 0, fileBuf, empty.Length);
            var bytes = System.Text.Encoding.Unicode.GetBytes(suggestedName);
            if (bytes.Length < bufCh * sizeof(char))
            {
                Marshal.Copy(bytes, 0, fileBuf, bytes.Length);
            }

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = hwnd,
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile = fileBuf,
                nMaxFile = bufCh,
                lpstrInitialDir = !string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) ? initialDir : null,
                lpstrTitle = "Save screenshot",
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_EXPLORER | OFN_NOCHANGEDIR,
                lpstrDefExt = defExt.TrimStart('.'),
            };

            if (!GetSaveFileNameW(ref ofn))
            {
                int err = CommDlgExtendedError();
                if (err != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[Clipsy] GetSaveFileName error 0x{err:X}");
                }
                return null;
            }

            var path = Marshal.PtrToStringUni(fileBuf);
            return string.IsNullOrEmpty(path) ? null : new SavePickResult(path!);
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuf);
        }
    }

    public static string MakeTimestampName(string prefix, string extension)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return $"{prefix}_{ts}{ext}";
    }

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_HIDEREADONLY    = 0x00000004;
    private const int OFN_NOCHANGEDIR     = 0x00000008;
    private const int OFN_PATHMUSTEXIST   = 0x00000800;
    private const int OFN_EXPLORER        = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW lpofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();
}
