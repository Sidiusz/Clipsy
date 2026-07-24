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
    private enum InteractionMode { Idle, SelectingNew, MovingSelection, ResizingSelection, DrawingStroke, DrawingRect, Erasing, PlacingText, SelectingOcrText, MovingText }
    private enum HandlePos { TL, T, TR, R, BR, B, BL, L }

    private const double MinSelectionSize = 4.0;
    private const double SingleClickFallbackSize = 100.0;
    private const double HandleSize = 10.0;
    private const double HandleHitInflate = 6.0;

    private ScreenFreezeService.FrozenFrame _frame;
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

    private StrokeElement? _activeStroke;
    private Shape? _activeRectVisual;
    private Line? _activeLineVisual;
    private Point _activeRectAnchor;
    private TextBox? _activeTextBox;

    // OCR state
    private bool _inOcrMode;
    private bool _ocrPanelDragging;
    private Point _ocrPanelDragOffset;

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

        _drawing = new DrawingController(CommittedLayer);
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
        Closed += OnOverlayClosed;
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        ArmReveal();

        SetTool(ToolKind.None);
    }

    // Re-cloaks and re-runs the proven cloak→reveal handshake. Called from the
    // ctor and on every reuse, so the reveal path itself never changes.
    private int _composedCount;
    private void ArmReveal()
    {
        _cloaked = true;
        _revealed = false;
        try
        {
            int cloak = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            SetWindowLong(_hwnd, GWL_EXSTYLE, WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
            SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);
        }
        catch { }
        _composedCount = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFirstFrames;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFirstFrames;
        StartRevealWatchdog();
    }

    // Frame is decoded synchronously, so the 2nd composition tick is guaranteed
    // to contain it — uncloak then. No async-decode race.
    private void OnFirstFrames(object? s, object e)
    {
        _composedCount++;
        if (_composedCount < 2) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFirstFrames;
        Uncloak();
    }

    // ---------- Window reuse ----------
    // Building the WinUI window costs ~80 ms of XAML init per capture. Keep one
    // instance alive and re-arm it instead of constructing a new one each time.

    internal void PrepareForReuse(ScreenFreezeService.FrozenFrame frame)
    {
        _frame = frame;
        _closed = false;
        ResetForReuse();
        ConfigureAsOverlay();   // un-hides (SWP_SHOWWINDOW), resizes to current bounds
        TryLoadFrozenImage();   // swap in the new capture's bitmap
        ArmReveal();
    }

    // Hides + fully resets, keeping the instance alive for the next capture.
    internal void HideAndReset()
    {
        HideForClose();
        try { ShowWindow(_hwnd, SW_HIDE); } catch { }
        ResetForReuse();
    }

    // Returns every interaction surface to its just-opened state. Composed from
    // the existing exit/cancel paths plus explicit flag/visual resets.
    private void ResetForReuse()
    {
        if (_inOcrMode) ExitOcrMode();
        if (_tempEyedropper) ExitTempEyedropper(pick: false);
        if (_eyedropperActive) ExitEyedropperMode();
        if (_activeTextBox != null) CancelText();

        RemoveActiveDrawingVisuals();
        _drawing.ClearAll();
        _movingText = null;
        _draggingActiveText = false;
        _mode = InteractionMode.Idle;

        if (_selectionRenderHooked)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSelectionRenderTick;
            _selectionRenderHooked = false;
        }
        _selectionVisualDirty = false;

        _hasSelection = false;
        _selectionFromFallback = false;
        _selectionRect = default;
        _selectionAtDragStart = default;
        _lastClickTick = 0;
        _anchorRight = true;
        _anchorBottom = true;

        SelectionLayer.Visibility = Visibility.Collapsed;
        HideToolbars();
        SetTool(ToolKind.None);

        try { ColorFlyout?.Hide(); } catch { }
        if (ShapesFlyout != null) ShapesFlyout.Visibility = Visibility.Collapsed;
        if (FontsFlyout != null) FontsFlyout.Visibility = Visibility.Collapsed;
        _shapesClickHandled = false;
        _textClickHandled = false;
        if (_hoverTimer != null) { _hoverTimer.Stop(); _hoverTimer = null; }

        // New capture → eyedropper must resample; drop the aliased buffer.
        _eyedropperPixels = null;

        // Intro animation replays from these start values.
        DimLayer.Opacity = 0;
        Hint.Opacity = 0;
        HintTranslate.Y = 0;
        Hint.Visibility = Visibility.Visible;
    }

    private void RemoveActiveDrawingVisuals()
    {
        _drawing.CancelActiveStroke();
        if (_activeRectVisual != null) DrawingCanvas.Children.Remove(_activeRectVisual);
        if (_activeLineVisual != null) DrawingCanvas.Children.Remove(_activeLineVisual);
        if (_activeArrowVisual != null) DrawingCanvas.Children.Remove(_activeArrowVisual);
        _activeStroke = null;
        _activeRectVisual = null;
        _activeLineVisual = null;
        _activeArrowVisual = null;
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

                Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var hintWidth = Hint.DesiredSize.Width;
                Hint.Margin = new Thickness(primaryCenterX - (hintWidth / 2), primaryTopY, 0, 0);
            }
            else
            {
                Hint.Margin = new Thickness(50, 72, 0, 0);
            }
        }
        catch
        {
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

        UpdateDimGeometry(null);

    }

    private bool _cloaked = true;
    private bool _revealed;
    private bool _closed;
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
                StripLayered();
                return;
            }
            if (ticksAfterMove < 3) return;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPostMoveTick;
            CompleteReveal();
        }
    }

    private void StripLayered()
    {
        // Strip WS_EX_LAYERED while cloaked: a full-screen layered window takes
        // DWM's slow path and caps FPS on high-refresh monitors.
        SetWindowLong(_hwnd, GWL_EXSTYLE, WS_EX_TOPMOST | WS_EX_TOOLWINDOW);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void CompleteReveal()
    {
        if (_revealed) return;
        _revealed = true;
        StripLayered();
        int cloak = 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        // Shown with NOACTIVATE, so grab foreground/focus explicitly once visible.
        ForceForeground(_hwnd);
        RootGrid.Focus(FocusState.Programmatic);
        // Force a GPU redraw now the window is visible: a ClearAll invalidate
        // issued while hidden is dropped, leaving last capture's drawings.
        _drawing?.InvalidateCommitted();
        PlayIntroAnimations();
    }

    // Under heavy load the CompositionTarget.Rendering ticks that drive the
    // cloak→reveal handshake can stall, leaving the overlay invisible forever
    // (and _current pinned, blocking every later capture). Force the reveal.
    private void StartRevealWatchdog()
    {
        var wd = DispatcherQueue.CreateTimer();
        wd.Interval = TimeSpan.FromMilliseconds(450);
        wd.IsRepeating = false;
        wd.Tick += (_, _) =>
        {
            if (_revealed || _closed) return;
            _cloaked = false;
            var b = _frame.VirtualBounds;
            SetWindowPos(_hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE);
            CompleteReveal();
        };
        wd.Start();
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
            Storyboard.SetTarget(dimFade, DimLayer);
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
            DimLayer.Opacity = 1;
            Hint.Opacity = 1;
            HintTranslate.Y = 0;
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDimGeometry(_hasSelection ? _selectionRect : null);
    }

    // WinUI 3 retains the Window after Close, so its full-screen frozen bitmap,
    // BMP buffer and composition surface would leak ~150 MB per capture. Null
    // every heavy field so they become collectible even if the shell lingers.
    private void OnOverlayClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        _scanTimer?.Stop();
        _hoverTimer?.Stop();
        if (_selectionRenderHooked)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSelectionRenderTick;
            _selectionRenderHooked = false;
        }

        _eyedropperPixels = null;

        try
        {
            FrozenImage.Source = null;
            MagBrush.ImageSource = null;
            _magBitmap = null;
            DrawingCanvas.Children.Clear();
            CursorPreviewLayer.Children.Clear();
            // Win2D holds a GPU device; release it explicitly.
            try { _drawing?.DisposeResources(); } catch { }
            try { CommittedLayer.RemoveFromVisualTree(); } catch { }
            RootGrid.Children.Clear();
            this.Content = null;
        }
        catch { }

        _frame = null!;
    }

    private void TryLoadFrozenImage()
    {
        try
        {
            // Raw BGRA copies straight into the WriteableBitmap — no decode, no
            // per-row loop. BitmapImage decodes async and would flash black.
            var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(_frame.PixelWidth, _frame.PixelHeight);
            using (var dst = wb.PixelBuffer.AsStream())
                dst.Write(_frame.PixelBytes, 0, _frame.PixelBytes.Length);
            wb.Invalidate();
            FrozenImage.Source = wb;

            var b = _frame.VirtualBounds;
            var rawDpi = GetDpiForWindow(_hwnd);
            var dpiScale = rawDpi > 0 ? rawDpi / 96.0 : (Content?.XamlRoot?.RasterizationScale ?? 1.0);
            FrozenImage.Width = b.Width / dpiScale;
            FrozenImage.Height = b.Height / dpiScale;

            // FrozenImage opacity controlled by DimLayer only
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

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
