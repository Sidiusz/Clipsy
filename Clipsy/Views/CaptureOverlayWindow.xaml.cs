using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using IOPath = System.IO.Path;
using Clipsy.Drawing;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

// Partial class split by concern: .Input/.Selection/.Drawing/.Text/.Flyouts/
// .ColorPicker/.Actions/.Ocr (+ this file: ctor, window setup, win32 interop).
public sealed partial class CaptureOverlayWindow : Window
{
    private enum InteractionMode { Idle, SelectingNew, MovingSelection, ResizingSelection, DrawingStroke, DrawingRect, Erasing, PlacingText, SelectingOcrText }
    private enum HandlePos { TL, T, TR, R, BR, B, BL, L }

    private const double MinSelectionSize = 4.0;
    private const double SingleClickFallbackSize = 100.0;
    private const double HandleSize = 10.0;
    private const double HandleHitInflate = 6.0;

    private readonly ScreenFreezeService.FrozenFrame _frame;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DrawingController _drawing;
    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _handleVisuals = new();
    private readonly Ellipse _pencilPreview;
    private readonly TextBlock _textPreview;

    private InteractionMode _mode = InteractionMode.Idle;
    private bool _hasSelection;
    private Rect _selectionRect;
    private Rect _selectionAtDragStart;
    private Point _dragStart;
    private HandlePos _activeHandle;

    private Polyline? _activeStrokeVisual;
    private StrokeElement? _activeStroke;
    private Shape? _activeRectVisual;
    private Line? _activeLineVisual;
    private Point _activeRectAnchor;
    private TextBox? _activeTextBox;

    // OCR state
    private bool _inOcrMode;
    private bool _ocrPanelDragging;
    private Point _ocrPanelDragOffset;

    // Current shape tool for shapes button
    private ToolKind _currentShapeTool = ToolKind.Rectangle;
    private readonly List<(Rect bounds, Microsoft.UI.Xaml.Shapes.Rectangle box, TextBlock glyph)> _ocrVisuals = new();
    private readonly List<OcrWord> _ocrWordsRaw = new();
    private readonly List<Rect> _ocrWordsDip = new();
    private readonly HashSet<int> _ocrSelected = new();
    private DispatcherTimer? _scanTimer;
    private DispatcherTimer? _hoverTimer;
    private double _scanProgress;
    private double _scanDir = 1.0;
    private Point _ocrDragStart;

    // Pre-allocated dim geometry — avoids GC pressure and XAML re-layout on every PointerMoved.
    private readonly RectangleGeometry _dimFull = new();
    private readonly RectangleGeometry _dimHole = new();

