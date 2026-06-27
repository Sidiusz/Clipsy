using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>Save dialog via Win32 GetSaveFileNameW (WinRT FileSavePicker can't
/// target an arbitrary folder, and is broker-blocked when elevated).</summary>
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
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr pv);

    // Win32 folder picker. WinRT FolderPicker is broker-hosted and refuses
    // elevated callers, so it can't be used while Clipsy runs as admin.
    public static Task<string?> PickFolderAsync(IntPtr hwnd)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new System.Threading.Thread(() =>
        {
            int oleHr = OleInitialize(IntPtr.Zero);
            try { tcs.TrySetResult(PickFolderSync(hwnd)); }
            catch (Exception ex) { Diagnostics.Log("SaveDialogService folder STA thread", ex); tcs.TrySetResult(null); }
            finally { if (oleHr >= 0) OleUninitialize(); }
        });
        th.SetApartmentState(System.Threading.ApartmentState.STA);
        th.IsBackground = true;
        th.Name = "ClipsyFolderDialog";
        th.Start();
        return tcs.Task;
    }

    private static string? PickFolderSync(IntPtr hwnd)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = hwnd,
            lpszTitle = "Select folder",
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
        };
        IntPtr pidl = SHBrowseForFolderW(ref bi);
        if (pidl == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(260);
            return SHGetPathFromIDListW(pidl, sb) ? sb.ToString() : null;
        }
        finally { CoTaskMemFree(pidl); }
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_EDITBOX          = 0x0010;
    private const uint BIF_NEWDIALOGSTYLE   = 0x0040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

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

            // The owning overlay/HUD is WS_EX_TOPMOST and would cover the dialog,
            // so drop topmost while it's up, then restore.
            bool wasTopmost = hwnd != IntPtr.Zero &&
                (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            if (wasTopmost)
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            try
            {
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
                if (wasTopmost)
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuf);
        }
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

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
