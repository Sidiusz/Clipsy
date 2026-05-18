using System;
using Microsoft.UI.Xaml;
using Clipsy.Services;
using Clipsy.Views;

namespace Clipsy;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public MainWindow? HostWindow { get; private set; }
    public HotkeyService? Hotkey { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Unhandled: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        HostWindow = new MainWindow();
        HostWindow.CaptureRequested += OnCaptureRequested;
        HostWindow.SettingsRequested += OnSettingsRequested;
        HostWindow.ExitRequested += OnExitRequested;

        Hotkey = new HotkeyService(HostWindow.Hwnd);
        Hotkey.RegisterDefault(OnCaptureRequested);
    }

    private void OnCaptureRequested()
    {
        CaptureOverlayHost.ShowOverlay();
    }

    private void OnSettingsRequested()
    {
        // Phase 6
    }

    private void OnExitRequested()
    {
        Hotkey?.Dispose();
        Application.Current.Exit();
    }
}
