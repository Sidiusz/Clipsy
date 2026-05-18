using System;
using System.IO;
using System.Windows.Input;
using Clipsy.Localization;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
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
        AppWindow.Hide();
    }

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
