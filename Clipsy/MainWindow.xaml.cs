using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Clipsy.Localization;
using Clipsy.Services;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipsy;

public sealed partial class MainWindow : Window
{
    public IntPtr Hwnd { get; }

    public event Action? CaptureRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public ICommand CaptureCommand { get; }
    public ICommand TrayRightClickCommand { get; }

    public MainWindow()
    {
        CaptureCommand = new RelayCommand(() => CaptureRequested?.Invoke());
        TrayRightClickCommand = new RelayCommand(ShowTrayMenu);
        InitializeComponent();
        ThemeService.Register(Content as Microsoft.UI.Xaml.FrameworkElement);
        Hwnd = WindowNative.GetWindowHandle(this);
        TrySetTrayIcon();
        WireTrayCommands();
        ApplyLocalization();
        HideAsTrayHost();
    }

    public TaskbarIcon TrayIconControl => TrayIcon;

    private void WireTrayCommands()
    {
        TrayIcon.LeftClickCommand = CaptureCommand;
        TrayIcon.RightClickCommand = TrayRightClickCommand;
        TrayIcon.ForceCreate();
    }

    private void ApplyLocalization()
    {
        TrayIcon.ToolTipText = Strings.Get("TrayTooltip");
    }

    private void TrySetTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "clipsy.ico");
            if (File.Exists(path))
            {
                TrayIcon.IconSource = new BitmapImage(new Uri(path));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Tray icon load failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the tray context menu via native TrackPopupMenu so we don't
    /// depend on XAML MenuFlyout/SecondWindow re-parenting. Items go
    /// straight to the corresponding event raisers.
    /// </summary>
    private void ShowTrayMenu()
    {
        const uint MF_STRING = 0x00000000;
        const uint MF_SEPARATOR = 0x00000800;
        const uint TPM_RETURNCMD = 0x0100;
        const uint TPM_RIGHTBUTTON = 0x0002;
        const uint TPM_NONOTIFY = 0x0080;

        if (!GetCursorPos(out var pt)) return;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, 1, Strings.Get("TrayCapture"));
            AppendMenuW(menu, MF_STRING, 2, Strings.Get("TraySettings"));
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, 3, Strings.Get("TrayExit"));

            SetForegroundWindow(Hwnd);
            uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                pt.X, pt.Y, 0, Hwnd, IntPtr.Zero);
            switch (cmd)
            {
                case 1: CaptureRequested?.Invoke(); break;
                case 2: SettingsRequested?.Invoke(); break;
                case 3: ExitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
            // Quirk: WM_NULL post is the canonical way to dismiss the menu
            // properly when TrackPopupMenu returns synchronously.
            PostMessageW(Hwnd, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void HideAsTrayHost()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var ex = GetWindowLong(Hwnd, GWL_EXSTYLE);
        SetWindowLong(Hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
