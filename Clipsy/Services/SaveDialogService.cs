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

    // Win32 folder picker. WinRT FolderPicker is broker-hosted and refuses
    // elevated callers, so it can't be used while Clipsy runs as admin.
    public static Task<string?> PickFolderAsync(IntPtr hwnd, string? initialDir, string title)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new System.Threading.Thread(() =>
        {
            int oleHr = OleInitialize(IntPtr.Zero);
            try { tcs.TrySetResult(PickFolderSync(hwnd, initialDir, title)); }
            catch (Exception ex) { Diagnostics.Log("SaveDialogService folder STA thread", ex); tcs.TrySetResult(null); }
            finally { if (oleHr >= 0) OleUninitialize(); }
        });
        th.SetApartmentState(System.Threading.ApartmentState.STA);
        th.IsBackground = true;
        th.Name = "ClipsyFolderDialog";
        th.Start();
        return tcs.Task;
    }

    private static string? PickFolderSync(IntPtr hwnd, string? initialDir, string title)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogCom();
        try
        {
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dialog.SetTitle(title);
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initialDir, IntPtr.Zero, ref iid, out var folder) == 0)
                {
                    dialog.SetFolder(folder);
                    Marshal.ReleaseComObject(folder);
                }
            }
            if (dialog.Show(hwnd) != 0) return null; // cancelled
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private const uint FOS_PICKFOLDERS     = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST   = 0x00000800;
    private const uint SIGDN_FILESYSPATH   = 0x80058000;

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCom { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

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
                lpstrTitle = Clipsy.Localization.Strings.Get("DlgSave"),
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

    /// <summary>Free path in <paramref name="folder"/>: "prefix_yyyyMMdd_HHmmss.ext", then "_2", "_3"... when taken.</summary>
    public static string UniquePath(string folder, string prefix, string extension, bool timestamp = true)
    {
        Directory.CreateDirectory(folder);
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var baseName = timestamp ? Path.GetFileNameWithoutExtension(MakeTimestampName(prefix, ext)) : prefix;
        var path = Path.Combine(folder, baseName + ext);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName}_{i}{ext}");
        return path;
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
