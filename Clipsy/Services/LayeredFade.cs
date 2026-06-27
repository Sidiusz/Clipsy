using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Clipsy.Services;

/// <summary>Whole-window alpha fade via WS_EX_LAYERED, masking the black
/// backdrop flash WinUI 3 shows during first compose.</summary>
public static class LayeredFade
{
    private const int  GWL_EXSTYLE      = -20;
    private const int  WS_EX_LAYERED    = 0x00080000;
    private const uint LWA_ALPHA        = 0x00000002;

    /// <summary>Add WS_EX_LAYERED with alpha=0 so the window is invisible until
    /// FadeIn(). Call before the first Activate()/Show().</summary>
    public static void EnableHidden(IntPtr hwnd)
    {
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
    }

    /// <summary>Animate alpha 0 → 255 (cubic ease-out). Call from the UI thread.</summary>
    public static void FadeIn(IntPtr hwnd, int durationMs = 160, Action? onComplete = null)
    {
        if (hwnd == IntPtr.Zero) { onComplete?.Invoke(); return; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        double elapsed = 0;
        timer.Tick += (_, _) =>
        {
            elapsed += 16;
            double t = Math.Min(elapsed / durationMs, 1.0);
            double eased = 1 - Math.Pow(1 - t, 3);
            byte alpha = (byte)(255 * eased);
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            if (t >= 1.0)
            {
                timer.Stop();
                onComplete?.Invoke();
            }
        };
        timer.Start();
    }

    /// <summary>Snap alpha back to 0 for reused windows (e.g. the tray menu
    /// reopened across the app's lifetime).</summary>
    public static void ResetHidden(IntPtr hwnd)
        => SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);

    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);
}
