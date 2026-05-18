using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>
/// Cheap diagnostics: appends to %LOCALAPPDATA%\Clipsy\debug.log and pops a
/// native Win32 MessageBox so the user gets a copy-pasteable error even when
/// the tray balloon notification is muted.
/// </summary>
public static class Diagnostics
{
    private static readonly object _lock = new();
    private static string? _logPath;

    private static string LogPath
    {
        get
        {
            if (_logPath != null) return _logPath;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "debug.log");
            }
            catch
            {
                _logPath = Path.Combine(Path.GetTempPath(), "clipsy-debug.log");
            }
            return _logPath;
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { }
        System.Diagnostics.Debug.WriteLine($"[Clipsy] {message}");
    }

    public static void Log(string context, Exception ex)
    {
        Log($"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    public static void Show(string context, Exception ex)
    {
        Log(context, ex);
        try
        {
            var body = $"{context}\n\n{ex.GetType().Name}: {ex.Message}\n\nFull details: {LogPath}";
            MessageBoxW(IntPtr.Zero, body, "Clipsy error", 0x00000010 /* MB_ICONERROR */);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd,
        [MarshalAs(UnmanagedType.LPWStr)] string text,
        [MarshalAs(UnmanagedType.LPWStr)] string caption,
        uint type);
}
