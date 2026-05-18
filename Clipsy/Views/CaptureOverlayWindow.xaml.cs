using System;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using DrawingRect = System.Drawing.Rectangle;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow : Window
{
    private readonly ScreenFreezeService.FrozenFrame _frame;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    private bool _isDragging;
    private Point _dragStart;
    private Point _dragCurrent;
    private bool _hasSelection;

    public CaptureOverlayWindow(ScreenFreezeService.FrozenFrame frame)
    {
        _frame = frame;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = GetAppWindowForCurrentWindow();
        ConfigureAsOverlay();

        Activated += OnActivated;
        Closed += (_, _) => { };
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
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
        _appWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));

        FrozenImage.Width = b.Width;
        FrozenImage.Height = b.Height;
        UpdateDimGeometry(null);
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (FrozenImage.Source == null)
        {
            FrozenImage.Source = await ScreenFreezeService.ToBitmapImageAsync(_frame.PngBytes);
        }
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == VirtualKey.A && IsCtrlDown())
        {
            e.Handled = true;
            SelectAll();
        }
    }

    private static bool IsCtrlDown()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void SelectAll()
    {
        _dragStart = new Point(0, 0);
        _dragCurrent = new Point(RootGrid.ActualWidth, RootGrid.ActualHeight);
        _hasSelection = true;
        _isDragging = false;
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(RootGrid).Position;
        if (e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStart = p;
            _dragCurrent = p;
            Hint.Visibility = Visibility.Collapsed;
            RootGrid.CapturePointer(e.Pointer);
            UpdateSelectionVisual();
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _dragCurrent = e.GetCurrentPoint(RootGrid).Position;
        UpdateSelectionVisual();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        var rect = GetSelectionRect();
        if (rect.Width < 4 && rect.Height < 4)
        {
            // Single click = 100x100 minimum centered on click
            _dragStart = new Point(_dragStart.X - 50, _dragStart.Y - 50);
            _dragCurrent = new Point(_dragStart.X + 100, _dragStart.Y + 100);
        }
        _hasSelection = true;
        UpdateSelectionVisual();
    }

    private Rect GetSelectionRect()
    {
        double x = Math.Min(_dragStart.X, _dragCurrent.X);
        double y = Math.Min(_dragStart.Y, _dragCurrent.Y);
        double w = Math.Abs(_dragCurrent.X - _dragStart.X);
        double h = Math.Abs(_dragCurrent.Y - _dragStart.Y);
        return new Rect(x, y, w, h);
    }

    private void UpdateSelectionVisual()
    {
        if (!_isDragging && !_hasSelection)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            UpdateDimGeometry(null);
            return;
        }

        var r = GetSelectionRect();
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Width = Math.Max(0, r.Width);
        SelectionBorder.Height = Math.Max(0, r.Height);
        SelectionBorder.Margin = new Thickness(r.X, r.Y, 0, 0);
        UpdateDimGeometry(r);
    }

    private void UpdateDimGeometry(Rect? hole)
    {
        DimGeometry.Children.Clear();
        var outer = new Windows.Foundation.Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
        DimGeometry.Children.Add(new RectangleGeometry { Rect = outer });
        if (hole.HasValue && hole.Value.Width > 0 && hole.Value.Height > 0)
        {
            DimGeometry.Children.Add(new RectangleGeometry { Rect = hole.Value });
        }
    }
}
