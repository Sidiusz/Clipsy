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
    // Shared, surface-agnostic pencil/eraser logic. Identical engine used by
    // the capture overlay canvas, so recording annotation behaves exactly the
    // same (thickness clamp, wheel step, partial/whole erase, single-click dot,
    // round caps/joins). This overlay is only the GDI render surface + Win32
    // input source; all state lives in the engine.
    private readonly Clipsy.Drawing.PencilEngine _engine = new();

    private IntPtr _hwnd;
    private bool _created;
    private int _x, _y, _w, _h;
    private bool _active;
    private bool _trackingLeave;
    private IntPtr _kbHook;
    private LowLevelKeyboardProcDelegate? _kbProcDelegate;

    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _dibBitmap;
    private IntPtr _oldBitmap;
    private Bitmap? _bitmap;
    private System.Drawing.Graphics? _g;

    private static readonly WndProcDelegate _staticWndProc = StaticWndProc;
    private static readonly Dictionary<IntPtr, Win32DrawingOverlay> _byHwnd = new();

    public IntPtr Hwnd => _hwnd;

    public Win32DrawingOverlay()
    {
        _engine.SetThickness(3f);
        // Engine raises Changed on every stroke/erase/thickness/cursor mutation;
        // repaint the layered window in response so canvas and recording stay
        // pixel-identical.
        _engine.Changed += OnEngineChanged;
    }

    private void OnEngineChanged()
    {
        if (_created) Render();
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
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW), // arrow cursor, matching canvas behaviour
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
        if (active) InstallKbHook();
        else { _engine.HideCursor(); UninstallKbHook(); }
        Render();
    }

    public void SetColor(byte r, byte g, byte b) => _engine.SetColor(r, g, b);

    public void SetThickness(int t) => _engine.SetThickness(t);

    public void ClearAll() => _engine.ClearAll();

    public void Destroy()
    {
        if (!_created) return;
        UninstallKbHook();
        _byHwnd.Remove(_hwnd);
        FreeDib();
        DestroyWindow(_hwnd);
        _created = false;
        _hwnd = IntPtr.Zero;
        _engine.ClearAll();
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
        foreach (var s in _engine.Strokes) DrawStroke(g, s);
        if (_engine.Current != null) DrawStroke(g, _engine.Current);
        DrawPreviewRing(g);
    }

    private static void DrawStroke(System.Drawing.Graphics g, Clipsy.Drawing.PencilEngine.Stroke s)
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

    // Preview ring: brush-size circle, visible whenever the overlay is active.
    // Matches canvas: shown during drawing, erasing, and idle — always brush size.
    private void DrawPreviewRing(System.Drawing.Graphics g)
    {
        if (!_active || !_engine.CursorVisible) return;
        float d = Math.Max(2f, _engine.Thickness);
        float cx = _engine.Cursor.X, cy = _engine.Cursor.Y;
        using var fill = new SolidBrush(System.Drawing.Color.FromArgb(80, 0, 0, 0));
        using var outline = new Pen(System.Drawing.Color.White, 1f);
        g.FillEllipse(fill, cx - d / 2f, cy - d / 2f, d, d);
        g.DrawEllipse(outline, cx - d / 2f, cy - d / 2f, d, d);
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
                _engine.BeginStroke(LoWord(lParam), HiWord(lParam));
                return IntPtr.Zero;
            }
            case WM_MOUSEMOVE:
            {
                float x = LoWord(lParam), y = HiWord(lParam);
                EnsureLeaveTracking(hwnd);
                // Always track cursor so the preview ring follows the pointer.
                _engine.SetCursor(x, y, _active);
                if (_engine.IsErasing) _engine.ExtendErase(x, y);
                else if (_engine.IsDrawing) _engine.ExtendStroke(x, y);
                return IntPtr.Zero;
            }
            case WM_LBUTTONUP:
            {
                if (!_engine.IsDrawing) break;
                ReleaseCapture();
                _engine.EndStroke();
                return IntPtr.Zero;
            }
            case WM_RBUTTONDOWN:
            {
                if (!_active) break;
                SetCapture(hwnd);
                // Shift held at press latches whole-stroke erase for the RMB drag,
                // matching capture overlay behavior.
                bool whole = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                _engine.BeginErase(LoWord(lParam), HiWord(lParam), whole);
                return IntPtr.Zero;
            }
            case WM_RBUTTONUP:
            {
                if (!_engine.IsErasing) break;
                ReleaseCapture();
                _engine.EndErase();
                return IntPtr.Zero;
            }
            case WM_MOUSEWHEEL:
            {
                if (!_active) break;
                int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                int steps = delta / 120;
                if (steps != 0) _engine.NudgeThickness(steps);
                return IntPtr.Zero;
            }
            case WM_MOUSELEAVE:
            {
                _trackingLeave = false;
                _engine.HideCursor();
                return IntPtr.Zero;
            }
            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void InstallKbHook()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProcDelegate = LowLevelKeyboardProc;
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProcDelegate, GetModuleHandle(null), 0);
    }

    private void UninstallKbHook()
    {
        if (_kbHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_kbHook);
        _kbHook = IntPtr.Zero;
        _kbProcDelegate = null;
    }

    private IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt64() == WM_KEYDOWN_MSG)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == VK_Z && (GetKeyState(VK_CONTROL) & 0x8000) != 0)
            {
                _engine.Undo();
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelKeyboardProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    private void EnsureLeaveTracking(IntPtr hwnd)
    {
        if (_trackingLeave) return;
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TME_LEAVE,
            hwndTrack = hwnd,
            dwHoverTime = 0,
        };
        if (TrackMouseEvent(ref tme)) _trackingLeave = true;
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

    private const uint IDC_ARROW   = 32512;
    private const uint WM_DESTROY  = 0x0002;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint TME_LEAVE = 0x00000002;
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
    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);
    private const int WH_KEYBOARD_LL = 13;
    private const long WM_KEYDOWN_MSG = 0x0100;
    private const int VK_Z = 0x5A;
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProcDelegate lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
