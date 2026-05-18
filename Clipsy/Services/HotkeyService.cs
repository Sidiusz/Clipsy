using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Clipsy.Services;

/// <summary>
/// Registers a global hotkey via RegisterHotKey and routes WM_HOTKEY through a
/// dedicated message-only window subclass. PrintScreen by default.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NONE = 0x0000;
    private const int HOTKEY_ID = 0xC1170;
    private const uint VK_SNAPSHOT = 0x2C; // PrintScreen

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _oldWndProc;
    private Action? _callback;
    private bool _disposed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("HotkeyService must be created on a UI thread.");
        _wndProc = OnWndProc;
        _oldWndProc = SubclassWindow(hwnd, _wndProc);
    }

    public bool RegisterDefault(Action callback)
    {
        _callback = callback;
        // Some shells eat PrintScreen — try MOD_NONE first; fallback to no-op if it fails.
        return RegisterHotKey(_hwnd, HOTKEY_ID, MOD_NONE, VK_SNAPSHOT);
    }

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            var cb = _callback;
            if (cb != null)
            {
                _dispatcher.TryEnqueue(() => cb());
            }
        }
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterHotKey(_hwnd, HOTKEY_ID);
        if (_oldWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
        }
    }

    private const int GWLP_WNDPROC = -4;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr SubclassWindow(IntPtr hwnd, WndProcDelegate newProc)
    {
        var ptr = Marshal.GetFunctionPointerForDelegate(newProc);
        return SetWindowLongPtr(hwnd, GWLP_WNDPROC, ptr);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
