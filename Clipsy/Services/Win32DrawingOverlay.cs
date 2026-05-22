using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>
/// Pure Win32 GDI overlay for free-hand annotation strokes during recording.
/// Replaces RecordingDrawingWindow (WinUI 3) because LWA_COLORKEY does not
/// work against a DirectComposition swap-chain — the magenta background
/// painted as the color key would never be stripped and the user would see
/// a solid purple rectangle. Pure GDI surface lets the color key do its job.
/// </summary>
public sealed class Win32DrawingOverlay
{
    private const uint COLORKEY_BGR = 0x00FF00FF; // magenta = transparent

    private IntPtr _hwnd;
    private bool _created;
    private int _x, _y, _w, _h;
    private bool _active;
    private bool _drawing;
    private uint _penColor = 0x000000FF; // BGR red
    private int _penThickness = 3;

    private readonly List<Stroke> _strokes = new();
    private Stroke? _current;
    private static IntPtr _bgBrush;
    // Static dispatch keeps the delegate alive for the process lifetime, so
    // Windows never calls back into a GC'd thunk. Per-hwnd instance lookup
    // routes messages to the right Win32DrawingOverlay.
    private static readonly WndProcDelegate _staticWndProc = StaticWndProc;
    private static readonly Dictionary<IntPtr, Win32DrawingOverlay> _byHwnd = new();

    public IntPtr Hwnd => _hwnd;

    private sealed class Stroke
    {
        public uint ColorBgr;
        public int Thickness;
        public List<int> X = new();
        public List<int> Y = new();
    }

    public bool Create(int x, int y, int w, int h)
    {
        if (_created) return false;
        _x = x; _y = y; _w = w; _h = h;
        if (_bgBrush == IntPtr.Zero) _bgBrush = CreateSolidBrush(COLORKEY_BGR);

        var wc = new WNDCLASS
        {
            lpfnWndProc = _staticWndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "ClipsyDrawingOverlay",
            hbrBackground = _bgBrush,
            hCursor = LoadCursor(IntPtr.Zero, IDC_CROSS),
        };
        RegisterClass(ref wc);

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "ClipsyDrawingOverlay",
            "",
            WS_POPUP,
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return false;
        _byHwnd[_hwnd] = this;
        SetLayeredWindowAttributes(_hwnd, COLORKEY_BGR, 0, LWA_COLORKEY);
        _created = true;
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        UpdateWindow(_hwnd);
        return true;
    }

    public void MoveTo(int x, int y, int w, int h)
    {
        if (!_created) return;
        _x = x; _y = y; _w = w; _h = h;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    public void SetActive(bool active)
    {
        if (!_created) return;
        _active = active;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = active ? (ex & ~WS_EX_TRANSPARENT) : (ex | (int)WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        // Re-apply colorkey after style change (defensive — some drivers reset it).
        SetLayeredWindowAttributes(_hwnd, COLORKEY_BGR, 0, LWA_COLORKEY);
    }

    public void SetColor(byte r, byte g, byte b)
    {
        _penColor = (uint)(r | (g << 8) | (b << 16));
    }

    public void SetThickness(int t) => _penThickness = Math.Max(1, t);

    public void ClearAll()
    {
        _strokes.Clear();
        if (_created) InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    public void Destroy()
    {
        if (!_created) return;
        _byHwnd.Remove(_hwnd);
        DestroyWindow(_hwnd);
        _created = false;
        _hwnd = IntPtr.Zero;
        _strokes.Clear();
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_byHwnd.TryGetValue(hwnd, out var self))
            return self.WndProc(hwnd, msg, wParam, lParam);
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // LWA_COLORKEY pixels are HTTRANSPARENT by default — clicks fall
                // through. Force HTCLIENT while active so the overlay receives
                // mouse input over the (transparent) magenta canvas.
                if (_active) return new IntPtr(HTCLIENT);
                return DefWindowProc(hwnd, msg, wParam, lParam);
            }
            case WM_ERASEBKGND:
            {
                var rc = new RECT { Left = 0, Top = 0, Right = _w, Bottom = _h };
                FillRect(wParam, ref rc, _bgBrush);
                return new IntPtr(1);
            }
            case WM_PAINT:
            {
                var ps = new PAINTSTRUCT();
                var hdc = BeginPaint(hwnd, ref ps);
                var bgRc = new RECT { Left = 0, Top = 0, Right = _w, Bottom = _h };
                FillRect(hdc, ref bgRc, _bgBrush);
                foreach (var s in _strokes) DrawStroke(hdc, s);
                if (_current != null) DrawStroke(hdc, _current);
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;
            }
            case WM_LBUTTONDOWN:
            {
                if (!_active) break;
                SetCapture(hwnd);
                _drawing = true;
                _current = new Stroke { ColorBgr = _penColor, Thickness = _penThickness };
                int x = LoWord(lParam), y = HiWord(lParam);
                _current.X.Add(x); _current.Y.Add(y);
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                if (!_drawing || _current == null) break;
                int x = LoWord(lParam), y = HiWord(lParam);
                int lastX = _current.X[_current.X.Count - 1];
                int lastY = _current.Y[_current.Y.Count - 1];
                if (x == lastX && y == lastY) break;
                _current.X.Add(x); _current.Y.Add(y);
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                if (!_drawing) break;
                _drawing = false;
                ReleaseCapture();
                if (_current != null && _current.X.Count > 1)
                {
                    _strokes.Add(_current);
                }
                _current = null;
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }
            case WM_RBUTTONUP:
            {
                if (!_active) break;
                ClearAll();
                return IntPtr.Zero;
            }
            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void DrawStroke(IntPtr hdc, Stroke s)
    {
        if (s.X.Count < 2) return;
        var pen = CreatePen(PS_SOLID, s.Thickness, s.ColorBgr);
        var oldPen = SelectObject(hdc, pen);
        SetBkMode(hdc, TRANSPARENT_BKMODE);
        MoveToEx(hdc, s.X[0], s.Y[0], IntPtr.Zero);
        for (int i = 1; i < s.X.Count; i++) LineTo(hdc, s.X[i], s.Y[i]);
        SelectObject(hdc, oldPen);
        DeleteObject(pen);
    }

    private static int LoWord(IntPtr p) => unchecked((short)(p.ToInt64() & 0xFFFF));
    private static int HiWord(IntPtr p) => unchecked((short)((p.ToInt64() >> 16) & 0xFFFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public int rcPaint_left, rcPaint_top, rcPaint_right, rcPaint_bottom;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_PAINT = 0x000F;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_COLORKEY = 0x00000001;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint IDC_CROSS = 32515;
    private const int PS_SOLID = 0;
    private const int TRANSPARENT_BKMODE = 1;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, uint lpCursorName);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint crColor);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lpPoint);
    [DllImport("gdi32.dll")] private static extern bool LineTo(IntPtr hdc, int nXEnd, int nYEnd);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
}