    public CaptureOverlayWindow(ScreenFreezeService.FrozenFrame frame)
    {
        _frame = frame;
        InitializeComponent();

        // Set picker colour in code: XAML markup assignment throws
        // XamlParseException (0x802B000A) on Windows App SDK 1.6.
        try { ColorPickerCtl.Color = Microsoft.UI.Colors.Red; } catch { }

        // Set window background to transparent to prevent white borders
        this.SystemBackdrop = null;

        ThemeService.Register(RootGrid);

        // Wire up pre-allocated dim geometries once so UpdateDimGeometry only
        // mutates Rect, never allocates or modifies the Children collection.
        DimGeometry.Children.Add(_dimFull);
        DimGeometry.Children.Add(_dimHole);

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = GetAppWindowForCurrentWindow();
        // Hide before the window shows: DWM cloak + WS_EX_LAYERED alpha 0
        // suppress the black first-frame erase before the first XAML paint.
        try
        {
            int cloak = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            SetWindowLong(_hwnd, GWL_EXSTYLE, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
            SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);
        }
        catch { }
        ConfigureAsOverlay();
        DisableDwmDecorations();
        // Load the frozen frame synchronously so the first composed frame
        // already shows the snapshot, not black-then-desktop.
        TryLoadFrozenImage();

        _drawing = new DrawingController(DrawingCanvas);
        ApplyLocalization();
        PositionHintOnPrimaryScreen();
        BuildHandles();
        _pencilPreview = new Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        CursorPreviewLayer.Children.Add(_pencilPreview);

        // Translucent "A" the size of the current text font so the user can
        // see what the text tool will look like before clicking to commit it.
        _textPreview = new TextBlock
        {
            Text = "A",
            Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 18,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        CursorPreviewLayer.Children.Add(_textPreview);

        BuildScreenMenu();
        Activated += OnActivated;
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        // Frame is decoded synchronously, so the 2nd composition tick is
        // guaranteed to contain it — uncloak then. No async-decode race.
        int composed = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFirstFrames;
        void OnFirstFrames(object? s, object e)
        {
            composed++;
            if (composed < 2) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFirstFrames;
            Uncloak();
        }

        // Start in region select mode (no drawing tool active)
        SetTool(ToolKind.None);
    }

    private void ApplyLocalization()
    {
        HintText.Text = Strings.Get("HintSelectArea");
        HintFullScreenLabel.Text = Strings.Get("HintFullScreen");
        HintCancelLabel.Text = Strings.Get("HintCancel");
        FontFilterBox.PlaceholderText = Strings.Get("FilterPlaceholder");

        ToolTipService.SetToolTip(RecordBtn,     Strings.Get("TipRecord"));
        ToolTipService.SetToolTip(ScreenshotBtn, Strings.Get("TipScreenshot"));
        ToolTipService.SetToolTip(CopyBtn,       Strings.Get("TipCopy"));
        ToolTipService.SetToolTip(CancelBtn,     Strings.Get("TipCancel"));

        ToolTipService.SetToolTip(ColorBtn, Strings.Get("TipColor"));
        // Flyout-hosted buttons may be null at ctor time; guard so a failed
        // SetToolTip doesn't kill the overlay ctor.
        if (EyedropperBtn   != null) ToolTipService.SetToolTip(EyedropperBtn,   Strings.Get("TipEyedropper"));
        if (ColorCancelBtn  != null) ToolTipService.SetToolTip(ColorCancelBtn,  Strings.Get("TipColorCancel"));
        if (ColorConfirmBtn != null) ToolTipService.SetToolTip(ColorConfirmBtn, Strings.Get("TipColorApply"));
        ToolTipService.SetToolTip(PencilBtn, Strings.Get("TipPencil"));
        ToolTipService.SetToolTip(EllipseBtn, Strings.Get("TipEllipse"));
        ToolTipService.SetToolTip(LineBtn,    Strings.Get("TipLine"));
        if (ArrowBtn != null) ToolTipService.SetToolTip(ArrowBtn, Strings.Get("TipArrow"));
        ToolTipService.SetToolTip(TextBtn,    Strings.Get("TipText"));
        ToolTipService.SetToolTip(ShapesBtn,  Strings.Get("TipShapes"));
        ToolTipService.SetToolTip(OcrBtn,     Strings.Get("TipOcr"));

        ToolTipService.SetToolTip(OcrSelectAllBtn, Strings.Get("TipOcrSelectAll"));
        ToolTipService.SetToolTip(OcrCopyBtn,      Strings.Get("TipOcrCopy"));
        ToolTipService.SetToolTip(OcrTranslateBtn, Strings.Get("TipOcrTranslate"));
        ToolTipService.SetToolTip(OcrExitBtn,      Strings.Get("TipOcrExit"));

        OcrPanelTitle.Text       = Strings.Get("OcrRecognized").ToUpperInvariant();
        TranslatePanelTitle.Text = Strings.Get("TrTranslation").ToUpperInvariant();

        SelectScreenMenu.Text = Strings.Get("MenuSelectScreen");
        MenuSelectAll.Text    = Strings.Get("MenuSelectAll");
        MenuCopy.Text         = Strings.Get("MenuCopy");
        MenuSave.Text         = Strings.Get("MenuSave");
        MenuSaveAs.Text       = Strings.Get("MenuSaveAs");
        MenuClear.Text        = Strings.Get("MenuClear");
        MenuCancel.Text       = Strings.Get("MenuCancel");
    }

    private void PositionHintOnPrimaryScreen()
    {
        try
        {
            // Get primary monitor bounds using Win32 API
            const int SM_XVIRTUALSCREEN = 76;
            const int SM_YVIRTUALSCREEN = 77;
            const int SM_CXSCREEN = 0;
            const int SM_CYSCREEN = 1;

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern int GetSystemMetrics(int nIndex);

            var primaryWidth = GetSystemMetrics(SM_CXSCREEN);
            var primaryHeight = GetSystemMetrics(SM_CYSCREEN);
            var virtualX = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var virtualY = GetSystemMetrics(SM_YVIRTUALSCREEN);

            if (primaryWidth > 0 && primaryHeight > 0)
            {
                // Primary monitor starts at (0,0) in screen coordinates
                // Convert to virtual coordinates by subtracting virtual origin
                var primaryCenterX = (primaryWidth / 2) - virtualX;
                var primaryTopY = 72 - virtualY;

                // Center hint on primary monitor - measure actual width
                Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var hintWidth = Hint.DesiredSize.Width;
                Hint.Margin = new Thickness(primaryCenterX - (hintWidth / 2), primaryTopY, 0, 0);
            }
            else
            {
                // Fallback to fixed positioning
                Hint.Margin = new Thickness(50, 72, 0, 0);
            }
        }
        catch
        {
            // Fallback to fixed positioning on any error
            Hint.Margin = new Thickness(50, 72, 0, 0);
        }
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    private void ConfigureAsOverlay()
    {
        if (_appWindow.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(false, false);
            op.IsResizable = false;
            op.IsMaximizable = false;
            op.IsMinimizable = false;
            op.IsAlwaysOnTop = true;
        }
        _appWindow.IsShownInSwitchers = false;

        var b = _frame.VirtualBounds;

        // First present is off-screen (-32000) so the black erase is unseen;
        // Uncloak() moves it into place. These APIs take physical pixels.
        _appWindow.MoveAndResize(new RectInt32(OffscreenX, OffscreenY, b.Width, b.Height));
        SetWindowPos(_hwnd, HWND_TOPMOST, OffscreenX, OffscreenY, b.Width, b.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // XAML element sizes are in DIPs, so divide by DPI scale.
        // Use GetDpiForWindow for accuracy before XamlRoot is ready.
        var rawDpi = GetDpiForWindow(_hwnd);
        var dpiScale = rawDpi > 0 ? rawDpi / 96.0 : (Content?.XamlRoot?.RasterizationScale ?? 1.0);
        RootGrid.Width = b.Width / dpiScale;
        RootGrid.Height = b.Height / dpiScale;

        UpdateDimGeometry(null);
    }

    private void DisableDwmDecorations()
    {
        try
        {
            // Cloak until the first XAML composition lands, else DWM shows the
            // default opaque surface (black flash) for one frame.
            int cloak = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

            // Disable window rounding
            int donotround = 1;
            DwmSetWindowAttribute(_hwnd, 33, ref donotround, sizeof(int));

            // Disable non-client area rendering
            int ncDisabled = 1;
            DwmSetWindowAttribute(_hwnd, 2, ref ncDisabled, sizeof(int));

            // Remove all window borders completely
            int borderless = 1;
            DwmSetWindowAttribute(_hwnd, 20, ref borderless, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE

            // Set window style to remove all borders. Keep WS_EX_LAYERED —
            // it is what suppresses the black first-frame erase.
            SetWindowLong(_hwnd, GWL_STYLE, WS_POPUP);
            SetWindowLong(_hwnd, GWL_EXSTYLE, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED);

            // Force Windows to recalculate the non-client area after style changes.
            // Without SWP_FRAMECHANGED the old border geometry stays active and blocks hits.
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Overlay DWM disable failed: {ex.Message}");
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (FrozenImage.Source == null) TryLoadFrozenImage();
        RootGrid.Focus(FocusState.Programmatic);

        // Update dimming geometry after window is fully loaded
        UpdateDimGeometry(null);

    }

    private bool _cloaked = true;
    private const int DWMWA_CLOAK = 13;

    private void Uncloak()
    {
        if (!_cloaked) return;
        _cloaked = false;
        // Move into place but stay cloaked: the move makes DWM repaint the
        // redirection surface black until the next present. Reveal a few ticks later.
        var b = _frame.VirtualBounds;
        SetWindowPos(_hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
        int ticksAfterMove = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnPostMoveTick;
        void OnPostMoveTick(object? s, object e)
        {
            ticksAfterMove++;
            if (ticksAfterMove == 2)
            {
                // Strip WS_EX_LAYERED while cloaked: a full-screen layered window
                // takes DWM's slow path and caps FPS on high-refresh monitors.
                SetWindowLong(_hwnd, GWL_EXSTYLE, WS_EX_TOPMOST | WS_EX_TOOLWINDOW);
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                return;
            }
            if (ticksAfterMove < 3) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPostMoveTick;
            int cloak = 0;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            // Shown with NOACTIVATE, so grab foreground/focus explicitly once visible.
            ForceForeground(_hwnd);
            RootGrid.Focus(FocusState.Programmatic);
            PlayIntroAnimations();
        }
    }

    // Cloak instantly before Close(): tearing down while visible paints the
    // black redirection surface for a frame (the flash on cancel/save).
    internal void HideForClose()
    {
        try
        {
            int cloak = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        }
        catch { }
    }

    // Soft entrance: dim fades up ~120 ms, hint pill slides in. Masks the
    // single-frame seam between desktop and frozen snapshot.
    private void PlayIntroAnimations()
    {
        try
        {
            var sb = new Storyboard();

            var dimFade = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(120)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(dimFade, DimPath);
            Storyboard.SetTargetProperty(dimFade, "Opacity");
            sb.Children.Add(dimFade);

            if (Hint.Visibility == Visibility.Visible)
            {
                var hintSlide = new DoubleAnimation
                {
                    From = -56.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(hintSlide, HintTranslate);
                Storyboard.SetTargetProperty(hintSlide, "Y");
                sb.Children.Add(hintSlide);

                var hintFade = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(hintFade, Hint);
                Storyboard.SetTargetProperty(hintFade, "Opacity");
                sb.Children.Add(hintFade);
            }

            sb.Begin();
        }
        catch
        {
            // Cosmetic only — but the elements start hidden (Opacity 0 in
            // XAML), so on any failure snap them to their final state.
            DimPath.Opacity = 1;
            Hint.Opacity = 1;
            HintTranslate.Y = 0;
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Update dimming geometry when window size changes
        UpdateDimGeometry(_hasSelection ? _selectionRect : null);
    }

    private void TryLoadFrozenImage()
    {
        try
        {
            // Decode synchronously into a WriteableBitmap; BitmapImage decodes
            // async even via SetSource, forcing a black flash or an open delay.
            using var ms = new System.IO.MemoryStream(_frame.ImageBytes);
            using var src = new System.Drawing.Bitmap(ms);
            var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(src.Width, src.Height);
            var data = src.LockBits(
                new System.Drawing.Rectangle(0, 0, src.Width, src.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using var dst = wb.PixelBuffer.AsStream();
                int rowBytes = src.Width * 4;
                var row = new byte[rowBytes];
                for (int y = 0; y < src.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                    dst.Write(row, 0, rowBytes);
                }
            }
            finally
            {
                src.UnlockBits(data);
            }
            wb.Invalidate();
            FrozenImage.Source = wb;

            // Set exact image dimensions in DIPs
            var b = _frame.VirtualBounds;
            var rawDpi = GetDpiForWindow(_hwnd);
            var dpiScale = rawDpi > 0 ? rawDpi / 96.0 : (Content?.XamlRoot?.RasterizationScale ?? 1.0);
            FrozenImage.Width = b.Width / dpiScale;
            FrozenImage.Height = b.Height / dpiScale;

            // FrozenImage opacity controlled by DimPath only
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] FrozenImage sync load failed: {ex.Message}");
        }
    }

    // DPI scale used across the drawing/save partials.
    private double DpiScale => Content?.XamlRoot?.RasterizationScale ?? 1.0;

    // ---------- Win32 interop ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // AttachThreadInput bypasses the foreground lock that makes a bare
    // SetForegroundWindow fail, so the overlay reliably gets keyboard focus.
    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint thisThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != thisThread
                && AttachThreadInput(fgThread, thisThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            if (attached) AttachThreadInput(fgThread, thisThread, false);
        }
        catch { SetForegroundWindow(hwnd); }
    }

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    // Off-screen warm-up position for the first (black-erase) present.
    private const int OffscreenX = -32000;
    private const int OffscreenY = -32000;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_SHOWWINDOW   = 0x0040;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
}
