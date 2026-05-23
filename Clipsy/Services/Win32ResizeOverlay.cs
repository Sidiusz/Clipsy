using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>
/// Win32 layered overlay (per-pixel alpha) shown while the recording region
/// is unlocked. Draws the same blue rectangle + 8 white-with-blue-stroke
/// handles as the initial capture-overlay selection so the two flows look
/// identical. Hit testing is done via the layered surface's alpha channel:
/// a 1/255-alpha bg absorbs clicks across the interior (so the user can drag
/// the region without clicking through to the underlying app), the handles
/// are fully opaque, and edges outside the window are not affected.
/// </summary>
public sealed class Win32ResizeOverlay
{
    private const int HandleSize = 10;
    private const int MinSize = 64;
    private const float BorderThickness = 1.5f;
    // Window is enlarged by HandleMargin on each side so that handles centered
    // on the region's corners are not clipped by the window edge.
    private const int HandleMargin = 8;
    private static readonly System.Drawing.Color AccentBlue =
        System.Drawing.Color.FromArgb(0xFF, 0x1F, 0x6F, 0xEB);

    private IntPtr _hwnd;
    private bool _created;
    private int _x, _y, _w, _h;
    private DragMode _drag = DragMode.None;
    private int _dragStartScreenX, _dragStartScreenY;
    private int _startX, _startY, _startW, _startH;

    private IntPtr _screenDc, _memDc, _dibBitmap, _oldBitmap;
    private Bitmap? _bitmap;
    private System.Drawing.Graphics? _g;

    private static readonly WndProcDelegate _staticWndProc = StaticWndProc;
    private static readonly Dictionary<IntPtr, Win32ResizeOverlay> _byHwnd = new();

    public event Action<int, int, int, int>? RegionChanged;
    public IntPtr Hwnd => _hwnd;

    private enum DragMode { None, Move, ResizeTL, ResizeT, ResizeTR, ResizeR, ResizeBR, ResizeB, ResizeBL, ResizeL }

    private int WinX => _x - HandleMargin;
    private int WinY => _y - HandleMargin;
    private int WinW => _w + HandleMargin * 2;
    private int WinH => _h + HandleMargin * 2;

    public bool Create(int x, int y, int w, int h)
    {
        if (_created) return false;
        _x = x; _y = y; _w = w; _h = h;
        var wc = new WNDCLASS
        {
            lpfnWndProc = _staticWndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "ClipsyResizeOverlay",
            hbrBackground = IntPtr.Zero,
            hCursor = LoadCursor(IntPtr.Zero, IDC_SIZEALL),
        };
        RegisterClass(ref wc);

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "ClipsyResizeOverlay",
            "",
            WS_POPUP,
            WinX, WinY, WinW, WinH,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return false;
        _byHwnd[_hwnd] = this;

        AllocDib();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        Render();
        return _created = true;
    }

