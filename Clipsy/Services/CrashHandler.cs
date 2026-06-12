using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>
/// Captures the crashes the managed handlers can't see. WinUI 3 native
/// fail-fasts (0xc0000409) and access violations terminate the process
/// without ever raising a CLR exception, so App.UnhandledException /
/// AppDomain.UnhandledException stay silent — the app just vanishes.
///
/// This installs a top-level native exception filter that, on such a crash,
/// writes the faulting exception code + address to debug.log and drops a
/// minidump (%LOCALAPPDATA%\Clipsy\crash_*.dmp) for post-mortem with
/// dotnet-dump / WinDbg. It also logs a start breadcrumb and a clean
/// ProcessExit marker, so a missing exit marker tells us the process was
/// hard-killed rather than exiting cleanly.
///
/// Note: pure __fastfail (int 0x29) bypasses even this filter by design;
/// those still only surface via WER. The fix for the 1.6 input fail-fast
/// was the WindowsAppSDK 1.7 upgrade. This handler covers everything else
/// (AVs, heap corruption surfaced as exceptions, stack overflow, etc.).
/// </summary>
public static class CrashHandler
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            var ver = typeof(CrashHandler).Assembly.GetName().Version;
            Diagnostics.Log($"=== Clipsy start pid={Environment.ProcessId} v{ver} ===");
        }
        catch { }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Diagnostics.Log($"ProcessExit (clean) pid={Environment.ProcessId}");

        try
        {
            // Keep the delegate rooted for the process lifetime; if it gets
            // GC'd the native side calls into freed memory.
            _filter = NativeFilter;
            SetUnhandledExceptionFilter(_filter);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("CrashHandler.Install (SetUnhandledExceptionFilter)", ex);
        }
    }

    // Rooted reference — do not inline.
    private static TopLevelExceptionFilter? _filter;

    private static int NativeFilter(IntPtr exceptionInfo)
    {
        try
        {
            uint code = 0;
            IntPtr addr = IntPtr.Zero;
            if (exceptionInfo != IntPtr.Zero)
            {
                var ptrs = Marshal.PtrToStructure<EXCEPTION_POINTERS>(exceptionInfo);
                if (ptrs.ExceptionRecord != IntPtr.Zero)
                {
                    var rec = Marshal.PtrToStructure<EXCEPTION_RECORD>(ptrs.ExceptionRecord);
                    code = rec.ExceptionCode;
                    addr = rec.ExceptionAddress;
                }
            }

            string module = ModuleAtAddress(addr);
            Diagnostics.Log(
                $"NATIVE CRASH code=0x{code:X8} addr=0x{addr.ToInt64():X} module={module}");

            string dump = WriteDump(exceptionInfo);
            Diagnostics.Log(dump.Length > 0 ? $"  minidump: {dump}" : "  minidump: FAILED");
        }
        catch
        {
            // Never throw from inside the crash filter.
        }

        // Let the default handler (WER) also run, then the process terminates.
        return EXCEPTION_CONTINUE_SEARCH;
    }

    private static string ModuleAtAddress(IntPtr addr)
    {
        if (addr == IntPtr.Zero) return "?";
        try
        {
            const int GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
            const int GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;
            if (GetModuleHandleEx(
                    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    addr, out IntPtr hModule) && hModule != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (GetModuleFileName(hModule, sb, sb.Capacity) > 0)
                    return Path.GetFileName(sb.ToString());
            }
        }
        catch { }
        return "?";
    }

    private static string WriteDump(IntPtr exceptionInfo)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

            var info = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionInfo,
                ClientPointers = false,
            };
            IntPtr infoPtr = IntPtr.Zero;
            try
            {
                if (exceptionInfo != IntPtr.Zero)
                {
                    infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MINIDUMP_EXCEPTION_INFORMATION>());
                    Marshal.StructureToPtr(info, infoPtr, false);
                }

                // Normal + thread info + unloaded modules + data segments: enough
                // for managed+native stacks without a multi-hundred-MB full dump.
                const int dumpType = (int)(
                    MiniDumpType.WithThreadInfo |
                    MiniDumpType.WithUnloadedModules |
                    MiniDumpType.WithDataSegs |
                    MiniDumpType.WithHandleData);

                bool ok = MiniDumpWriteDump(
                    GetCurrentProcess(), (uint)Environment.ProcessId, fs.SafeFileHandle,
                    dumpType, infoPtr, IntPtr.Zero, IntPtr.Zero);
                return ok ? path : string.Empty;
            }
            finally
            {
                if (infoPtr != IntPtr.Zero) Marshal.FreeHGlobal(infoPtr);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Win32 / Dbghelp interop ──────────────────────────────────────

    private delegate int TopLevelExceptionFilter(IntPtr exceptionInfo);
    private const int EXCEPTION_CONTINUE_SEARCH = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(TopLevelExceptionFilter lpTopLevelExceptionFilter);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleEx(int dwFlags, IntPtr address, out IntPtr phModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleFileName(IntPtr hModule, System.Text.StringBuilder lpFilename, int nSize);

    [DllImport("Dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        int dumpType, IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_POINTERS
    {
        public IntPtr ExceptionRecord;
        public IntPtr ContextRecord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecordChain;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        // Trailing ExceptionInformation[15] omitted — we only read code/address.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINIDUMP_EXCEPTION_INFORMATION
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientPointers;
    }

    [Flags]
    private enum MiniDumpType
    {
        Normal                 = 0x00000000,
        WithDataSegs           = 0x00000001,
        WithHandleData         = 0x00000004,
        WithUnloadedModules    = 0x00000020,
        WithThreadInfo         = 0x00001000,
    }
}
