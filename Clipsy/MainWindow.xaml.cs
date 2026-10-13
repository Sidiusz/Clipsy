using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    public event Action? SessionEnding;

    private SubclassProc? _subclassProc;
    private bool _shutdownBlocked;
    private static readonly TimeSpan SessionEndRecordingTimeout = TimeSpan.FromSeconds(20);

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
        _subclassProc = HostWndProc;
        SetWindowSubclass(Hwnd, _subclassProc, UIntPtr.Zero, IntPtr.Zero);
    }

    // Logoff/shutdown and installer Restart Manager end the process on purpose;
    // tell the watchdog so it doesn't treat it as a crash and relaunch.
    private IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        if (msg == WM_QUERYENDSESSION && RecordingController.Current != null && !_shutdownBlocked)
        {
            // Windows shows this while WM_ENDSESSION below finishes the file.
            _shutdownBlocked = ShutdownBlockReasonCreate(hWnd, Strings.Get("ShutdownSavingRecording"));
        }
        else if (msg == WM_ENDSESSION)
        {
            if (wParam != IntPtr.Zero)
            {
                FinishRecordingBeforeSessionEnd();
                try { SettingsService.Instance.Flush(); } catch (Exception ex) { Diagnostics.Log("Session end: settings flush", ex); }
                ProcessWatchdog.MarkCleanExit();
                SingleInstanceService.StopServer();
                try { SessionEnding?.Invoke(); } catch (Exception ex) { Diagnostics.Log("SessionEnding", ex); }
            }
            if (_shutdownBlocked) ShutdownBlockReasonDestroy(hWnd);
            _shutdownBlocked = false;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // The process may end as soon as WM_ENDSESSION returns, and an MP4 is unplayable until its
    // index is written at the end. Stopping and saving run on this (UI) thread, so pump messages
    // here instead of blocking it; the deadline keeps a stuck save from holding up the logoff.
    private static void FinishRecordingBeforeSessionEnd()
    {
        if (RecordingController.Current is not { } recording) return;
        Task stop;
        try { stop = recording.StopForExitAsync(SessionEndRecordingTimeout); }
        catch (Exception ex) { Diagnostics.Log("Session end: stopping recording failed", ex); return; }

        Diagnostics.Log("Session ending; finishing the recording first");
        long deadline = Environment.TickCount64 + (long)SessionEndRecordingTimeout.TotalMilliseconds;
        while (!stop.IsCompleted)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0)
            {
                Diagnostics.Log("Session end: recording did not finish in time");
                return;
            }
            MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)Math.Min(left, 100), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            while (PeekMessage(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (m.message == WM_QUIT)
                {
                    PostQuitMessage((int)m.wParam);
                    return;
                }
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }
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
            Diagnostics.Log("Tray icon load failed", ex);
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

    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_REMOVE = 0x0001;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);
    [DllImport("user32.dll")] private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
