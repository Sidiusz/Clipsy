using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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
                RotateIfLarge(_logPath);
            }
            catch
            {
                _logPath = Path.Combine(Path.GetTempPath(), "clipsy-debug.log");
            }
            return _logPath;
        }
    }

    private const long MaxLogBytes = 2 * 1024 * 1024;

    /// <summary>Keeps one previous generation (name.old.log) once the file passes 2 MB.</summary>
    public static void RotateIfLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxLogBytes) return;
            File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
        }
        catch { }
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                // Lines queued by LogInBackground first, so the file stays in order.
                File.AppendAllText(LogPath, DequeuePending() + FormatLine(message));
            }
        }
        catch { }
        System.Diagnostics.Debug.WriteLine($"[Clipsy] {message}");
    }

    private static readonly ConcurrentQueue<string> _pending = new();
    private static int _drainQueued;

    /// <summary>Log without touching the disk on the calling thread: for the overlay's
    /// UI-thread paths, where a disk busy with game I/O would stall the capture.</summary>
    public static void LogInBackground(string message)
    {
        _pending.Enqueue(FormatLine(message));
        System.Diagnostics.Debug.WriteLine($"[Clipsy] {message}");
        if (Interlocked.Exchange(ref _drainQueued, 1) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(_ => Flush(), null);
    }

    /// <summary>Writes out lines queued by LogInBackground.</summary>
    public static void Flush()
    {
        Volatile.Write(ref _drainQueued, 0);
        try
        {
            lock (_lock)
            {
                var text = DequeuePending();
                if (text.Length > 0) File.AppendAllText(LogPath, text);
            }
        }
        catch { }
    }

    private static string DequeuePending()
    {
        if (_pending.IsEmpty) return string.Empty;
        var sb = new System.Text.StringBuilder();
        while (_pending.TryDequeue(out var line)) sb.Append(line);
        return sb.ToString();
    }

    private static string FormatLine(string message)
        => $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

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
            string caption = "Clipsy";
            try { caption = Clipsy.Localization.Strings.Get("ErrDialogCaption"); } catch { }
            MessageBoxW(IntPtr.Zero, body, caption, 0x00000010 /* MB_ICONERROR */);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd,
        [MarshalAs(UnmanagedType.LPWStr)] string text,
        [MarshalAs(UnmanagedType.LPWStr)] string caption,
        uint type);
}
