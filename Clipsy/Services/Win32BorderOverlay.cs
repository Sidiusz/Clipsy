using System;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>
/// Pure Win32 overlay for drawing recording region border without WinUI artifacts
/// </summary>
public class Win32BorderOverlay
{
    private IntPtr _hwnd;
    private bool _created;
    private int _x, _y, _w, _h;
    private static readonly WndProcDelegate _staticWndProc = StaticWndProc;
    private static readonly System.Collections.Generic.Dictionary<IntPtr, Win32BorderOverlay> _byHwnd = new();

    // Magenta color key: any pixel painted with this exact RGB becomes transparent.
    private const uint COLORKEY_BGR = 0x00FF00FF;

    public IntPtr Hwnd => _hwnd;

    public bool Create(int x, int y, int w, int h)
    {
        if (_created) return false;

        _x = x; _y = y; _w = w; _h = h;

        // Register window class — magenta background brush is what color-key
        // strips out, leaving only the border lines visible on screen.
        if (_magentaBrush == IntPtr.Zero) _magentaBrush = CreateSolidBrush(COLORKEY_BGR);
        var wc = new WNDCLASS
        {
            lpfnWndProc = _staticWndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "ClipsyBorderOverlay",
            hbrBackground = _magentaBrush,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW)
        };

        // RegisterClass returns 0 if class already registered (atom collision). Ignore that.
        RegisterClass(ref wc);

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            "ClipsyBorderOverlay",
            "",
            WS_POPUP,
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return false;
        _byHwnd[_hwnd] = this;

        // Color-key mode: magenta pixels become fully transparent, everything
        // else renders normally. Works reliably for GDI-painted layered windows.
        SetLayeredWindowAttributes(_hwnd, COLORKEY_BGR, 0, LWA_COLORKEY);

        _created = true;
        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);

        return true;
    }

    private static IntPtr _magentaBrush;

    public void MoveTo(int x, int y, int w, int h)
    {
        if (!_created) return;
        _x = x; _y = y; _w = w; _h = h;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        // bErase=false: WM_PAINT clears the bg itself, so skipping the erase
        // pass kills the blue→magenta→blue flash during interactive resize.
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    public void Destroy()
    {
        if (!_created) return;
        _byHwnd.Remove(_hwnd);
        DestroyWindow(_hwnd);
        _created = false;
        _hwnd = IntPtr.Zero;
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
            case WM_PAINT:
            {
                var ps = new PAINTSTRUCT();
                var hdc = BeginPaint(hwnd, ref ps);

                // Fill bg with the color key magenta atomically inside WM_PAINT
                // (no separate WM_ERASEBKGND pass) — that's what kills the
                // flicker during interactive resize.
                var bgRc = new RECT { Left = 0, Top = 0, Right = _w, Bottom = _h };
                FillRect(hdc, ref bgRc, _magentaBrush);

                var pen = CreatePen(PS_SOLID, 2, RGB(31, 111, 235)); // #1F6FEB
                var oldPen = SelectObject(hdc, pen);
                MoveToEx(hdc, 0, 0, IntPtr.Zero);
                LineTo(hdc, _w - 1, 0);
                LineTo(hdc, _w - 1, _h - 1);
                LineTo(hdc, 0, _h - 1);
                LineTo(hdc, 0, 0);
                SelectObject(hdc, oldPen);
                DeleteObject(pen);
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;
            }
            case WM_ERASEBKGND:
                // Suppress default erase — WM_PAINT handles the fill, no flicker.
                return new IntPtr(1);

            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // Win32 API declarations
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
        public string lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public int rcPaint_left;
        public int rcPaint_top;
        public int rcPaint_right;
        public int rcPaint_bottom;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_PAINT = 0x000F;
    private const uint WM_DESTROY = 0x0002;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint LWA_COLORKEY = 0x00000001;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint crColor);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    private const int SW_SHOW = 5;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint IDC_ARROW = 32512;
    private const int PS_SOLID = 0;

    private static uint RGB(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, uint lpCursorName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lpPoint);

    [DllImport("gdi32.dll")]
    private static extern bool LineTo(IntPtr hdc, int nXEnd, int nYEnd);
}