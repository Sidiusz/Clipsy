using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Clipsy.Services;

/// <summary>Top-level windows that can be recorded: lookup, bounds, title and the process whose audio they play.</summary>
public static class WindowTargets
{
    public sealed record Target(IntPtr Hwnd, Rectangle Bounds, string Title);

    /// <summary>The window the user is in, or null when that is the desktop, the taskbar or Clipsy itself.</summary>
    public static Target? Foreground()
    {
        IntPtr hwnd = GetAncestor(GetForegroundWindow(), GA_ROOT);
        return IsRecordable(hwnd) ? Describe(hwnd) : null;
    }

    /// <summary>Recordable windows front to back, as of now.</summary>
    public static List<Target> VisibleInZOrder()
    {
        var list = new List<Target>();
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
            if (IsRecordable(h)) list.Add(Describe(h));
        return list;
    }

    public static bool Exists(IntPtr hwnd) => IsWindow(hwnd);

    public static Rectangle Bounds(IntPtr hwnd)
    {
        // The DWM frame excludes the invisible resize borders GetWindowRect includes.
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>The process that plays the window's sound: for UWP apps that is the app inside the frame host.</summary>
    public static int AudioProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (ClassName(hwnd) != "ApplicationFrameWindow") return (int)pid;
        uint inner = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint childPid);
            if (childPid == pid) return true;
            inner = childPid;
            return false;
        }, IntPtr.Zero);
        return (int)(inner != 0 ? inner : pid);
    }

    public static Target Describe(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        var title = sb.ToString().Trim();
        if (title.Length == 0)
        {
            try { using var p = Process.GetProcessById(AudioProcessId(hwnd)); title = p.ProcessName; }
            catch { title = "?"; }
        }
        return new Target(hwnd, Bounds(hwnd), title);
    }

    private static bool IsRecordable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;
        var b = Bounds(hwnd);
        return b.Width >= 32 && b.Height >= 32;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private const uint GA_ROOT = 2;
    private const uint GW_HWNDNEXT = 2;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
