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
    public event Action? RecordRequested;
    public event Action? OpenFolderRequested;
    public event Action? SettingsRequested;
    public event Action? AboutRequested;
    public event Action? ExitRequested;

    public ICommand CaptureCommand { get; }

    public MainWindow()
    {
        CaptureCommand = new RelayCommand(() => CaptureRequested?.Invoke());
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
        // LMB still triggers capture. RMB opens the WinUI ContextFlyout
        // declared in MainWindow.xaml — H.NotifyIcon shows it at the cursor.
        TrayIcon.LeftClickCommand = CaptureCommand;
        TrayIcon.ForceCreate();
    }

    private void ApplyLocalization()
    {
        TrayIcon.ToolTipText = Strings.Get("TrayTooltip");
        try
        {
            if (TrayCaptureItem    != null) TrayCaptureItem.Text    = Strings.Get("TrayCapture");
            if (TrayRecordItem     != null) TrayRecordItem.Text     = Strings.Get("TrayRecord");
            if (TrayOpenFolderItem != null) TrayOpenFolderItem.Text = Strings.Get("TrayOpenFolder");
            if (TraySettingsItem   != null) TraySettingsItem.Text   = Strings.Get("TraySettings");
            if (TrayAboutItem      != null) TrayAboutItem.Text      = Strings.Get("TrayAbout");
            if (TrayExitItem       != null) TrayExitItem.Text       = Strings.Get("TrayExit");
            if (TrayHeaderItem != null)
                TrayHeaderItem.KeyboardAcceleratorTextOverride =
                    $"v{UpdateService.CurrentVersion()} · {Strings.Get("TrayReady")}";
        }
        catch (Exception ex)
        {
            Diagnostics.Log("MainWindow.ApplyLocalization", ex);
        }
    }

    private void OnTrayCapture(object sender, RoutedEventArgs e)    => CaptureRequested?.Invoke();
    private void OnTrayRecord(object sender, RoutedEventArgs e)     => RecordRequested?.Invoke();
    private void OnTrayOpenFolder(object sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke();
    private void OnTraySettings(object sender, RoutedEventArgs e)   => SettingsRequested?.Invoke();
    private void OnTrayAbout(object sender, RoutedEventArgs e)      => AboutRequested?.Invoke();
    private void OnTrayExit(object sender, RoutedEventArgs e)       => ExitRequested?.Invoke();

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

    private void HideAsTrayHost()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var ex = GetWindowLong(Hwnd, GWL_EXSTYLE);
        SetWindowLong(Hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
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
