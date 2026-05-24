using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public sealed partial class CaptureOverlayWindow : Window
{
    private enum InteractionMode { Idle, SelectingNew, MovingSelection, ResizingSelection, DrawingStroke, DrawingRect, Erasing, PlacingText, SelectingOcrText }
    private enum HandlePos { TL, T, TR, R, BR, B, BL, L }

    private const double MinSelectionSize = 4.0;
    private const double SingleClickFallbackSize = 100.0;
    private const double HandleSize = 10.0;
    private const double HandleHitInflate = 6.0;
    private const double EraserRadius = 4.0;

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

        // Initial picker colour set in code rather than XAML — assigning
        // ColorPicker.Color through the markup parser throws XamlParseException
        // (0x802B000A) at runtime on Windows App SDK 1.6.
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
        ConfigureAsOverlay();
        DisableDwmDecorations();
        // Load the frozen frame synchronously into the Image source so the
        // very first frame the compositor renders already shows the desktop
        // snapshot instead of black-then-desktop.
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
        // Wait for the second composition tick before revealing the window —
        // the first Rendering event fires before the swap chain has the
        // frozen-frame image, so Low-priority dispatcher posting still flashed
        // black on some machines.
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

        ToolTipService.SetToolTip(RecordBtn,     Strings.Get("TipRecord"));
        ToolTipService.SetToolTip(ScreenshotBtn, Strings.Get("TipScreenshot"));
        ToolTipService.SetToolTip(CopyBtn,       Strings.Get("TipCopy"));
        ToolTipService.SetToolTip(CancelBtn,     Strings.Get("TipCancel"));

        ToolTipService.SetToolTip(ColorBtn, Strings.Get("TipColor"));
        // Flyout-hosted buttons may not be materialized at ctor time in
        // WinUI 3; guard against null so a failed SetToolTip doesn't kill
        // the overlay ctor (which would also swallow PrintScreen via the
        // already-installed LL keyboard hook).
        if (EyedropperBtn   != null) ToolTipService.SetToolTip(EyedropperBtn,   Strings.Get("TipEyedropper"));
        if (ColorCancelBtn  != null) ToolTipService.SetToolTip(ColorCancelBtn,  Strings.Get("TipColorCancel"));
        if (ColorConfirmBtn != null) ToolTipService.SetToolTip(ColorConfirmBtn, Strings.Get("TipColorApply"));
        ToolTipService.SetToolTip(PencilBtn, Strings.Get("TipPencil"));
        ToolTipService.SetToolTip(EllipseBtn, Strings.Get("TipEllipse"));
        ToolTipService.SetToolTip(LineBtn,    Strings.Get("TipLine"));
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
        var b = _frame.VirtualBounds;

        // AppWindow.MoveAndResize and SetWindowPos both take physical screen pixels.
        // Never divide b.Width/b.Height by dpiScale here — that shrinks the window.
        _appWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        SetWindowPos(_hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
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
            // Cloak the window until the first XAML composition lands. Without
            // this DWM shows the window's default opaque surface for one frame
            // (visible as a black flash) before the frozen-frame image paints.
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

            // Set window style to remove all borders
            SetWindowLong(_hwnd, GWL_STYLE, WS_POPUP);
            SetWindowLong(_hwnd, GWL_EXSTYLE, WS_EX_TOPMOST | WS_EX_TOOLWINDOW);

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
        int cloak = 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
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
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(_frame.PngBytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.SetSource(stream);
            FrozenImage.Source = bmp;

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

    // ---------- Handles ----------

    private bool _shapesClickHandled = false;

    private void OnShapesClick(object sender, RoutedEventArgs e)
    {
        _shapesClickHandled = true;

        // Cancel hover timer to prevent flyout opening after click
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        // Hide flyout if it's open
        if (ShapesFlyout != null)
        {
            ShapesFlyout.Visibility = Visibility.Collapsed;
        }

        // Toggle: re-click active shape deselects
        SetTool(_drawing.Settings.Tool == _currentShapeTool ? ToolKind.None : _currentShapeTool);

        // Reset flag after short delay
        var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        resetTimer.Tick += (s, args) => { _shapesClickHandled = false; resetTimer.Stop(); };
        resetTimer.Start();
    }

    private static void FadeOutFlyout(FrameworkElement flyout)
    {
        if (flyout.Visibility == Visibility.Collapsed) return;
        var anim = new DoubleAnimation { From = 1.0, To = 0.0, Duration = new Duration(TimeSpan.FromMilliseconds(100)), EnableDependentAnimation = true };
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, flyout);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Completed += (_, _) => { flyout.Visibility = Visibility.Collapsed; flyout.Opacity = 1.0; };
        sb.Begin();
    }

    private static void ShowFlyout(FrameworkElement flyout)
    {
        flyout.Opacity = 0.0;
        flyout.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(120)), EnableDependentAnimation = true };
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, flyout);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void OnShapesPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ShapesFlyout == null || ShapesBtn == null || _shapesClickHandled) return;
        // Close font flyout if open
        if (FontsFlyout != null) FadeOutFlyout(FontsFlyout);

        // Cancel any existing timer
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        // Start hover delay timer
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += OnHoverTimerTick;
        _hoverTimer.Start();
    }

    private void OnHoverTimerTick(object? sender, object e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        if (ShapesFlyout == null || ShapesBtn == null) return;

        PositionShapesFlyout();
        ShowFlyout(ShapesFlyout);
    }

    private void PositionShapesFlyout()
    {
        if (ShapesFlyout == null || ShapesBtn == null) return;

        ShapesFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var flyoutSize = ShapesFlyout.DesiredSize;

        // Get shapes button position
        var transform = ShapesBtn.TransformToVisual(RootGrid);
        var buttonPos = transform.TransformPoint(new Point(0, 0));

        // Position flyout to the right of shapes button
        double x = buttonPos.X + ShapesBtn.ActualWidth + 8;
        double y = buttonPos.Y + (ShapesBtn.ActualHeight - flyoutSize.Height) / 2;

        // Keep flyout within screen bounds
        if (x + flyoutSize.Width > RootGrid.ActualWidth - 8)
        {
            x = buttonPos.X - flyoutSize.Width - 8; // Show on left side
        }
        y = System.Math.Clamp(y, 8, System.Math.Max(8, RootGrid.ActualHeight - flyoutSize.Height - 8));

        Canvas.SetLeft(ShapesFlyout, x);
        Canvas.SetTop(ShapesFlyout, y);
    }

    private void OnShapesPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Cancel hover timer when cursor leaves button
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        // Start timer to hide flyout after small delay
        // This allows cursor to move to flyout without closing it
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTimer.Tick += (s, args) => {
            if (ShapesFlyout != null) FadeOutFlyout(ShapesFlyout);
            _hoverTimer?.Stop();
            _hoverTimer = null;
        };
        _hoverTimer.Start();
    }

    private void OnShapesFlyoutPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Cancel any hide timer when cursor enters flyout
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }
    }

    private void OnShapesFlyoutPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ShapesFlyout != null) FadeOutFlyout(ShapesFlyout);
    }

    // ---------- Text / Fonts flyout (mirrors Shapes flyout) ----------

    private bool _textClickHandled;

    private void OnTextClick(object sender, RoutedEventArgs e)
    {
        _textClickHandled = true;

        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        if (FontsFlyout != null) FontsFlyout.Visibility = Visibility.Collapsed;
        // Toggle: re-click active text tool deselects
        SetTool(_drawing.Settings.Tool == ToolKind.Text ? ToolKind.None : ToolKind.Text);

        var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        resetTimer.Tick += (s, args) => { _textClickHandled = false; resetTimer.Stop(); };
        resetTimer.Start();
    }

    private void OnTextPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (FontsFlyout == null || TextBtn == null || _textClickHandled) return;

        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += OnFontHoverTimerTick;
        _hoverTimer.Start();
    }

    private void OnFontHoverTimerTick(object? sender, object e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnFontHoverTimerTick;
            _hoverTimer = null;
        }
        if (FontsFlyout == null || TextBtn == null) return;
        if (ShapesFlyout != null) FadeOutFlyout(ShapesFlyout);
        EnsureFontListBuilt();
        PositionFontsFlyout();
        ShowFlyout(FontsFlyout);
    }

    private List<string>? _systemFonts;

    private void EnsureFontListBuilt()
    {
        if (FontList == null || _systemFonts != null) return;
        try
        {
            // GDI+ enumeration via System.Drawing.Common. Already referenced
            // by the project (image processing path). Filter to families with
            // a regular face so we don't surface broken icon/symbol entries.
            using var coll = new System.Drawing.Text.InstalledFontCollection();
            _systemFonts = coll.Families
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Font enumeration failed: {ex.Message}");
            _systemFonts = new List<string> { "Segoe UI Variable", "Segoe UI", "Arial" };
        }
        // Prepend the bundled Onest entry so it always shows even if not
        // installed system-wide.
        _systemFonts.Insert(0, "Onest (bundled)");
        RenderFontList(string.Empty);
    }

    private void RenderFontList(string filter)
    {
        if (FontList == null || _systemFonts == null) return;
        FontList.Children.Clear();
        IEnumerable<string> items = _systemFonts;
        if (!string.IsNullOrWhiteSpace(filter))
            items = items.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase));
        foreach (var name in items)
        {
            var (family, tag) = name == "Onest (bundled)"
                ? ("ms-appx:///Assets/Fonts/Onest-VariableFont_wght.ttf#Onest, Inter, Segoe UI, sans-serif",
                   "ms-appx:///Assets/Fonts/Onest-VariableFont_wght.ttf#Onest, Inter, Segoe UI, sans-serif")
                : (name, name);
            var preview = new TextBlock
            {
                Text = name,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            try { preview.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family); }
            catch { /* fall back to inherited */ }
            var btn = new Button
            {
                Content = preview,
                Tag = tag,
                Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["ClipsyButtonGhost"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 1, 0, 1),
            };
            btn.Click += OnFontPick;
            FontList.Children.Add(btn);
        }
    }

    private void OnFontFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (FontFilterBox == null) return;
        RenderFontList(FontFilterBox.Text ?? string.Empty);
    }

    private void PositionFontsFlyout()
    {
        if (FontsFlyout == null || TextBtn == null) return;
        FontsFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var flyoutSize = FontsFlyout.DesiredSize;
        var transform = TextBtn.TransformToVisual(RootGrid);
        var buttonPos = transform.TransformPoint(new Point(0, 0));
        double x = buttonPos.X + TextBtn.ActualWidth + 8;
        double y = buttonPos.Y + (TextBtn.ActualHeight - flyoutSize.Height) / 2;
        if (x + flyoutSize.Width > RootGrid.ActualWidth - 8)
            x = buttonPos.X - flyoutSize.Width - 8;
        y = System.Math.Clamp(y, 8, System.Math.Max(8, RootGrid.ActualHeight - flyoutSize.Height - 8));
        Canvas.SetLeft(FontsFlyout, x);
        Canvas.SetTop(FontsFlyout, y);
    }

    private void OnTextPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnFontHoverTimerTick;
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTimer.Tick += (s, args) =>
        {
            if (FontsFlyout != null) FadeOutFlyout(FontsFlyout);
            _hoverTimer?.Stop();
            _hoverTimer = null;
        };
        _hoverTimer.Start();
    }

    private void OnFontsFlyoutPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer.Tick -= OnFontHoverTimerTick;
            _hoverTimer = null;
        }
    }

    private void OnFontsFlyoutPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (FontsFlyout != null) FadeOutFlyout(FontsFlyout);
    }

    private void OnFontPick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string family) return;
        _drawing.Settings.TextFont = family;
        SetTool(ToolKind.Text);
        // Reflect choice on the toolbar T glyph so the user sees current font.
        if (TextBtnGlyph != null)
        {
            try { TextBtnGlyph.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family); }
            catch { /* fallback to inherited font */ }
        }
    }

    private void OnShapePick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        var tool = tag switch
        {
            "Pencil" => ToolKind.Pencil,
            "Rectangle" => ToolKind.Rectangle,
            "Ellipse" => ToolKind.Ellipse,
            "Line" => ToolKind.Line,
            "Text" => ToolKind.Text,
            _ => ToolKind.None,
        };
        // Toggle: re-click active tool deselects
        SetTool(_drawing.Settings.Tool == tool ? ToolKind.None : tool);

        // Update current shape tool if it's a shape
        if (tool is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line)
        {
            _currentShapeTool = tool;
        }
    }

    private void BuildHandles()
    {
        for (int i = 0; i < 8; i++)
        {
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x7D, 0x0D)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            HandlesLayer.Children.Add(r);
            _handleVisuals.Add(r);
        }
    }

    /// <summary>
    /// Anchor positions in selection-local coords, clamped so the handle stays
    /// fully visible when the selection touches a screen edge.
    /// </summary>
    private (double X, double Y, HandlePos H)[] GetClampedAnchors()
    {
        double w = _selectionRect.Width, h = _selectionRect.Height;
        var raw = new (double X, double Y, HandlePos H)[]
        {
            (0, 0, HandlePos.TL), (w / 2, 0, HandlePos.T), (w, 0, HandlePos.TR),
            (w, h / 2, HandlePos.R),
            (w, h, HandlePos.BR), (w / 2, h, HandlePos.B), (0, h, HandlePos.BL),
            (0, h / 2, HandlePos.L),
        };
        double rootW = RootGrid.ActualWidth;
        double rootH = RootGrid.ActualHeight;
        double margin = HandleSize / 2 + 2;
        var result = new (double X, double Y, HandlePos H)[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double rx = _selectionRect.X + raw[i].X;
            double ry = _selectionRect.Y + raw[i].Y;
            rx = System.Math.Clamp(rx, margin, System.Math.Max(margin, rootW - margin));
            ry = System.Math.Clamp(ry, margin, System.Math.Max(margin, rootH - margin));
            result[i] = (rx - _selectionRect.X, ry - _selectionRect.Y, raw[i].H);
        }
        return result;
    }

    private void PositionHandles()
    {
        if (!_hasSelection)
        {
            foreach (var hv in _handleVisuals) hv.Visibility = Visibility.Collapsed;
            return;
        }
        var anchors = GetClampedAnchors();
        for (int i = 0; i < 8; i++)
        {
            Canvas.SetLeft(_handleVisuals[i], anchors[i].X - HandleSize / 2);
            Canvas.SetTop(_handleVisuals[i], anchors[i].Y - HandleSize / 2);
            _handleVisuals[i].Visibility = Visibility.Visible;
        }
    }

    private bool TryGetHandle(Point rootPos, out HandlePos handle)
    {
        handle = HandlePos.TL;
        if (!_hasSelection) return false;
        var local = new Point(rootPos.X - _selectionRect.X, rootPos.Y - _selectionRect.Y);
        double half = HandleSize / 2 + HandleHitInflate;
        foreach (var a in GetClampedAnchors())
        {
            if (System.Math.Abs(local.X - a.X) <= half && System.Math.Abs(local.Y - a.Y) <= half)
            {
                handle = a.H;
                return true;
            }
        }
        return false;
    }

    // ---------- Pointer input ----------

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var cp = e.GetCurrentPoint(RootGrid);
        var pos = cp.Position;
        bool rmb = cp.Properties.IsRightButtonPressed;
        bool lmb = cp.Properties.IsLeftButtonPressed;

        if (_eyedropperActive)
        {
            if (lmb)
                ApplyPickedColor(SamplePixel(pos));
            ExitEyedropperMode();
            e.Handled = true;
            return;
        }

        if (_inOcrMode)
        {
            // OCR mode owns the overlay. Text selection happens inside the
            // floating OcrTextBox; clicks elsewhere are ignored so they
            // don't paint, move the selection, or open menus.
            return;
        }

        // When a paint tool is active the selection is locked: clicks
        // anywhere on the overlay paint (LMB) or erase (RMB). The user can
        // draw outside the selection rectangle without accidentally
        // starting a new selection.
        if (_drawing.Settings.Tool != ToolKind.None)
        {
            if (rmb)
            {
                _mode = InteractionMode.Erasing;
                RootGrid.CapturePointer(e.Pointer);
                TryEraseAt(pos);
                e.Handled = true;
                return;
            }
            if (lmb)
            {
                StartToolPress(pos, e.Pointer);
                e.Handled = true;
                return;
            }
            return;
        }

        if (rmb) return; // let RightTapped surface the overlay context menu

        if (!lmb) return;

        if (_hasSelection && TryGetHandle(pos, out var hp))
        {
            _mode = InteractionMode.ResizingSelection;
            _activeHandle = hp;
            _selectionAtDragStart = _selectionRect;
            _dragStart = pos;
            RootGrid.CapturePointer(e.Pointer);
            return;
        }

        if (_hasSelection && IsInsideSelection(pos))
        {
            _mode = InteractionMode.MovingSelection;
            _selectionAtDragStart = _selectionRect;
            _dragStart = pos;
            RootGrid.CapturePointer(e.Pointer);
            return;
        }

        // Outside or no selection: start new selection
        if (_drawing.Elements.Count > 0)
        {
            _drawing.ClearAll();
        }
        _mode = InteractionMode.SelectingNew;
        _hasSelection = false;
        _dragStart = pos;
        _selectionRect = new Rect(pos.X, pos.Y, 0, 0);
        UpdateSelectionVisual();
        Hint.Visibility = Visibility.Collapsed;
        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;
        if (_eyedropperActive)
        {
            UpdateMagnifier(pos);
            return;
        }
        var local = new Point(pos.X - _selectionRect.X, pos.Y - _selectionRect.Y);
        if (_drawing.Settings.Tool is ToolKind.Pencil or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line)
        {
            _pencilPreview.Visibility = Visibility.Visible;
            _textPreview.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(_pencilPreview, local.X - _pencilPreview.Width / 2);
            Canvas.SetTop(_pencilPreview, local.Y - _pencilPreview.Height / 2);
        }
        else if (_drawing.Settings.Tool == ToolKind.Text && _activeTextBox == null)
        {
            _pencilPreview.Visibility = Visibility.Collapsed;
            _textPreview.Visibility = Visibility.Visible;
            _textPreview.FontSize = _drawing.Settings.TextSize;
            // Mirror the current font choice and center the glyph on the
            // cursor — matches StartTextEntry's anchor so the preview lands
            // exactly where the committed text will sit.
            try { _textPreview.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(_drawing.Settings.TextFont); }
            catch { /* fallback to inherited font */ }
            var (pw, ph) = MeasureGlyph(_textPreview.Text, _textPreview.FontSize, _textPreview.FontFamily);
            Canvas.SetLeft(_textPreview, local.X - pw / 2);
            Canvas.SetTop(_textPreview,  local.Y - ph / 2);
        }
        else
        {
            _pencilPreview.Visibility = Visibility.Collapsed;
            _textPreview.Visibility = Visibility.Collapsed;
        }

        switch (_mode)
        {
            case InteractionMode.SelectingNew:
                _selectionRect = MakeRect(_dragStart, pos);
                UpdateSelectionVisual();
                break;
            case InteractionMode.MovingSelection:
            {
                double dx = pos.X - _dragStart.X;
                double dy = pos.Y - _dragStart.Y;
                _selectionRect = new Rect(
                    _selectionAtDragStart.X + dx,
                    _selectionAtDragStart.Y + dy,
                    _selectionAtDragStart.Width,
                    _selectionAtDragStart.Height);
                UpdateSelectionVisual();
                break;
            }
            case InteractionMode.ResizingSelection:
                _selectionRect = ResizeFromHandle(_selectionAtDragStart, _activeHandle, pos);
                UpdateSelectionVisual();
                break;
            case InteractionMode.DrawingStroke:
                // GetIntermediatePoints returns all high-frequency samples buffered between
                // PointerMoved events — critical for smooth strokes at 144Hz+.
                var pts = e.GetIntermediatePoints(RootGrid);
                if (pts != null && pts.Count > 0)
                    foreach (var p in pts) ExtendStroke(p.Position);
                else
                    ExtendStroke(pos);
                break;
            case InteractionMode.DrawingRect:
                UpdateActiveShape(pos);
                break;
            case InteractionMode.Erasing:
                TryEraseAt(pos);
                break;
            case InteractionMode.SelectingOcrText:
                UpdateOcrDragSelection(pos);
                break;
        }
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;
        RootGrid.ReleasePointerCapture(e.Pointer);

        switch (_mode)
        {
            case InteractionMode.SelectingNew:
            {
                var rect = MakeRect(_dragStart, pos);
                if (rect.Width < MinSelectionSize && rect.Height < MinSelectionSize)
                {
                    double x = _dragStart.X - SingleClickFallbackSize / 2;
                    double y = _dragStart.Y - SingleClickFallbackSize / 2;
                    rect = new Rect(x, y, SingleClickFallbackSize, SingleClickFallbackSize);
                }
                _selectionRect = rect;
                _hasSelection = true;
                UpdateSelectionVisual();
                ShowToolbars();
                break;
            }
            case InteractionMode.DrawingStroke:
                FinishStroke();
                break;
            case InteractionMode.DrawingRect:
                FinishActiveShape();
                break;
            case InteractionMode.SelectingOcrText:
                FinishOcrSelection(pos);
                break;
        }

        _mode = InteractionMode.Idle;
    }

    private void OnRootPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_hasSelection) return;
        int delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double step = delta > 0 ? 1.0 : -1.0;

        // Wheel while typing in a text box resizes the active text element
        // instead of the brush — same gesture, the obvious meaning depends on
        // what the user is currently doing.
        if (_activeTextBox != null)
        {
            _drawing.Settings.BrushSize = System.Math.Clamp(_drawing.Settings.BrushSize + step, 1.0, 64.0);
            _activeTextBox.FontSize = _drawing.Settings.TextSize;
            e.Handled = true;
            return;
        }

        _drawing.Settings.BrushSize = System.Math.Clamp(_drawing.Settings.BrushSize + step, 1.0, 64.0);
        UpdatePreviewForThickness(_drawing.Settings.BrushSize);

        // Refresh the text-tool preview live too — wheeling between letters
        // shouldn't require nudging the cursor for the size hint to update.
        if (_textPreview != null && _drawing.Settings.Tool == ToolKind.Text)
            _textPreview.FontSize = _drawing.Settings.TextSize;

        // Apply the new thickness live to whichever shape the user is currently
        // dragging so the visual matches the cursor preview immediately.
        if (_activeStrokeVisual != null)
            _activeStrokeVisual.StrokeThickness = _drawing.Settings.PencilThickness;
        if (_activeRectVisual != null)
            _activeRectVisual.StrokeThickness = _drawing.Settings.Tool == ToolKind.Ellipse
                ? _drawing.Settings.EllipseThickness
                : _drawing.Settings.RectangleThickness;
        if (_activeLineVisual != null)
            _activeLineVisual.StrokeThickness = _drawing.Settings.LineThickness;

        e.Handled = true;
    }

    private void UpdatePreviewForThickness(double _thickness)
    {
        var d = _drawing.Settings.PreviewDiameter;
        _pencilPreview.Width = d;
        _pencilPreview.Height = d;
    }

    private void OnRootRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_inOcrMode)
        {
            // OCR mode owns right-click; never show the overlay context menu.
            e.Handled = true;
            return;
        }
        if (_drawing.Settings.Tool != ToolKind.None && _hasSelection)
        {
            var pos = e.GetPosition(RootGrid);
            if (IsInsideSelection(pos))
            {
                // RMB inside selection with tool active is erase, not menu.
                e.Handled = true;
                return;
            }
        }
        UpdateContextMenuVisibility();
    }

    // ---------- Selection / drawing helpers ----------

    private bool IsInsideSelection(Point p)
    {
        return _hasSelection
            && p.X >= _selectionRect.X && p.X <= _selectionRect.X + _selectionRect.Width
            && p.Y >= _selectionRect.Y && p.Y <= _selectionRect.Y + _selectionRect.Height;
    }

    private Point ToCanvas(Point root) => new(root.X - _selectionRect.X, root.Y - _selectionRect.Y);

    private static Rect MakeRect(Point a, Point b)
    {
        double x = System.Math.Min(a.X, b.X);
        double y = System.Math.Min(a.Y, b.Y);
        double w = System.Math.Abs(a.X - b.X);
        double h = System.Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    private Rect ResizeFromHandle(Rect baseRect, HandlePos h, Point pos)
    {
        double left = baseRect.X, top = baseRect.Y, right = baseRect.X + baseRect.Width, bottom = baseRect.Y + baseRect.Height;
        switch (h)
        {
            case HandlePos.TL: left = pos.X; top = pos.Y; break;
            case HandlePos.T:  top = pos.Y; break;
            case HandlePos.TR: right = pos.X; top = pos.Y; break;
            case HandlePos.R:  right = pos.X; break;
            case HandlePos.BR: right = pos.X; bottom = pos.Y; break;
            case HandlePos.B:  bottom = pos.Y; break;
            case HandlePos.BL: left = pos.X; bottom = pos.Y; break;
            case HandlePos.L:  left = pos.X; break;
        }
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void UpdateSelectionVisual()
    {
        if (_selectionRect.Width <= 0 || _selectionRect.Height <= 0)
        {
            SelectionLayer.Visibility = Visibility.Collapsed;
            UpdateDimGeometry(null);
            PositionToolbars();
            return;
        }

        SelectionLayer.Visibility = Visibility.Visible;
        SelectionLayer.Margin = new Thickness(_selectionRect.X, _selectionRect.Y, 0, 0);
        SelectionLayer.Width = _selectionRect.Width;
        SelectionLayer.Height = _selectionRect.Height;

        SelectionBorder.Width = _selectionRect.Width;
        SelectionBorder.Height = _selectionRect.Height;
        HandlesLayer.Width = _selectionRect.Width;
        HandlesLayer.Height = _selectionRect.Height;
        CursorPreviewLayer.Width = _selectionRect.Width;
        CursorPreviewLayer.Height = _selectionRect.Height;

        PositionHandles();
        UpdateDimGeometry(_selectionRect);
        PositionToolbars();
    }

    private void UpdateDimGeometry(Rect? hole)
    {
        double w = RootGrid.ActualWidth;
        double h = RootGrid.ActualHeight;
        if (w <= 0) w = _frame.VirtualBounds.Width;
        if (h <= 0) h = _frame.VirtualBounds.Height;

        _dimFull.Rect = new Rect(0, 0, w, h);

        // EvenOdd: a valid hole punches through the dim; an empty rect collapses
        // the second geometry so no hole shows (both rects identical → even fill = nothing).
        // Zero-size rect means no hole: EvenOdd ignores 0-area geometry.
        _dimHole.Rect = (hole.HasValue && hole.Value.Width > 0 && hole.Value.Height > 0)
            ? hole.Value
            : new Rect(0, 0, 0, 0);
    }

    private void ShowToolbars()
    {
        BottomToolbar.Visibility = Visibility.Visible;
        RightToolbar.Visibility = Visibility.Visible;
        PositionToolbars();
    }

    private void HideToolbars()
    {
        BottomToolbar.Visibility = Visibility.Collapsed;
        RightToolbar.Visibility = Visibility.Collapsed;
    }

    private void PositionToolbars()
    {
        if (BottomToolbar.Visibility != Visibility.Visible || !_hasSelection) return;
        BottomToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RightToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;

        double bw = BottomToolbar.DesiredSize.Width;
        double bh = BottomToolbar.DesiredSize.Height;
        double rw = RightToolbar.DesiredSize.Width;
        double rh = RightToolbar.DesiredSize.Height;

        double bx = _selectionRect.X + (_selectionRect.Width - bw) / 2;
        double by = _selectionRect.Y + _selectionRect.Height + 12;
        if (by + bh > rootH - 8)
        {
            by = _selectionRect.Y - bh - 12;
            if (by < 8) by = _selectionRect.Y + _selectionRect.Height - bh - 8;
        }
        bx = System.Math.Clamp(bx, 8, System.Math.Max(8, rootW - bw - 8));
        Canvas.SetLeft(BottomToolbar, bx);
        Canvas.SetTop(BottomToolbar, by);

        double rx = _selectionRect.X + _selectionRect.Width + 12;
        double ry = _selectionRect.Y + (_selectionRect.Height - rh) / 2;
        if (rx + rw > rootW - 8)
        {
            rx = _selectionRect.X - rw - 12;
            if (rx < 8) rx = _selectionRect.X + _selectionRect.Width - rw - 8;
        }
        ry = System.Math.Clamp(ry, 8, System.Math.Max(8, rootH - rh - 8));
        Canvas.SetLeft(RightToolbar, rx);
        Canvas.SetTop(RightToolbar, ry);
    }

    // ---------- Drawing tools ----------

    private void StartToolPress(Point pos, Pointer pointer)
    {
        // Drawings live in root DIPs so they stay fixed on screen when the
        // selection rectangle moves or resizes.
        switch (_drawing.Settings.Tool)
        {
            case ToolKind.Pencil:
                _mode = InteractionMode.DrawingStroke;
                _activeStrokeVisual = new Polyline
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.PencilThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                _activeStrokeVisual.Points.Add(pos);
                _activeStroke = new StrokeElement
                {
                    Visual = _activeStrokeVisual,
                    Points = new List<Point> { pos },
                    Thickness = _drawing.Settings.PencilThickness,
                };
                DrawingCanvas.Children.Add(_activeStrokeVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Rectangle:
                _mode = InteractionMode.DrawingRect;
                _activeRectAnchor = pos;
                _activeRectVisual = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.RectangleThickness,
                    Width = 0,
                    Height = 0,
                };
                Canvas.SetLeft(_activeRectVisual, pos.X);
                Canvas.SetTop(_activeRectVisual, pos.Y);
                DrawingCanvas.Children.Add(_activeRectVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Ellipse:
                _mode = InteractionMode.DrawingRect;
                _activeRectAnchor = pos;
                _activeRectVisual = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.EllipseThickness,
                    Width = 0,
                    Height = 0,
                };
                Canvas.SetLeft(_activeRectVisual, pos.X);
                Canvas.SetTop(_activeRectVisual, pos.Y);
                DrawingCanvas.Children.Add(_activeRectVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Line:
                _mode = InteractionMode.DrawingRect;
                _activeLineVisual = new Line
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.LineThickness,
                    X1 = pos.X,
                    Y1 = pos.Y,
                    X2 = pos.X,
                    Y2 = pos.Y,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                DrawingCanvas.Children.Add(_activeLineVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Text:
                // Click-to-place: do not enter a drag mode and do not capture
                // the pointer. PointerReleased resets _mode to Idle, and the
                // TextBox keeps focus until LostFocus / Enter / Esc.
                StartTextEntry(pos);
                break;
        }
    }

    private void ExtendStroke(Point pos)
    {
        if (_activeStroke == null || _activeStrokeVisual == null) return;
        _activeStroke.Points.Add(pos);
        _activeStrokeVisual.Points.Add(pos);
    }

    private void FinishStroke()
    {
        if (_activeStroke == null || _activeStrokeVisual == null) return;
        // Single click → zero-distance stroke. Polyline with one point (or two
        // identical points) renders nothing even with Round caps. Add a 0.01-px
        // sibling so the round end-cap paints a visible dot.
        if (_activeStroke.Points.Count == 1)
        {
            var only = _activeStroke.Points[0];
            var twin = new Point(only.X + 0.01, only.Y + 0.01);
            _activeStroke.Points.Add(twin);
            _activeStrokeVisual.Points.Add(twin);
        }
        DrawingCanvas.Children.Remove(_activeStrokeVisual);
        _drawing.Add(_activeStroke);
        _activeStroke = null;
        _activeStrokeVisual = null;
    }

    private void UpdateActiveShape(Point pos)
    {
        if (_activeLineVisual != null)
        {
            _activeLineVisual.X2 = pos.X;
            _activeLineVisual.Y2 = pos.Y;
            return;
        }

        if (_activeRectVisual == null) return;
        double x = System.Math.Min(_activeRectAnchor.X, pos.X);
        double y = System.Math.Min(_activeRectAnchor.Y, pos.Y);
        double w = System.Math.Abs(pos.X - _activeRectAnchor.X);
        double h = System.Math.Abs(pos.Y - _activeRectAnchor.Y);
        Canvas.SetLeft(_activeRectVisual, x);
        Canvas.SetTop(_activeRectVisual, y);
        // Below the stroke thickness an ellipse degenerates into a strip — WinUI
        // still renders the stroke across the longer axis. Hide the visual until
        // the user drags out a usable size to avoid the "circle = line" artifact.
        double minSide = System.Math.Max(2.0, _activeRectVisual.StrokeThickness);
        _activeRectVisual.Visibility = (w < minSide || h < minSide)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _activeRectVisual.Width = w;
        _activeRectVisual.Height = h;
    }

    private void FinishActiveShape()
    {
        if (_activeLineVisual != null)
        {
            double x1 = _activeLineVisual.X1;
            double y1 = _activeLineVisual.Y1;
            double x2 = _activeLineVisual.X2;
            double y2 = _activeLineVisual.Y2;
            DrawingCanvas.Children.Remove(_activeLineVisual);
            if (System.Math.Abs(x2 - x1) < 1 && System.Math.Abs(y2 - y1) < 1)
            {
                _activeLineVisual = null;
                return;
            }
            var visual = new Line
            {
                Stroke = _activeLineVisual.Stroke,
                StrokeThickness = _activeLineVisual.StrokeThickness,
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeStartLineCap = _activeLineVisual.StrokeStartLineCap,
                StrokeEndLineCap = _activeLineVisual.StrokeEndLineCap,
                StrokeLineJoin = _activeLineVisual.StrokeLineJoin,
            };
            var element = new LineElement
            {
                Visual = visual,
                Start = new Point(x1, y1),
                End = new Point(x2, y2),
                Thickness = _activeLineVisual.StrokeThickness,
            };
            _drawing.Add(element);
            _activeLineVisual = null;
            return;
        }

        if (_activeRectVisual == null) return;
        double x = Canvas.GetLeft(_activeRectVisual);
        double y = Canvas.GetTop(_activeRectVisual);
        double w = _activeRectVisual.Width;
        double h = _activeRectVisual.Height;
        DrawingCanvas.Children.Remove(_activeRectVisual);
        if (w < 2 || h < 2) { _activeRectVisual = null; return; }

        if (_activeRectVisual is Ellipse)
        {
            var visual = new Ellipse
            {
                Stroke = _activeRectVisual.Stroke,
                StrokeThickness = _activeRectVisual.StrokeThickness,
                Width = w,
                Height = h,
            };
            Canvas.SetLeft(visual, x);
            Canvas.SetTop(visual, y);
            var element = new EllipseElement
            {
                Visual = visual,
                Bounds = new Rect(x, y, w, h),
                Thickness = _activeRectVisual.StrokeThickness,
            };
            _drawing.Add(element);
        }
        else
        {
            var visual = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Stroke = _activeRectVisual.Stroke,
                StrokeThickness = _activeRectVisual.StrokeThickness,
                Width = w,
                Height = h,
            };
            Canvas.SetLeft(visual, x);
            Canvas.SetTop(visual, y);
            var element = new RectangleElement
            {
                Visual = visual,
                Bounds = new Rect(x, y, w, h),
                Thickness = _activeRectVisual.StrokeThickness,
            };
            _drawing.Add(element);
        }
        _activeRectVisual = null;
    }

    // Padding inside the active TextBox. Extracted so StartTextEntry and
    // CommitText agree on the visual offset between the box and the glyph.
    private static readonly Thickness TextEntryPadding = new(4, 2, 4, 2);

    // Drag handle that sits above the active TextBox so the user can move
    // the in-progress text around the screen before committing it. Tracked
    // so CancelText / CommitText can find and remove it.
    private Border? _activeDragHandle;
    private bool _draggingActiveText;
    private Point _dragStartPointer;
    private double _dragStartTbLeft;
    private double _dragStartTbTop;

    private void StartTextEntry(Point pos)
    {
        // Commit any prior entry before opening a new one.
        if (_activeTextBox != null) CommitText();

        var family = new Microsoft.UI.Xaml.Media.FontFamily(_drawing.Settings.TextFont);
        var (glyphW, glyphH) = MeasureGlyph("M", _drawing.Settings.TextSize, family);

        var tb = new TextBox
        {
            MinWidth = 80,
            AcceptsReturn = false,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Foreground = new SolidColorBrush(_drawing.Settings.Color),
            BorderBrush = new SolidColorBrush(_drawing.Settings.Color),
            BorderThickness = new Thickness(1),
            FontFamily = family,
            FontSize = _drawing.Settings.TextSize,
            Padding = TextEntryPadding,
        };
        // Offset so the first glyph's optical center sits on the click point
        // instead of the TextBox top-left corner. Adjusts again on the first
        // keystroke once the actual typed character is known.
        double tbLeft = pos.X - TextEntryPadding.Left - glyphW / 2;
        double tbTop  = pos.Y - TextEntryPadding.Top  - glyphH / 2;
        Canvas.SetLeft(tb, tbLeft);
        Canvas.SetTop(tb,  tbTop);
        DrawingCanvas.Children.Add(tb);
        _activeTextBox = tb;
        _activeTextAnchor = pos;
        _activeTextAnchorApplied = false;
        tb.LostFocus += (_, _) =>
        {
            // Don't commit while the user is dragging the handle — focus moves
            // off the textbox during drag.
            if (_draggingActiveText) return;
            CommitText();
        };
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter) { ke.Handled = true; CommitText(); }
            else if (ke.Key == VirtualKey.Escape) { ke.Handled = true; CancelText(); }
        };
        tb.TextChanged += OnActiveTextBoxTextChanged;
        // Eat pointer events so RootGrid handlers don't re-trigger StartToolPress
        // when the user clicks inside the active text box.
        tb.PointerPressed += (_, ev) => ev.Handled = true;
        tb.PointerReleased += (_, ev) => ev.Handled = true;
        DrawingCanvas.IsHitTestVisible = true;

        // Drag handle: small pill above the textbox. Click-drag moves the
        // textbox to a new screen position. Neutral dark grey + Fluent Move
        // glyph so it doesn't read as a close/danger button.
        var handle = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x2E, 0x2E, 0x32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x60, 0x66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new FontIcon
            {
                Glyph = "", // Move (Segoe Fluent Icons)
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        ToolTipService.SetToolTip(handle, "Drag to reposition");
        Canvas.SetLeft(handle, tbLeft);
        Canvas.SetTop(handle, tbTop - 18); // sit just above the box
        DrawingCanvas.Children.Add(handle);
        _activeDragHandle = handle;

        handle.PointerPressed += OnDragHandlePressed;
        handle.PointerMoved   += OnDragHandleMoved;
        handle.PointerReleased += OnDragHandleReleased;
        handle.PointerCaptureLost += (_, _) => _draggingActiveText = false;

        tb.Focus(FocusState.Programmatic);
    }

    private void OnDragHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_activeTextBox == null || sender is not UIElement el) return;
        _draggingActiveText = true;
        _dragStartPointer = e.GetCurrentPoint(DrawingCanvas).Position;
        _dragStartTbLeft = Canvas.GetLeft(_activeTextBox);
        _dragStartTbTop  = Canvas.GetTop(_activeTextBox);
        el.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingActiveText || _activeTextBox == null || _activeDragHandle == null) return;
        var p = e.GetCurrentPoint(DrawingCanvas).Position;
        double dx = p.X - _dragStartPointer.X;
        double dy = p.Y - _dragStartPointer.Y;
        double newLeft = _dragStartTbLeft + dx;
        double newTop  = _dragStartTbTop  + dy;
        Canvas.SetLeft(_activeTextBox, newLeft);
        Canvas.SetTop(_activeTextBox,  newTop);
        Canvas.SetLeft(_activeDragHandle, newLeft);
        Canvas.SetTop(_activeDragHandle, newTop - 18);
        // Move the anchor too so future re-centering on first keystroke
        // (if it hasn't fired yet) stays consistent with the new position.
        _activeTextAnchor = new Point(
            _activeTextAnchor.X + dx,
            _activeTextAnchor.Y + dy);
        _dragStartPointer = p;
        _dragStartTbLeft = newLeft;
        _dragStartTbTop  = newTop;
        e.Handled = true;
    }

    private void OnDragHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.ReleasePointerCaptures();
        _draggingActiveText = false;
        // Return focus to the textbox so the user can keep typing without
        // an extra click.
        _activeTextBox?.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private Point _activeTextAnchor;
    private bool  _activeTextAnchorApplied;

    private void OnActiveTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_activeTextBox == null || _activeTextAnchorApplied) return;
        var text = _activeTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        // Re-measure with the actual first character so off-width glyphs
        // (e.g. "i" vs "M") still end up centered on the click point.
        var family = _activeTextBox.FontFamily;
        var (gw, gh) = MeasureGlyph(text[0].ToString(), _activeTextBox.FontSize, family);
        double newLeft = _activeTextAnchor.X - TextEntryPadding.Left - gw / 2;
        double newTop  = _activeTextAnchor.Y - TextEntryPadding.Top  - gh / 2;
        Canvas.SetLeft(_activeTextBox, newLeft);
        Canvas.SetTop(_activeTextBox,  newTop);
        // Drag handle was anchored to the pre-recenter position, so it would
        // visibly jump apart from the box on the first keystroke. Move it
        // with the box.
        if (_activeDragHandle != null)
        {
            Canvas.SetLeft(_activeDragHandle, newLeft);
            Canvas.SetTop(_activeDragHandle, newTop - 18);
        }
        _activeTextAnchorApplied = true;
    }

    private static (double w, double h) MeasureGlyph(string ch, double size, Microsoft.UI.Xaml.Media.FontFamily family)
    {
        var probe = new TextBlock
        {
            Text = ch,
            FontSize = size,
            FontFamily = family,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return (probe.DesiredSize.Width, probe.DesiredSize.Height);
    }

    private void CommitText()
    {
        if (_activeTextBox == null) return;
        var text = _activeTextBox.Text ?? string.Empty;
        // Preserve the TextBox's internal padding so the committed TextBlock
        // glyph sits at the same on-screen position as it did during entry.
        double x = Canvas.GetLeft(_activeTextBox) + TextEntryPadding.Left;
        double y = Canvas.GetTop(_activeTextBox)  + TextEntryPadding.Top;
        var owning = _activeTextBox;
        var family = owning.FontFamily;
        owning.TextChanged -= OnActiveTextBoxTextChanged;
        _activeTextBox = null;
        DrawingCanvas.Children.Remove(owning);
        if (_activeDragHandle != null)
        {
            DrawingCanvas.Children.Remove(_activeDragHandle);
            _activeDragHandle = null;
        }
        DrawingCanvas.IsHitTestVisible = false;
        if (string.IsNullOrWhiteSpace(text)) return;
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = family,
            FontSize = _drawing.Settings.TextSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_drawing.Settings.Color),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = tb.DesiredSize;
        var element = new TextElement
        {
            Visual = tb,
            Position = new Point(x, y),
            Text = text,
            FontSize = _drawing.Settings.TextSize,
            MeasuredSize = size,
        };
        _drawing.Add(element);
    }

    private void CancelText()
    {
        if (_activeTextBox == null) return;
        DrawingCanvas.Children.Remove(_activeTextBox);
        _activeTextBox = null;
        if (_activeDragHandle != null)
        {
            DrawingCanvas.Children.Remove(_activeDragHandle);
            _activeDragHandle = null;
        }
        DrawingCanvas.IsHitTestVisible = false;
    }

    private void TryEraseAt(Point rootPos)
    {
        // Partial-erase pencil strokes (drop the points inside the eraser
        // disc, keep the surrounding sub-strokes). Rectangles and text are
        // removed whole on touch since they are not point-sampled.
        // Shift + RMB removes whole strokes too, matching the recording overlay.
        double r = System.Math.Max(EraserRadius, _drawing.Settings.PencilThickness * 1.5);
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (shift) _drawing.WholeStrokeErase(rootPos, r);
        else _drawing.PartialErase(rootPos, r);
    }

    // ---------- Keyboard ----------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        if (_activeTextBox != null) return; // typing in textbox; handled by it

        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                HandleEscape();
                return;
            case VirtualKey.A when ctrl:
                e.Handled = true;
                SelectAll();
                return;
            case VirtualKey.Z when ctrl:
                e.Handled = true;
                _drawing.Undo();
                return;
            case VirtualKey.Y when ctrl:
                e.Handled = true;
                _drawing.Redo();
                return;
            case VirtualKey.S when ctrl:
                if (_inOcrMode) return; // do not steal save during OCR
                e.Handled = true;
                _ = SaveSilentAsync();
                return;
            case VirtualKey.C when ctrl:
                e.Handled = true;
                if (_inOcrMode) { _ = CopyOcrTextAsync(); return; }
                _ = CopyAsync();
                return;
        }
    }

    private void HandleEscape()
    {
        if (_eyedropperActive)
        {
            ExitEyedropperMode();
            return;
        }
        if (_inOcrMode)
        {
            ExitOcrMode();
            return;
        }
        if (_drawing.Settings.Tool != ToolKind.None)
        {
            SetTool(ToolKind.None);
            return;
        }
        Close();
    }

    private static bool IsCtrlDown()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void SelectAll()
    {
        var rect = new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
        SetSelection(rect);
    }

    private void SetSelection(Rect rect)
    {
        _selectionRect = rect;
        _hasSelection = true;
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
        ShowToolbars();
    }

    // ---------- Toolbar / tool selection ----------

    private void OnToolToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        ToolKind tool = tb.Name switch
        {
            "PencilBtn" => ToolKind.Pencil,
            "Rectangle" => ToolKind.Rectangle,
            "EllipseBtn" => ToolKind.Ellipse,
            "LineBtn" => ToolKind.Line,
            "TextBtn" => ToolKind.Text,
            _ => ToolKind.None,
        };
        SetTool(tb.IsChecked == true ? tool : ToolKind.None);
    }

    private void SetTool(ToolKind tool)
    {
        _drawing.Settings.Tool = tool;

        // Swap the Style instead of mutating brushes inline. The Selected
        // style ships its own template + visual states with amber stops so
        // PointerOver doesn't drop us back to grey.
        var selectedStyle = (Microsoft.UI.Xaml.Style)Application.Current.Resources["ClipsyIconButtonSelected"];
        var normalStyle   = (Microsoft.UI.Xaml.Style)Application.Current.Resources["ClipsyIconButton"];

        PencilBtn.Style = tool == ToolKind.Pencil ? selectedStyle : normalStyle;
        TextBtn.Style   = tool == ToolKind.Text   ? selectedStyle : normalStyle;
        ShapesBtn.Style = tool is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line
            ? selectedStyle : normalStyle;

        // The Shapes icon glyphs are Stroke-based shapes (not FontIcon
        // glyphs that inherit Foreground), so swap their stroke explicitly.
        var shapesActive = tool is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line;
        var shapesStroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            shapesActive ? "ClipsyAccentBrush" : "ClipsyText2Brush"];
        if (ShapeIconRect    != null) ShapeIconRect.Stroke    = shapesStroke;
        if (ShapeIconEllipse != null) ShapeIconEllipse.Stroke = shapesStroke;
        if (ShapeIconLine    != null) ShapeIconLine.Stroke    = shapesStroke;

        // Show/hide shapes in flyout - selected shape is hidden, others visible
        // Use _currentShapeTool to determine which shape is currently selected
        RectBtn.Visibility = _currentShapeTool == ToolKind.Rectangle ? Visibility.Collapsed : Visibility.Visible;
        EllipseBtn.Visibility = _currentShapeTool == ToolKind.Ellipse ? Visibility.Collapsed : Visibility.Visible;
        LineBtn.Visibility = _currentShapeTool == ToolKind.Line ? Visibility.Collapsed : Visibility.Visible;

        // Update shapes icon based on current shape tool
        if (_currentShapeTool == ToolKind.Rectangle)
        {
            ShapeIconRect.Visibility = Visibility.Visible;
            ShapeIconEllipse.Visibility = Visibility.Collapsed;
            ShapeIconLine.Visibility = Visibility.Collapsed;
        }
        else if (_currentShapeTool == ToolKind.Ellipse)
        {
            ShapeIconRect.Visibility = Visibility.Collapsed;
            ShapeIconEllipse.Visibility = Visibility.Visible;
            ShapeIconLine.Visibility = Visibility.Collapsed;
        }
        else if (_currentShapeTool == ToolKind.Line)
        {
            ShapeIconRect.Visibility = Visibility.Collapsed;
            ShapeIconEllipse.Visibility = Visibility.Collapsed;
            ShapeIconLine.Visibility = Visibility.Visible;
        }
        else
        {
            ShapeIconRect.Visibility = Visibility.Visible;
            ShapeIconEllipse.Visibility = Visibility.Collapsed;
            ShapeIconLine.Visibility = Visibility.Collapsed;
        }

        if (tool is ToolKind.Pencil or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line)
        {
            UpdatePreviewForThickness(_drawing.Settings.BrushSize);
            if (_textPreview != null) _textPreview.Visibility = Visibility.Collapsed;
        }
        else
        {
            _pencilPreview.Visibility = Visibility.Collapsed;
            // Text preview's per-frame visibility is set in PointerMoved; collapse
            // it explicitly when switching to a non-text tool so it doesn't linger.
            if (_textPreview != null && tool != ToolKind.Text)
                _textPreview.Visibility = Visibility.Collapsed;
        }
    }


    // Cached swatch brush — mutating its Color avoids per-tick allocation.
    private SolidColorBrush? _swatchBrush;
    private Color _colorBeforeFlyout;

    private void OnColorFlyoutOpened(object sender, object e)
    {
        // Snapshot current color so Cancel can revert.
        _colorBeforeFlyout = _drawing.Settings.Color;
        ColorPickerCtl.Color = _colorBeforeFlyout;
        EnsureSwatchBrush().Color = _colorBeforeFlyout;
    }

    private void OnColorPickerChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        // Live preview only — defer writing to _drawing.Settings.Color until
        // Confirm. Mutating the cached brush avoids GC churn that drove the
        // visible drag lag.
        var c = Color.FromArgb(0xFF, args.NewColor.R, args.NewColor.G, args.NewColor.B);
        EnsureSwatchBrush().Color = c;
    }

    private void OnColorConfirmClick(object sender, RoutedEventArgs e)
    {
        var c = ColorPickerCtl.Color;
        _drawing.Settings.Color = Color.FromArgb(0xFF, c.R, c.G, c.B);
        ColorFlyout?.Hide();
    }

    private void OnColorCancelClick(object sender, RoutedEventArgs e)
    {
        // Revert swatch to original color; do not touch _drawing.Settings.Color.
        EnsureSwatchBrush().Color = _colorBeforeFlyout;
        ColorPickerCtl.Color = _colorBeforeFlyout;
        ColorFlyout?.Hide();
    }

    private SolidColorBrush EnsureSwatchBrush()
    {
        if (_swatchBrush == null)
        {
            _swatchBrush = new SolidColorBrush(_drawing.Settings.Color);
            ColorSwatch.Fill = _swatchBrush;
        }
        return _swatchBrush;
    }

    // ──────────────────────────────────────────────────────────────────
    // Eyedropper
    // ──────────────────────────────────────────────────────────────────

    private bool _eyedropperActive;
    private System.Drawing.Bitmap? _eyedropperBitmap;
    private const double MagZoom = 10.0;
    private const double MagHalf = 64.0; // half of 128px magnifier

    private void OnEyedropperBtnClick(object sender, RoutedEventArgs e)
    {
        ColorFlyout?.Hide();
        EnsureEyedropperBitmap();
        if (_eyedropperBitmap == null) return;

        // Share the FrozenImage's BitmapImage as the magnifier source so we
        // don't decode the PNG twice.
        MagImage.Source = FrozenImage.Source;

        _eyedropperActive = true;
        EyedropperMagnifier.Visibility = Visibility.Visible;
        RootGrid.Focus(FocusState.Programmatic);

        // Position magnifier at current cursor immediately.
        try
        {
            GetCursorPos(out var pt);
            var scale = DpiScale;
            var dip = new Point((pt.X - _frame.VirtualBounds.X) / scale,
                                (pt.Y - _frame.VirtualBounds.Y) / scale);
            UpdateMagnifier(dip);
        }
        catch { /* no-op */ }
    }

    private void ExitEyedropperMode()
    {
        _eyedropperActive = false;
        EyedropperMagnifier.Visibility = Visibility.Collapsed;
    }

    private void EnsureEyedropperBitmap()
    {
        if (_eyedropperBitmap != null) return;
        try
        {
            using var ms = new MemoryStream(_frame.PngBytes);
            _eyedropperBitmap = new System.Drawing.Bitmap(ms);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Eyedropper bitmap decode", ex);
        }
    }

    private void UpdateMagnifier(Point cursorDip)
    {
        // Place magnifier near cursor, offset to avoid covering target pixel.
        double offset = 24;
        double x = cursorDip.X + offset;
        double y = cursorDip.Y + offset;
        double w = RootGrid.Width > 0 ? RootGrid.Width : RootGrid.ActualWidth;
        double h = RootGrid.Height > 0 ? RootGrid.Height : RootGrid.ActualHeight;
        if (x + 128 > w) x = cursorDip.X - 128 - offset;
        if (y + 128 > h) y = cursorDip.Y - 128 - offset;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        EyedropperMagnifier.Margin = new Thickness(x, y, 0, 0);

        // Translate so the source pixel under the cursor lands at the magnifier centre.
        MagTranslate.X = MagHalf - cursorDip.X * MagZoom;
        MagTranslate.Y = MagHalf - cursorDip.Y * MagZoom;
    }

    private Color SamplePixel(Point cursorDip)
    {
        if (_eyedropperBitmap == null) return Microsoft.UI.Colors.Black;
        var scale = DpiScale;
        int px = (int)(cursorDip.X * scale);
        int py = (int)(cursorDip.Y * scale);
        if (px < 0) px = 0;
        if (py < 0) py = 0;
        if (px >= _eyedropperBitmap.Width)  px = _eyedropperBitmap.Width  - 1;
        if (py >= _eyedropperBitmap.Height) py = _eyedropperBitmap.Height - 1;
        var c = _eyedropperBitmap.GetPixel(px, py);
        return Color.FromArgb(0xFF, c.R, c.G, c.B);
    }

    private void ApplyPickedColor(Color c)
    {
        _drawing.Settings.Color = c;
        ColorPickerCtl.Color = c;
        EnsureSwatchBrush().Color = c;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private static Color ParseHexColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 8)
        {
            byte a = System.Convert.ToByte(s.Substring(0, 2), 16);
            byte r = System.Convert.ToByte(s.Substring(2, 2), 16);
            byte g = System.Convert.ToByte(s.Substring(4, 2), 16);
            byte b = System.Convert.ToByte(s.Substring(6, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }
        if (s.Length == 6)
        {
            byte r = System.Convert.ToByte(s.Substring(0, 2), 16);
            byte g = System.Convert.ToByte(s.Substring(2, 2), 16);
            byte b = System.Convert.ToByte(s.Substring(4, 2), 16);
            return Color.FromArgb(0xFF, r, g, b);
        }
        return Microsoft.UI.Colors.Red;
    }

    // ---------- Bottom toolbar actions ----------

    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (!_hasSelection) return;
        var scale = DpiScale;
        var b = _frame.VirtualBounds;
        int x = b.X + (int)System.Math.Round(_selectionRect.X * scale);
        int y = b.Y + (int)System.Math.Round(_selectionRect.Y * scale);
        int w = (int)System.Math.Round(_selectionRect.Width * scale);
        int h = (int)System.Math.Round(_selectionRect.Height * scale);
        if (w < 8 || h < 8) return;
        var dq = App.Current.HostWindow!.DispatcherQueue;
        Close();
        await Task.Delay(150);
        dq.TryEnqueue(() => RecordingController.TryStart(x, y, w, h));
    }
    private void OnScreenshotClick(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnCopyClick(object sender, RoutedEventArgs e) => _ = CopyAsync();
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
    private async void OnOcrClick(object sender, RoutedEventArgs e) => await EnterOcrModeAsync();

    // ---------- Screenshot save / copy ----------

    private double DpiScale => Content?.XamlRoot?.RasterizationScale ?? 1.0;

    private async Task SaveSilentAsync()
    {
        if (!_hasSelection) return;
        try
        {
            var settings = SettingsService.Instance;
            var fmt = ScreenshotRenderer.ParseFormat(settings.Settings.ScreenshotFormat);
            var ext = ScreenshotRenderer.ExtensionFor(fmt);
            var folder = settings.GetEffectiveScreenshotFolder();
            Directory.CreateDirectory(folder);
            var name = SaveDialogService.MakeTimestampName("Clipsy", ext);
            var fullPath = IOPath.Combine(folder, name);
            var bytes = ScreenshotRenderer.RenderEncoded(_frame, _selectionRect, _drawing.Elements,
                DpiScale, fmt, settings.Settings.JpgQuality);
            await File.WriteAllBytesAsync(fullPath, bytes);
            NotificationService.ScreenshotSaved(name, bytes.LongLength / 1024L, fullPath);
            AfterSaveAction.Run(fullPath, settings.Settings.AfterSaveAction);
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Silent save failed: {ex.Message}");
            NotificationService.Error("ErrSaveFailed");
        }
    }

    private async Task SaveAsAsync()
    {
        if (!_hasSelection) return;
        try
        {
            var settings = SettingsService.Instance;
            var suggestedFolder = settings.GetEffectiveScreenshotFolder();
            var preferredFmt = ScreenshotRenderer.ParseFormat(settings.Settings.ScreenshotFormat);
            var preferredExt = ScreenshotRenderer.ExtensionFor(preferredFmt);
            var name = SaveDialogService.MakeTimestampName("Clipsy", preferredExt);

            var filters = new List<SaveDialogService.SaveFilter>
            {
                new("PNG image (*.png)",   "*.png"),
                new("JPEG image (*.jpg)",  "*.jpg"),
                new("WebP image (*.webp)", "*.webp"),
            };
            // Move the preferred format to the top so the dialog defaults to it.
            int preferredIdx = preferredFmt switch
            {
                ScreenshotRenderer.OutputFormat.Jpeg => 1,
                ScreenshotRenderer.OutputFormat.Webp => 2,
                _ => 0,
            };
            if (preferredIdx > 0)
            {
                var picked = filters[preferredIdx];
                filters.RemoveAt(preferredIdx);
                filters.Insert(0, picked);
            }

            var result = await SaveDialogService.PickSaveAsync(_hwnd, suggestedFolder, name, filters, preferredExt);
            if (result == null) return;

            // Figure out the format from the chosen filter; fall back to file extension.
            var chosen = filters[System.Math.Max(0, result.FilterIndex - 1)];
            var chosenExt = SaveDialogService.ExtensionFromPattern(chosen.Pattern);
            var pathExt = IOPath.GetExtension(result.Path);
            var finalExt = string.IsNullOrEmpty(pathExt) ? chosenExt : pathExt;
            var fmt = ScreenshotRenderer.ParseFormat(finalExt.TrimStart('.'));
            var finalPath = result.Path;
            if (string.IsNullOrEmpty(pathExt))
            {
                finalPath = result.Path + chosenExt;
            }

            var bytes = ScreenshotRenderer.RenderEncoded(_frame, _selectionRect, _drawing.Elements,
                DpiScale, fmt, settings.Settings.JpgQuality);
            await File.WriteAllBytesAsync(finalPath, bytes);
            NotificationService.ScreenshotSaved(IOPath.GetFileName(finalPath), bytes.LongLength / 1024L, finalPath);
            var dir = IOPath.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir))
            {
                settings.Settings.LastScreenshotFolder = dir;
                settings.Save();
            }
            AfterSaveAction.Run(finalPath, settings.Settings.AfterSaveAction);
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Save As failed: {ex.Message}");
            NotificationService.Error("ErrSaveFailed");
        }
    }

    private async Task CopyAsync()
    {
        if (!_hasSelection) return;
        try
        {
            var png = ScreenshotRenderer.RenderPng(_frame, _selectionRect, _drawing.Elements, DpiScale);
            await ClipboardService.SetImageAsync(png);
            NotificationService.CopiedToClipboard();
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Copy failed: {ex.Message}");
            NotificationService.Error("ErrCopyFailed");
        }
    }

    // ---------- Context menu ----------

    private void BuildScreenMenu()
    {
        SelectScreenMenu.Items.Clear();
        int i = 1;
        foreach (var m in _frame.Monitors)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"Screen {i}" + (m.IsPrimary ? " (primary)" : string.Empty),
                Tag = m,
            };
            item.Click += OnMenuSelectScreen;
            SelectScreenMenu.Items.Add(item);
            i++;
        }
    }

    private void UpdateContextMenuVisibility()
    {
        bool s = _hasSelection;
        var vis = s ? Visibility.Visible : Visibility.Collapsed;
        SelectionMenuSeparator.Visibility = vis;
        MenuCopy.Visibility = vis;
        MenuSave.Visibility = vis;
        MenuSaveAs.Visibility = vis;
        MenuClear.Visibility = vis;
    }

    private void OnMenuSelectScreen(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem mfi && mfi.Tag is ScreenFreezeService.MonitorInfo m)
        {
            var b = _frame.VirtualBounds;
            var rect = new Rect(m.Bounds.X - b.X, m.Bounds.Y - b.Y, m.Bounds.Width, m.Bounds.Height);
            SetSelection(rect);
        }
    }

    private void OnMenuSelectAll(object sender, RoutedEventArgs e) => SelectAll();
    private void OnMenuCopy(object sender, RoutedEventArgs e) => _ = CopyAsync();
    private void OnMenuSave(object sender, RoutedEventArgs e) => _ = SaveSilentAsync();
    private void OnMenuSaveAs(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnMenuClear(object sender, RoutedEventArgs e)
    {
        _drawing.ClearAll();
        _hasSelection = false;
        SelectionLayer.Visibility = Visibility.Collapsed;
        HideToolbars();
        UpdateDimGeometry(null);
        Hint.Visibility = Visibility.Visible;
    }
    private void OnMenuCancel(object sender, RoutedEventArgs e) => Close();

    // ---------- OCR ----------

    private async Task EnterOcrModeAsync()
    {
        if (!_hasSelection || _inOcrMode) return;
        _inOcrMode = true;
        SetTool(ToolKind.None);
        BottomToolbar.Visibility = Visibility.Collapsed;
        // Right toolbar stays visible during scan but its tools become
        // unusable so the user can't paint on top of the OCR overlay.
        SetRightToolbarEnabled(false);
        TranslatePanel.Visibility = Visibility.Collapsed;
        OcrPanelsContainer.Visibility = Visibility.Collapsed;
        ClearOcrVisuals();
        OcrStatusLabel.Visibility = Visibility.Collapsed;
        OcrLayer.Visibility = Visibility.Visible;
        OcrToolbar.Visibility = Visibility.Visible;
        SetOcrButtonsEnabled(false); // disabled until results come back
        PositionOcrToolbar();
        StartScanAnimation();

        IReadOnlyList<OcrWord> words;
        try
        {
            var png = ScreenshotRenderer.RenderPng(_frame, _selectionRect, Array.Empty<DrawElement>(), DpiScale);
            var engine = OcrEngineFactory.Resolve();
            words = await engine.RecognizeAsync(png);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] OCR failed: {ex.Message}");
            NotificationService.Error("ErrOcrFailed");
            words = Array.Empty<OcrWord>();
        }

        StopScanAnimation();
        if (!_inOcrMode) return;
        RenderOcrResults(words);
    }

    private void ExitOcrMode()
    {
        _inOcrMode = false;
        StopScanAnimation();
        OcrLayer.Visibility = Visibility.Collapsed;
        ClearOcrVisuals();
        OcrToolbar.Visibility = Visibility.Collapsed;
        TranslatePanel.Visibility = Visibility.Collapsed;
        OcrPanelsContainer.Visibility = Visibility.Collapsed;
        OcrStatusLabel.Visibility = Visibility.Collapsed;
        OcrTextBox.Text = string.Empty;
        SetRightToolbarEnabled(true);
        if (_hasSelection)
        {
            BottomToolbar.Visibility = Visibility.Visible;
            RightToolbar.Visibility = Visibility.Visible;
        }
    }

    private void SetRightToolbarEnabled(bool enabled)
    {
        ShapesBtn.IsEnabled = enabled;
        ColorBtn.IsEnabled = enabled;
        OcrBtn.IsEnabled = enabled;
        PencilBtn.IsEnabled = enabled;
        EllipseBtn.IsEnabled = enabled;
        RectBtn.IsEnabled = enabled;
        LineBtn.IsEnabled = enabled;
        TextBtn.IsEnabled = enabled;
        if (!enabled && ShapesFlyout != null)
        {
            ShapesFlyout.Visibility = Visibility.Collapsed;
        }
    }

    private void SetOcrButtonsEnabled(bool enabled)
    {
        OcrSelectAllBtn.IsEnabled = enabled;
        OcrCopyBtn.IsEnabled = enabled;
        OcrTranslateBtn.IsEnabled = enabled;
        OcrExitBtn.IsEnabled = true; // always reachable
    }

    private void StartScanAnimation()
    {
        ScanLine.Visibility = Visibility.Visible;
        ScanLine.Width = _selectionRect.Width;
        Canvas.SetLeft(ScanLine, 0);
        Canvas.SetTop(ScanLine, 0);
        _scanProgress = 0;
        _scanDir = 1.0;
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scanTimer.Tick += OnScanTick;
        _scanTimer.Start();
    }

    private void OnScanTick(object? sender, object e)
    {
        _scanProgress += 0.015 * _scanDir;
        if (_scanProgress >= 1.0) { _scanProgress = 1.0; _scanDir = -1.0; }
        else if (_scanProgress <= 0.0) { _scanProgress = 0.0; _scanDir = 1.0; }
        var t = _scanProgress;
        var eased = t * t * (3.0 - 2.0 * t);
        double maxY = System.Math.Max(0, _selectionRect.Height - ScanLine.Height);
        Canvas.SetTop(ScanLine, eased * maxY);
    }

    private void StopScanAnimation()
    {
        if (_scanTimer != null)
        {
            _scanTimer.Stop();
            _scanTimer.Tick -= OnScanTick;
            _scanTimer = null;
        }
        ScanLine.Visibility = Visibility.Collapsed;
    }

    private void ClearOcrVisuals()
    {
        foreach (var (_, box, glyph) in _ocrVisuals)
        {
            OcrLayer.Children.Remove(box);
            OcrLayer.Children.Remove(glyph);
        }
        _ocrVisuals.Clear();
        _ocrWordsRaw.Clear();
        _ocrWordsDip.Clear();
        _ocrSelected.Clear();
    }

    private void RenderOcrResults(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0)
        {
            OcrStatusLabel.Text = Strings.Get("NoTextFound");
            Canvas.SetLeft(OcrStatusLabel, System.Math.Max(8, _selectionRect.Width / 2 - 50));
            Canvas.SetTop(OcrStatusLabel, System.Math.Max(8, _selectionRect.Height / 2 - 10));
            OcrStatusLabel.Visibility = Visibility.Visible;
            _ = FadeOutLaterAsync(OcrStatusLabel, 2500);
            SetOcrButtonsEnabled(false);
            return;
        }

        // Map word bounds from source-bitmap pixels to root DIPs:
        //   root = selection origin + (pixel / dpiScale)
        var scale = DpiScale;
        var ox = _selectionRect.X;
        var oy = _selectionRect.Y;

        foreach (var w in words)
        {
            _ocrWordsRaw.Add(w);
            var b = new Rect(
                ox + w.BoundsPixels.X / scale,
                oy + w.BoundsPixels.Y / scale,
                w.BoundsPixels.Width / scale,
                w.BoundsPixels.Height / scale);
            _ocrWordsDip.Add(b);

            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = b.Width,
                Height = b.Height,
                Fill = new SolidColorBrush(Color.FromArgb(80, 0xFF, 0xEB, 0x3B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, b.X);
            Canvas.SetTop(rect, b.Y);
            OcrLayer.Children.Add(rect);

            _ocrVisuals.Add((b, rect, new TextBlock()));
        }

        // Build sorted, line-grouped text for the text panel.
        OcrTextBox.Text = BuildSortedText();
        OcrToolbar.Visibility = Visibility.Collapsed;
        OcrPanelsContainer.Visibility = Visibility.Visible;
        PositionOcrPanelsContainer();
        SetOcrButtonsEnabled(true);
    }

    private string BuildSortedText()
    {
        if (_ocrWordsDip.Count == 0) return string.Empty;
        // Estimate a typical line height to group words into rows.
        double medianH = _ocrWordsDip.Select(r => r.Height).OrderBy(h => h)
            .ElementAt(_ocrWordsDip.Count / 2);
        double lineTolerance = System.Math.Max(4, medianH * 0.55);

        // Pair indices with bounds and sort by Y, then X.
        var sorted = Enumerable.Range(0, _ocrWordsDip.Count)
            .OrderBy(i => _ocrWordsDip[i].Y)
            .ThenBy(i => _ocrWordsDip[i].X)
            .ToList();

        var sb = new StringBuilder();
        double currentLineY = double.NaN;
        var lineBuffer = new List<int>();

        void Flush()
        {
            if (lineBuffer.Count == 0) return;
            lineBuffer.Sort((a, b) => _ocrWordsDip[a].X.CompareTo(_ocrWordsDip[b].X));
            for (int k = 0; k < lineBuffer.Count; k++)
            {
                if (k > 0) sb.Append(' ');
                sb.Append(_ocrWordsRaw[lineBuffer[k]].Text);
            }
            sb.AppendLine();
            lineBuffer.Clear();
        }

        foreach (var i in sorted)
        {
            var y = _ocrWordsDip[i].Y;
            if (double.IsNaN(currentLineY)) currentLineY = y;
            if (System.Math.Abs(y - currentLineY) > lineTolerance)
            {
                Flush();
                currentLineY = y;
            }
            lineBuffer.Add(i);
        }
        Flush();
        return sb.ToString().TrimEnd();
    }

    private void PositionOcrPanelsContainer()
    {
        OcrPanelsContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = OcrPanelsContainer.DesiredSize;
        double rootW = RootGrid.ActualWidth;
        double rootH = RootGrid.ActualHeight;
        double tx = Canvas.GetLeft(OcrToolbar);
        double ty = Canvas.GetTop(OcrToolbar) + OcrToolbar.DesiredSize.Height + 8;
        if (ty + sz.Height > rootH - 8)
        {
            ty = _selectionRect.Y - sz.Height - 12;
            if (ty < 8) ty = 8;
        }
        if (tx + sz.Width > rootW - 8) tx = rootW - sz.Width - 8;
        if (tx < 8) tx = 8;
        Canvas.SetLeft(OcrPanelsContainer, tx);
        Canvas.SetTop(OcrPanelsContainer, ty);
    }

    private void UpdateOcrDragSelection(Point pos)
    {
        var dragRoot = MakeRect(_ocrDragStart, pos);
        var dragLocal = new Rect(
            dragRoot.X - _selectionRect.X,
            dragRoot.Y - _selectionRect.Y,
            dragRoot.Width,
            dragRoot.Height);
        _ocrSelected.Clear();
        for (int i = 0; i < _ocrWordsDip.Count; i++)
        {
            if (RectsIntersect(_ocrWordsDip[i], dragLocal)) _ocrSelected.Add(i);
        }
        UpdateOcrSelectionVisual();
    }

    private void FinishOcrSelection(Point pos)
    {
        var dragRoot = MakeRect(_ocrDragStart, pos);
        if (dragRoot.Width < 4 && dragRoot.Height < 4)
        {
            var local = new Point(_ocrDragStart.X - _selectionRect.X, _ocrDragStart.Y - _selectionRect.Y);
            int idx = -1;
            for (int i = 0; i < _ocrWordsDip.Count; i++)
            {
                var b = _ocrWordsDip[i];
                if (local.X >= b.X && local.X <= b.X + b.Width && local.Y >= b.Y && local.Y <= b.Y + b.Height)
                {
                    idx = i;
                    break;
                }
            }
            _ocrSelected.Clear();
            if (idx >= 0) _ocrSelected.Add(idx);
            UpdateOcrSelectionVisual();
        }
    }

    private static bool RectsIntersect(Rect a, Rect b)
    {
        return !(b.X > a.X + a.Width || b.X + b.Width < a.X || b.Y > a.Y + a.Height || b.Y + b.Height < a.Y);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_SHOWWINDOW   = 0x0040;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private void UpdateOcrSelectionVisual()
    {
        var unsel = new SolidColorBrush(Color.FromArgb(80, 0xFF, 0xEB, 0x3B));
        var sel = new SolidColorBrush(Color.FromArgb(170, 0xFF, 0xEB, 0x3B));
        for (int i = 0; i < _ocrVisuals.Count; i++)
        {
            _ocrVisuals[i].box.Fill = _ocrSelected.Contains(i) ? sel : unsel;
        }
    }

    private string GetSelectedOcrText()
    {
        if (_ocrSelected.Count == 0) return string.Empty;
        var sorted = _ocrSelected
            .OrderBy(i => _ocrWordsDip[i].Y)
            .ThenBy(i => _ocrWordsDip[i].X)
            .ToList();
        var sb = new StringBuilder();
        double lastY = double.NaN;
        double lastH = 0;
        foreach (var i in sorted)
        {
            var b = _ocrWordsDip[i];
            if (!double.IsNaN(lastY) && System.Math.Abs(b.Y - lastY) > lastH * 0.6)
            {
                sb.AppendLine();
            }
            else if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(_ocrWordsRaw[i].Text);
            lastY = b.Y;
            lastH = b.Height;
        }
        return sb.ToString();
    }

    private void PositionOcrToolbar()
    {
        OcrToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;
        double w = OcrToolbar.DesiredSize.Width;
        double h = OcrToolbar.DesiredSize.Height;
        double x = _selectionRect.X + (_selectionRect.Width - w) / 2;
        double y = _selectionRect.Y + _selectionRect.Height + 12;
        if (y + h > rootH - 8) y = _selectionRect.Y - h - 12;
        x = System.Math.Clamp(x, 8, System.Math.Max(8, rootW - w - 8));
        Canvas.SetLeft(OcrToolbar, x);
        Canvas.SetTop(OcrToolbar, y);
    }


    private void OnOcrSelectAll(object sender, RoutedEventArgs e)
    {
        OcrTextBox.Focus(FocusState.Programmatic);
        OcrTextBox.SelectAll();
    }

    private async void OnOcrCopy(object sender, RoutedEventArgs e)
    {
        await CopyOcrTextAsync();
    }

    private async Task CopyOcrTextAsync()
    {
        string text = OcrTextBox.SelectionLength > 0
            ? OcrTextBox.SelectedText
            : OcrTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] OCR copy failed: {ex.Message}");
            return;
        }
        OcrStatusLabel.Text = Strings.Get("Copied");
        Canvas.SetLeft(OcrStatusLabel, System.Math.Max(8, _selectionRect.Width / 2 - 30));
        Canvas.SetTop(OcrStatusLabel, 8);
        OcrStatusLabel.Visibility = Visibility.Visible;
        await FadeOutLaterAsync(OcrStatusLabel, 1200);
    }

    private string? _lastTranslateSource;

    private async void OnOcrTranslate(object sender, RoutedEventArgs e)
    {
        string text = OcrTextBox.SelectionLength > 0
            ? OcrTextBox.SelectedText
            : OcrTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        await DoTranslateAsync(text);
    }

    private async System.Threading.Tasks.Task DoTranslateAsync(string text)
    {
        _lastTranslateSource = text;
        TranslateTarget.Text = "...";
        TranslatePanel.Width = OcrTextPanel.ActualWidth;
        TranslatePanel.Visibility = Visibility.Visible;
        UpdateTranslateButtons();

        var cfg = SettingsService.Instance.Settings;
        string from = cfg.TranslateFrom;
        string to   = cfg.TranslateTo == "ui" ? Strings.Lang : cfg.TranslateTo;

        // MyMemory doesn't support sl=auto; fall back to heuristic detection
        if (from == "auto" && !string.Equals(cfg.TranslateService, "Google", StringComparison.OrdinalIgnoreCase))
        {
            var guessed = TranslationService.GuessLangPair(text);
            from = guessed.from;
            if (cfg.TranslateTo == "ui") to = guessed.to;
        }

        var translated = await TranslationService.TranslateAsync(text, from, to, cfg.TranslateService);
        TranslateTarget.Text = translated ?? Strings.Get("TranslateUnavailable");
    }

    private void UpdateTranslateButtons()
    {
        if (TranslateFromBtn == null || TranslateToBtn == null) return;
        var s = SettingsService.Instance.Settings;
        TranslateFromBtn.Content = LangBadge(s.TranslateFrom);
        TranslateToBtn.Content   = LangBadge(s.TranslateTo == "ui" ? Strings.Lang : s.TranslateTo);
    }

    private static string LangBadge(string code) => code.ToLowerInvariant() switch
    {
        "auto" => "AUTO",
        "ui"   => Strings.Lang.ToUpperInvariant(),
        _      => code.ToUpperInvariant()
    };

    private void OnTranslateFromBtnClick(object sender, RoutedEventArgs e)
        => ShowLangFlyout((Button)sender, isFrom: true);

    private void OnTranslateToBtnClick(object sender, RoutedEventArgs e)
        => ShowLangFlyout((Button)sender, isFrom: false);

    private void ShowLangFlyout(Button anchor, bool isFrom)
    {
        var flyout = new MenuFlyout();
        var cfg = SettingsService.Instance.Settings;
        bool google = string.Equals(cfg.TranslateService, "Google", StringComparison.OrdinalIgnoreCase);

        if (isFrom && google)
        {
            var auto = new MenuFlyoutItem { Text = Strings.Get("LangAutoDetect") };
            auto.Click += async (_, _) => await SetTranslateLangAsync("auto", isFrom);
            flyout.Items.Add(auto);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        if (!isFrom)
        {
            var ui = new MenuFlyoutItem { Text = Strings.Get("LangUiDefault") };
            ui.Click += async (_, _) => await SetTranslateLangAsync("ui", isFrom);
            flyout.Items.Add(ui);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var lang in TranslationService.LangCatalog)
        {
            var code = lang.Code;
            string label = (Strings.Lang == "ru" ? lang.Ru : lang.En) + $"  ({code.ToUpperInvariant()})";
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (_, _) => await SetTranslateLangAsync(code, isFrom);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private async System.Threading.Tasks.Task SetTranslateLangAsync(string code, bool isFrom)
    {
        var s = SettingsService.Instance.Settings;
        if (isFrom) s.TranslateFrom = code;
        else        s.TranslateTo   = code;
        SettingsService.Instance.Save();
        UpdateTranslateButtons();
        if (!string.IsNullOrEmpty(_lastTranslateSource))
            await DoTranslateAsync(_lastTranslateSource);
    }

    private void OnOcrPanelDragStart(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(OverlayLayer).Position;
        _ocrPanelDragOffset = new Point(
            pos.X - Canvas.GetLeft(OcrPanelsContainer),
            pos.Y - Canvas.GetTop(OcrPanelsContainer));
        _ocrPanelDragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnOcrPanelDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_ocrPanelDragging) return;
        var pos = e.GetCurrentPoint(OverlayLayer).Position;
        double x = pos.X - _ocrPanelDragOffset.X;
        double y = pos.Y - _ocrPanelDragOffset.Y;
        x = System.Math.Clamp(x, 0, System.Math.Max(0, RootGrid.ActualWidth - OcrPanelsContainer.ActualWidth));
        y = System.Math.Clamp(y, 0, System.Math.Max(0, RootGrid.ActualHeight - OcrPanelsContainer.ActualHeight));
        Canvas.SetLeft(OcrPanelsContainer, x);
        Canvas.SetTop(OcrPanelsContainer, y);
        e.Handled = true;
    }

    private void OnOcrPanelDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (_ocrPanelDragging)
        {
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            _ocrPanelDragging = false;
        }
        e.Handled = true;
    }

    private void OnOcrPanelDragCancel(object sender, PointerRoutedEventArgs e)
    {
        _ocrPanelDragging = false;
    }

    private void OnOcrExit(object sender, RoutedEventArgs e) => ExitOcrMode();

    private async Task FadeOutLaterAsync(UIElement el, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs);
            el.Visibility = Visibility.Collapsed;
        }
        catch { /* ignore */ }
    }
}





