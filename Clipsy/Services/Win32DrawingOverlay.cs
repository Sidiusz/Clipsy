using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>
/// Per-pixel-alpha Win32 layered overlay for free-hand annotation during
/// recording. Uses UpdateLayeredWindow + a GDI+ paint into an ARGB DIB so:
///   • strokes render with antialiasing and clean RGBA pixels in the MP4;
///   • the background tints the region by 1/255 (visually invisible) so the
///     layered window blocks clicks across the whole region — the user can't
///     accidentally activate the app being recorded while drawing;
///   • toggling click-through (active vs inactive) is a single style flip.
/// </summary>
public sealed class Win32DrawingOverlay
{
    private const int EraserRadius = 14;

    private IntPtr _hwnd;
    private bool _created;
    private int _x, _y, _w, _h;
    private bool _active;
    private bool _drawing;
    private bool _erasing;
    private bool _eraseWhole;
    private System.Drawing.Color _penColor = System.Drawing.Color.Red;
    private float _penThickness = 3f;

    private readonly List<Stroke> _strokes = new();
    private Stroke? _current;

    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dibBitmap;
    private IntPtr _oldBitmap;
    private Bitmap? _bitmap;
    private System.Drawing.Graphics? _g;

    private static readonly WndProcDelegate _staticWndProc = StaticWndProc;
    private static readonly Dictionary<IntPtr, Win32DrawingOverlay> _byHwnd = new();

    public IntPtr Hwnd => _hwnd;

    private sealed class Stroke
    {
        public System.Drawing.Color Color;
        public float Thickness;
        public List<PointF> Points = new();
    }

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

        AllocDib();
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
        // While active we own clicks (no WS_EX_TRANSPARENT). While inactive we
        // let everything fall through to the app being recorded.
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = active ? (ex & ~WS_EX_TRANSPARENT) : (ex | (int)WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        Render();
    }

    public void SetColor(byte r, byte g, byte b)
    {
        _penColor = System.Drawing.Color.FromArgb(255, r, g, b);
    }

    public void SetThickness(int t) => _penThickness = Math.Max(1, t);

    public void ClearAll()
    {
        _strokes.Clear();
        _current = null;
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
        _strokes.Clear();
    }

    private void AllocDib()
    {
        FreeDib();
        if (_w <= 0 || _h <= 0) return;
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);

        var bi = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = _w,
            biHeight = -_h,            // top-down DIB
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,         // BI_RGB
        };
        IntPtr ppvBits;
        _dibBitmap = CreateDIBSection(_memDc, ref bi, 0, out ppvBits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(_memDc, _dibBitmap);
        // Wrap the DIB pixel buffer with a managed Bitmap so we can use GDI+
        // for antialiased stroke rendering without copying.
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

        // Bg: alpha = 1 black so the layered window absorbs clicks across the
        // whole region (per-pixel alpha hit-testing) while staying visually
        // invisible. Strokes render on top with full opacity.
        if (_active)
            _g.Clear(System.Drawing.Color.FromArgb(1, 0, 0, 0));
        else
            _g.Clear(System.Drawing.Color.Transparent);

        DrawAll(_g);

        UpdateLayered();
    }

    private void DrawAll(System.Drawing.Graphics g)
    {
        foreach (var s in _strokes) DrawStroke(g, s);
        if (_current != null) DrawStroke(g, _current);
    }

    private static void DrawStroke(System.Drawing.Graphics g, Stroke s)
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
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */,
        };
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
            {
                if (!_active) break;
                SetCapture(hwnd);
                _drawing = true;
                _current = new Stroke { Color = _penColor, Thickness = _penThickness };
                _current.Points.Add(new PointF(LoWord(lParam), HiWord(lParam)));
                Render();
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                float x = LoWord(lParam), y = HiWord(lParam);
                if (_erasing)
                {
                    if (EraseDispatch(x, y)) Render();
                    return IntPtr.Zero;
                }
                if (!_drawing || _current == null) break;
                var last = _current.Points[_current.Points.Count - 1];
                if (last.X == x && last.Y == y) break;
                _current.Points.Add(new PointF(x, y));
                Render();
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                if (!_drawing) break;
                _drawing = false;
                ReleaseCapture();
                if (_current != null && _current.Points.Count > 0) _strokes.Add(_current);
                _current = null;
                Render();
                return IntPtr.Zero;
            }
            case WM_RBUTTONDOWN:
            {
                if (!_active) break;
                SetCapture(hwnd);
                _erasing = true;
                // Shift held at press latches whole-stroke erase for the duration
                // of this RMB drag, matching capture overlay behavior.
                _eraseWhole = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                if (EraseDispatch(LoWord(lParam), HiWord(lParam))) Render();
                return IntPtr.Zero;
            }
            case WM_RBUTTONUP:
            {
                if (!_erasing) break;
                _erasing = false;
                _eraseWhole = false;
                ReleaseCapture();
                return IntPtr.Zero;
            }
            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool EraseDispatch(float x, float y) =>
        _eraseWhole ? EraseWholeAt(x, y) : EraseSplitAt(x, y);

    private bool EraseWholeAt(float x, float y)
    {
        bool removed = false;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            float hit = EraserRadius + s.Thickness / 2f;
            float hit2 = hit * hit;
            foreach (var p in s.Points)
            {
                float dx = p.X - x, dy = p.Y - y;
                if (dx * dx + dy * dy <= hit2)
                {
                    _strokes.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }
        return removed;
    }

    // Partial erase: drop points inside the eraser disc, surviving runs become
    // independent strokes. Mirrors DrawingController.PartialErase in the capture
    // overlay so pencil behavior is identical across both surfaces.
    private bool EraseSplitAt(float x, float y)
    {
        bool changed = false;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            float hit = EraserRadius + s.Thickness / 2f;
            float hit2 = hit * hit;
            var runs = new List<List<PointF>>();
            List<PointF>? run = null;
            bool anyHit = false;
            foreach (var p in s.Points)
            {
                float dx = p.X - x, dy = p.Y - y;
                bool inside = dx * dx + dy * dy <= hit2;
                if (inside)
                {
                    anyHit = true;
                    if (run != null) { runs.Add(run); run = null; }
                }
                else
                {
                    run ??= new List<PointF>();
                    run.Add(p);
                }
            }
            if (run != null) runs.Add(run);
            if (!anyHit) continue;
            changed = true;
            _strokes.RemoveAt(i);
            foreach (var r in runs)
            {
                if (r.Count < 2) continue;
                _strokes.Insert(i, new Stroke { Color = s.Color, Thickness = s.Thickness, Points = r });
            }
        }
        return changed;
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
    private struct BITMAPINFOHEADER
    {
        public int biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize; public int biWidth; public int biHeight;
        public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
        public uint biPalette;
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
    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint IDC_CROSS = 32515;
    private const int GWL_EXSTYLE = -20;
    private const uint ULW_ALPHA = 0x00000002;

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
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, uint lpCursorName);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    private const int VK_SHIFT = 0x10;
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