    public void MoveTo(int x, int y, int w, int h)
    {
        if (!_created) return;
        _x = x; _y = y; _w = w; _h = h;
        SetWindowPos(_hwnd, HWND_TOPMOST, WinX, WinY, WinW, WinH, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        AllocDib();
        Render();
    }

    public void Destroy()
    {
        if (!_created) return;
        _byHwnd.Remove(_hwnd);
        FreeDib();
        DestroyWindow(_hwnd);
        _created = false;
        _hwnd = IntPtr.Zero;
    }

    private void AllocDib()
    {
        FreeDib();
        if (WinW <= 0 || WinH <= 0) return;
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var bi = new BITMAPINFO
        {
            biSize = 40, biWidth = WinW, biHeight = -WinH, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        _dibBitmap = CreateDIBSection(_memDc, ref bi, 0, out IntPtr ppvBits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(_memDc, _dibBitmap);
        _bitmap = new Bitmap(WinW, WinH, WinW * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, ppvBits);
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
        if (_g == null) return;

        // DIB is fully transparent outside the click-active interior.
        _g.Clear(System.Drawing.Color.Transparent);

        // Interior: alpha=1 black tint absorbs clicks across the region without
        // visibly tinting the screen. Drawn at margin offset because the window
        // is larger than the region by HandleMargin on each side.
        using (var interior = new SolidBrush(System.Drawing.Color.FromArgb(1, 0, 0, 0)))
            _g.FillRectangle(interior, HandleMargin, HandleMargin, _w, _h);

        // Selection rectangle border.
        using (var pen = new Pen(AccentBlue, BorderThickness))
        {
            float half = BorderThickness / 2f;
            _g.DrawRectangle(pen,
                HandleMargin + half, HandleMargin + half,
                _w - BorderThickness, _h - BorderThickness);
        }

        // 8 handles matching the initial selection design: white fill, blue stroke.
        foreach (var (hx, hy) in HandleCenters(_w, _h))
            DrawHandle(_g, HandleMargin + hx, HandleMargin + hy);

        UpdateLayered();
    }

    private static IEnumerable<(int x, int y)> HandleCenters(int w, int h)
    {
        yield return (0, 0);
        yield return (w / 2, 0);
        yield return (w, 0);
        yield return (w, h / 2);
        yield return (w, h);
        yield return (w / 2, h);
        yield return (0, h);
        yield return (0, h / 2);
    }

    private static void DrawHandle(System.Drawing.Graphics g, int cx, int cy)
    {
        var rect = new RectangleF(cx - HandleSize / 2f, cy - HandleSize / 2f, HandleSize, HandleSize);
        using var fill = new SolidBrush(System.Drawing.Color.White);
        using var stroke = new Pen(AccentBlue, 1f);
        g.FillRectangle(fill, rect);
        g.DrawRectangle(stroke, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private void UpdateLayered()
    {
        if (_memDc == IntPtr.Zero) return;
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        var size = new SIZE { cx = WinW, cy = WinH };
        var src = new POINT { X = 0, Y = 0 };
        var dst = new POINT { X = WinX, Y = WinY };
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
            case WM_SETCURSOR:
            {
                GetCursorPos(out POINT p);
                ScreenToClient(hwnd, ref p);
                var mode = HitTest(p.X, p.Y);
                SetCursor(LoadCursor(IntPtr.Zero, CursorForMode(mode)));
                return new IntPtr(1);
            }
            case WM_LBUTTONDOWN:
            {
                int x = LoWord(lParam), y = HiWord(lParam);
                _drag = HitTest(x, y);
                if (_drag == DragMode.None) break;
                SetCapture(hwnd);
                GetCursorPos(out POINT p);
                _dragStartScreenX = p.X; _dragStartScreenY = p.Y;
                _startX = _x; _startY = _y; _startW = _w; _startH = _h;
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                if (_drag == DragMode.None) break;
                GetCursorPos(out POINT p);
                ApplyDrag(p.X - _dragStartScreenX, p.Y - _dragStartScreenY);
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                if (_drag == DragMode.None) break;
                _drag = DragMode.None;
                ReleaseCapture();
                return IntPtr.Zero;
            }
            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private DragMode HitTest(int wx, int wy)
    {
        // wx/wy are in window-local coords. The region's (0,0) sits at the
        // window's (HandleMargin, HandleMargin) offset.
        int x = wx - HandleMargin, y = wy - HandleMargin;
        int hs = HandleSize + 2; // generous hit radius
        bool inX(int cx) => x >= cx - hs / 2 && x <= cx + hs / 2;
        bool inY(int cy) => y >= cy - hs / 2 && y <= cy + hs / 2;
        if (inX(0) && inY(0)) return DragMode.ResizeTL;
        if (inX(_w / 2) && inY(0)) return DragMode.ResizeT;
        if (inX(_w) && inY(0)) return DragMode.ResizeTR;
        if (inX(_w) && inY(_h / 2)) return DragMode.ResizeR;
        if (inX(_w) && inY(_h)) return DragMode.ResizeBR;
        if (inX(_w / 2) && inY(_h)) return DragMode.ResizeB;
        if (inX(0) && inY(_h)) return DragMode.ResizeBL;
        if (inX(0) && inY(_h / 2)) return DragMode.ResizeL;
        if (x >= 0 && x < _w && y >= 0 && y < _h) return DragMode.Move;
        return DragMode.None;
    }

    private static uint CursorForMode(DragMode m) => m switch
    {
        DragMode.ResizeTL or DragMode.ResizeBR => IDC_SIZENWSE,
        DragMode.ResizeTR or DragMode.ResizeBL => IDC_SIZENESW,
        DragMode.ResizeT or DragMode.ResizeB => IDC_SIZENS,
        DragMode.ResizeL or DragMode.ResizeR => IDC_SIZEWE,
        DragMode.Move => IDC_SIZEALL,
        _ => IDC_ARROW,
    };

    private void ApplyDrag(int dx, int dy)
    {
        int nx = _startX, ny = _startY, nw = _startW, nh = _startH;
        switch (_drag)
        {
            case DragMode.Move:     nx += dx; ny += dy; break;
            case DragMode.ResizeTL: nx += dx; ny += dy; nw -= dx; nh -= dy; break;
            case DragMode.ResizeT:           ny += dy;          nh -= dy; break;
            case DragMode.ResizeTR:          ny += dy; nw += dx; nh -= dy; break;
            case DragMode.ResizeR:                     nw += dx;          break;
            case DragMode.ResizeBR:                    nw += dx; nh += dy; break;
            case DragMode.ResizeB:                                nh += dy; break;
            case DragMode.ResizeBL: nx += dx;          nw -= dx; nh += dy; break;
            case DragMode.ResizeL:  nx += dx;          nw -= dx;          break;
        }
        if (nw < MinSize)
        {
            if (_drag is DragMode.ResizeTL or DragMode.ResizeBL or DragMode.ResizeL) nx -= MinSize - nw;
            nw = MinSize;
        }
        if (nh < MinSize)
        {
            if (_drag is DragMode.ResizeTL or DragMode.ResizeTR or DragMode.ResizeT) ny -= MinSize - nh;
            nh = MinSize;
        }
        RegionChanged?.Invoke(nx, ny, nw, nh);
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
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint IDC_ARROW = 32512;
    private const uint IDC_SIZEALL = 32646;
    private const uint IDC_SIZENWSE = 32642;
    private const uint IDC_SIZENESW = 32643;
    private const uint IDC_SIZENS = 32645;
    private const uint IDC_SIZEWE = 32644;
    private const uint ULW_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, uint lpCursorName);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
