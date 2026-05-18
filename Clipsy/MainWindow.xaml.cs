using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Clipsy.Localization;
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

    public MainWindow()
    {
        CaptureCommand = new RelayCommand(() => CaptureRequested?.Invoke());
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        TrySetTrayIcon();
        ApplyLocalization();
        HideAsTrayHost();
    }

    /// <summary>
    /// The host window must be a real, activated window for the WinUI 3
    /// XAML island (and therefore TaskbarIcon's commands and ContextFlyout)
    /// to come alive. AppWindow.Hide() defeats that, so instead we mark
    /// the window as a tool window (no taskbar / alt-tab entry) and shove
    /// it offscreen with a 1x1 size. The caller is expected to invoke
    /// Activate() once after construction.
    /// </summary>
    private void HideAsTrayHost()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        var ex = GetWindowLong(Hwnd, GWL_EXSTYLE);
        SetWindowLong(Hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public TaskbarIcon TrayIconControl => TrayIcon;

    private void ApplyLocalization()
    {
        TrayIcon.ToolTipText = Strings.Get("TrayTooltip");
        MenuCapture.Text = Strings.Get("TrayCapture");
        MenuSettings.Text = Strings.Get("TraySettings");
        MenuExit.Text = Strings.Get("TrayExit");
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

    private void OnCaptureClick(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnExitClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
