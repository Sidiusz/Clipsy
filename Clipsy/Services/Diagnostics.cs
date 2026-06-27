using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>Cheap diagnostics: appends to %LOCALAPPDATA%\Clipsy\debug.log and
/// pops a native MessageBox so errors show even with toasts muted.</summary>
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
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{context}: {ex.GetType().FullName}: {ex.Message}");
        sb.AppendLine($"  HResult: 0x{ex.HResult:X8}");
        if (ex.Data != null && ex.Data.Count > 0)
        {
            foreach (var key in ex.Data.Keys)
                sb.AppendLine($"  Data[{key}]: {ex.Data[key]}");
        }
        sb.AppendLine(ex.StackTrace);
        var inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth++ < 5)
        {
            sb.AppendLine($"  --- Inner({depth}): {inner.GetType().Name}: {inner.Message}");
            sb.AppendLine(inner.StackTrace);
            inner = inner.InnerException;
        }
        Log(sb.ToString());
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
