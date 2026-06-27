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
    public event Action? MenuRequested;

    public ICommand CaptureCommand  { get; }
    public ICommand ShowMenuCommand { get; }

    public MainWindow()
    {
        CaptureCommand  = new RelayCommand(() => CaptureRequested?.Invoke());
        ShowMenuCommand = new RelayCommand(() => MenuRequested?.Invoke());
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
        TrayIcon.LeftClickCommand  = CaptureCommand;
        TrayIcon.RightClickCommand = ShowMenuCommand;
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
            // Prefer Assets\Icons\clipsy.ico; fall back to legacy Assets\clipsy.ico
            // if the new folder is missing.
            var newPath    = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "clipsy.ico");
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "Assets", "clipsy.ico");
            var path = File.Exists(newPath) ? newPath : legacyPath;
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

    private void HideAsTrayHost()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        var ex = GetWindowLong(Hwnd, GWL_EXSTYLE);
        SetWindowLong(Hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
        AppWindow.IsShownInSwitchers = false;
    }

    // ---------- Win32 ----------

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
