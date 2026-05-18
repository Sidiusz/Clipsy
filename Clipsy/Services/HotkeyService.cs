using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Clipsy.Services;

/// <summary>
/// Global hotkey registration backed by a dedicated message-only window
/// running its own GetMessage pump on a background STA thread. We do not
/// subclass the WinUI 3 hwnd because WM_HOTKEY routing through the
/// XAML island's WndProc is unreliable and risks breaking dispatch.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_QUIT   = 0x0012;
    private const uint MOD_NONE = 0x0000;
    private const int HOTKEY_ID = 0xC1170;
    private const uint VK_SNAPSHOT = 0x2C;
    private const int HWND_MESSAGE = -3;

    private readonly DispatcherQueue _dispatcher;
    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;       // kept alive against GC
    private GCHandle _wndProcHandle;
    private Action? _callback;
    private volatile bool _running;
    private uint _threadId;

    public bool IsRegistered { get; private set; }

    /// <summary>Last Win32 error from RegisterHotKey, 0 if success or not attempted.</summary>
    public int LastRegisterError { get; private set; }

    public HotkeyService(DispatcherQueue uiDispatcher)
    {
        _dispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public bool RegisterDefault(Action callback)
    {
        _callback = callback;
        _running = true;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => MessageLoop(ready))
        {
            IsBackground = true,
            Name = "Clipsy.HotkeyPump",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
        return IsRegistered;
    }

    private void MessageLoop(ManualResetEventSlim ready)
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _wndProc = WndProc;
            _wndProcHandle = GCHandle.Alloc(_wndProc);
            string className = "ClipsyHotkeyMsgWnd_" + Guid.NewGuid().ToString("N");
            var hInstance = GetModuleHandle(null);
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance   = hInstance,
                lpszClassName = className,
            };
            ushort atom = RegisterClassW(ref wc);
            if (atom == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Clipsy] RegisterClass failed err={Marshal.GetLastWin32Error()}");
                ready.Set();
                return;
            }
            _hwnd = CreateWindowExW(0, className, "ClipsyHotkey", 0, 0, 0, 0, 0,
                new IntPtr(HWND_MESSAGE), IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine($"[Clipsy] CreateWindowEx failed err={Marshal.GetLastWin32Error()}");
                ready.Set();
                return;
            }

            if (!RegisterHotKey(_hwnd, HOTKEY_ID, MOD_NONE, VK_SNAPSHOT))
            {
                LastRegisterError = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"[Clipsy] RegisterHotKey(PrintScreen) failed err=0x{LastRegisterError:X}. Win11 Snipping Tool override likely. Disable in Settings > Accessibility > Keyboard > Use PrtScn key.");
            }
            else
            {
                IsRegistered = true;
            }
            ready.Set();

            while (_running && GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Hotkey pump crash: {ex}");
            ready.Set();
        }
        finally
        {
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    UnregisterHotKey(_hwnd, HOTKEY_ID);
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
            catch { }
            if (_wndProcHandle.IsAllocated) _wndProcHandle.Free();
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            var cb = _callback;
            if (cb != null) _dispatcher.TryEnqueue(() => cb());
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        try { _thread?.Join(500); } catch { }
    }

    // ---------- Win32 ----------

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
