using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// Save dialog backed by Win32 GetSaveFileNameW. We do not use
/// FileSavePicker because that API can't point at an arbitrary folder,
/// only at the PickerLocationId enum.
/// </summary>
public static class SaveDialogService
{
    public sealed record SaveFilter(string Label, string Pattern);
    public sealed record SavePickResult(string Path, int FilterIndex);

    public static Task<SavePickResult?> PickSaveAsync(
        IntPtr hwnd,
        string initialDir,
        string suggestedName,
        IList<SaveFilter> filters,
        string defaultExt)
    {
        // GetSaveFileNameW needs an STA + OLE-initialized thread. ThreadPool
        // workers are MTA and not OLE-init → native AV inside comdlg32.
        var tcs = new TaskCompletionSource<SavePickResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new System.Threading.Thread(() =>
        {
            int oleHr = OleInitialize(IntPtr.Zero);
            try
            {
                var result = PickSync(hwnd, initialDir, suggestedName, filters, defaultExt);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SaveDialogService STA thread", ex);
                tcs.TrySetException(ex);
            }
            finally
            {
                if (oleHr >= 0) OleUninitialize();
            }
        });
        th.SetApartmentState(System.Threading.ApartmentState.STA);
        th.IsBackground = true;
        th.Name = "ClipsySaveDialog";
        th.Start();
        return tcs.Task;
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr pvReserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();

    // Convenience for PNG-only callers (kept for any existing call sites).
    public static Task<SavePickResult?> PickPngSaveAsync(IntPtr hwnd, string initialDir, string suggestedName)
        => PickSaveAsync(hwnd, initialDir, suggestedName,
            new List<SaveFilter> { new("PNG image (*.png)", "*.png") }, ".png");

    private static SavePickResult? PickSync(
        IntPtr hwnd,
        string initialDir,
        string suggestedName,
        IList<SaveFilter> filters,
        string defaultExt)
    {
        const int bufCh = 32768;
        var fileBuf = Marshal.AllocHGlobal(bufCh * sizeof(char));
        try
        {
            byte[] empty = new byte[bufCh * sizeof(char)];
            Marshal.Copy(empty, 0, fileBuf, empty.Length);
            var nameBytes = Encoding.Unicode.GetBytes(suggestedName);
            if (nameBytes.Length < bufCh * sizeof(char))
            {
                Marshal.Copy(nameBytes, 0, fileBuf, nameBytes.Length);
            }

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = hwnd,
                lpstrFilter = BuildFilterString(filters),
                nFilterIndex = 1,
                lpstrFile = fileBuf,
                nMaxFile = bufCh,
                lpstrInitialDir = !string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) ? initialDir : null,
                lpstrTitle = "Save",
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_EXPLORER | OFN_NOCHANGEDIR,
                lpstrDefExt = defaultExt.TrimStart('.'),
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
            if (string.IsNullOrEmpty(path)) return null;
            return new SavePickResult(path!, ofn.nFilterIndex);
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuf);
        }
    }

    private static string BuildFilterString(IList<SaveFilter> filters)
    {
        var sb = new StringBuilder();
        foreach (var f in filters)
        {
            sb.Append(f.Label).Append('\0').Append(f.Pattern).Append('\0');
        }
        sb.Append('\0'); // double-null terminator
        return sb.ToString();
    }

    public static string MakeTimestampName(string prefix, string extension)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return $"{prefix}_{ts}{ext}";
    }

    /// <summary>Pulls the extension from a SaveFilter's pattern such as "*.png".</summary>
    public static string ExtensionFromPattern(string pattern)
    {
        var i = pattern.LastIndexOf('.');
        return i < 0 ? "" : pattern.Substring(i + 1).ToLowerInvariant();
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
