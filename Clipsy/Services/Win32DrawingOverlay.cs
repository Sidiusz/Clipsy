using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Clipsy.Drawing;

namespace Clipsy.Services;

/// <summary>
/// Per-pixel-alpha Win32 layered overlay that hosts <see cref="PencilEngine"/>
/// during a recording. The engine owns the stroke list, eraser logic, brush
/// state, and cursor position — this class only translates Win32 mouse / wheel
/// messages into engine calls and re-paints the engine's state into an ARGB
/// DIB via System.Drawing. The same engine is reused by the capture overlay.
/// </summary>
public sealed class Win32DrawingOverlay
{
    private IntPtr _hwnd;
    private bool _created;
    private int _x, _y, _w, _h;
    private bool _active;

    public PencilEngine Engine { get; } = new();

    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dibBitmap;
    private IntPtr _oldBitmap;
    private Bitmap? _bitmap;
    private System.Drawing.Graphics? _g;

    private static readonly WndProcDelegate _staticWndProc = StaticWndProc;
    private static readonly Dictionary<IntPtr, Win32DrawingOverlay> _byHwnd = new();

    public IntPtr Hwnd => _hwnd;

    public bool Create(int x, int y, int w, int h)
    {
        if (_created) return false;
        _x = x; _y = y; _w = w; _h = h;

        var wc = new WNDCLASS
        {
            lpfnWndProc = _staticWndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "ClipsyDrawingOverlay",
            hbrBackground = IntPtr.Zero,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
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

        AllocDib();
        Engine.Changed += Render;
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        Render();
        return _created = true;
    }

    public void MoveTo(int x, int y, int w, int h)
    {
        if (!_created) return;
        _x = x; _y = y; _w = w; _h = h;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        AllocDib();
        Render();
    }

    public void SetActive(bool active)
    {
        if (!_created) return;
        _active = active;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = active ? (ex & ~WS_EX_TRANSPARENT) : (ex | (int)WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        if (!active) Engine.HideCursor();
        Render();
    }

    public void SetZOrder(bool topmost)
    {
        if (!_created) return;
        var insertAfter = topmost ? HWND_TOPMOST : HWND_BOTTOM;
        SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Destroy()
    {
        if (!_created) return;
        Engine.Changed -= Render;
        _byHwnd.Remove(_hwnd);
        FreeDib();
        DestroyWindow(_hwnd);
        _created = false;
        _hwnd = IntPtr.Zero;
    }

    private void AllocDib()
    {
        FreeDib();
        if (_w <= 0 || _h <= 0) return;
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var bi = new BITMAPINFO
        {
            biSize = 40, biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        _dibBitmap = CreateDIBSection(_memDc, ref bi, 0, out IntPtr ppvBits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(_memDc, _dibBitmap);
        _bitmap = new Bitmap(_w, _h, _w * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, ppvBits);
        _g = System.Drawing.Graphics.FromImage(_bitmap);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
    }

    private void FreeDib()
    {
        _g?.Dispose(); _g = null;
        _bitmap?.Dispose(); _bitmap = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) SelectObject(_memDc, _oldBitmap);
            DeleteDC(_memDc); _memDc = IntPtr.Zero;
        }
        if (_dibBitmap != IntPtr.Zero) { DeleteObject(_dibBitmap); _dibBitmap = IntPtr.Zero; }
        if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        _oldBitmap = IntPtr.Zero;
    }

    private void Render()
    {
        if (_g == null || _bitmap == null) return;

        if (_active)
            _g.Clear(System.Drawing.Color.FromArgb(1, 0, 0, 0));
        else
            _g.Clear(System.Drawing.Color.Transparent);

        foreach (var s in Engine.Strokes) DrawStroke(_g, s);
        if (Engine.Current != null) DrawStroke(_g, Engine.Current);

        if (_active && Engine.CursorVisible)
        {
            // Cursor preview: a ring whose diameter matches the brush thickness,
            // identical to the XAML capture overlay so behavior reads the same
            // on both surfaces.
            float d = Math.Max(2f, Engine.Thickness);
            float r = d / 2f;
            var c = Engine.Cursor;
            using var fill = new SolidBrush(System.Drawing.Color.FromArgb(80, 0, 0, 0));
            using var outline = new Pen(System.Drawing.Color.White, 1f);
            _g.FillEllipse(fill, c.X - r, c.Y - r, d, d);
            _g.DrawEllipse(outline, c.X - r, c.Y - r, d, d);
        }

        UpdateLayered();
    }

    private static void DrawStroke(System.Drawing.Graphics g, PencilEngine.Stroke s)
    {
        if (s.Points.Count < 1) return;
        using var pen = new Pen(s.Color, s.Thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        if (s.Points.Count == 1)
        {
            float r = s.Thickness / 2f;
            using var b = new SolidBrush(s.Color);
            g.FillEllipse(b, s.Points[0].X - r, s.Points[0].Y - r, s.Thickness, s.Thickness);
        }
        else
        {
            g.DrawLines(pen, s.Points.ToArray());
        }
    }

    private void UpdateLayered()
    {
        if (_memDc == IntPtr.Zero) return;
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        var size = new SIZE { cx = _w, cy = _h };
        var src = new POINT { X = 0, Y = 0 };
        var dst = new POINT { X = _x, Y = _y };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, ULW_ALPHA);
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
            case WM_LBUTTONDOWN:
                if (!_active) break;
                SetCapture(hwnd);
                Engine.BeginStroke(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                if (!_active) break;
                {
                    float x = LoWord(lParam), y = HiWord(lParam);
                    Engine.SetCursor(x, y);
                    if (Engine.IsDrawing) Engine.ExtendStroke(x, y);
                    else if (Engine.IsErasing) Engine.ExtendErase(x, y);
                }
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (!Engine.IsDrawing) break;
                Engine.EndStroke();
                ReleaseCapture();
                return IntPtr.Zero;

            case WM_RBUTTONDOWN:
                if (!_active) break;
                SetCapture(hwnd);
                bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                Engine.BeginErase(LoWord(lParam), HiWord(lParam), shift);
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                if (!Engine.IsErasing) break;
                Engine.EndErase();
                ReleaseCapture();
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
                if (!_active) break;
                {
                    short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    int steps = delta / 120; // one notch
                    Engine.NudgeThickness(steps);
                }
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                Engine.HideCursor();
                return IntPtr.Zero;

            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
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

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant; public uint biPalette;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp; public byte BlendFlags;
        public byte SourceConstantAlpha; public byte AlphaFormat;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    private const uint IDC_ARROW = 32512;
    private const int GWL_EXSTYLE = -20;
    private const uint ULW_ALPHA = 0x00000002;
    private const int VK_SHIFT = 0x10;

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, uint lpCursorName);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
