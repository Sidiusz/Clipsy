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
/// Supports two simultaneous hotkeys: capture (primary) and record-stop (optional).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY       = 0x0312;
    private const int WM_QUIT         = 0x0012;
    private const int WM_USER_REREG   = 0x0401;
    private const uint MOD_NONE       = 0x0000;
    private const uint MOD_ALT        = 0x0001;
    private const uint MOD_CONTROL    = 0x0002;
    private const uint MOD_SHIFT      = 0x0004;
    private const int HOTKEY_CAPTURE  = 0xC1170;
    private const int HOTKEY_RECORD   = 0xC1171;
    private const int HOTKEY_MIC      = 0xC1172;
    private const int HWND_MESSAGE    = -3;

    private readonly DispatcherQueue _dispatcher;
    private Thread? _thread;
    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;
    private GCHandle _wndProcHandle;
    private volatile bool _running;
    private uint _threadId;

    private Action? _captureCallback;
    private Action? _recordCallback;
    private Action? _micCallback;

    private uint _captureVk;
    private uint _captureMods;
    private uint _recordVk;
    private uint _recordMods;
    private uint _micVk;
    private uint _micMods;

    // Low-level keyboard hook fallback. RegisterHotKey collides with
    // Win11's Snipping Tool when the user has "Use PrintScreen to open
    // Snipping" enabled, and with arbitrary third-party apps that already
    // own a binding. WH_KEYBOARD_LL intercepts the key before any other
    // hotkey handler and works in all those cases — same approach as
    // Lightshot / ShareX.
    private IntPtr _llHook;
    private LowLevelKeyboardProc? _llProc;
    private GCHandle _llProcHandle;
    private bool _captureViaLL;
    private bool _recordViaLL;
    private bool _micViaLL;

    public bool IsCaptureRegistered { get; private set; }
    public int LastRegisterError { get; private set; }

    public HotkeyService(DispatcherQueue uiDispatcher)
    {
        _dispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public bool Register(Action captureCallback, string captureBinding,
                         Action? recordCallback = null, string? recordBinding = null,
                         Action? micCallback = null, string? micBinding = null)
    {
        _captureCallback = captureCallback;
        _recordCallback  = recordCallback;
        _micCallback     = micCallback;

        ParseBinding(captureBinding, out _captureVk, out _captureMods);
        ParseBinding(recordBinding,  out _recordVk,  out _recordMods);
        ParseBinding(micBinding,     out _micVk,     out _micMods);

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
        return IsCaptureRegistered;
    }

    /// <summary>Re-register all hotkeys with new bindings without restarting the thread.</summary>
    public void Reregister(string captureBinding, string? recordBinding, string? micBinding = null)
    {
        ParseBinding(captureBinding, out _captureVk, out _captureMods);
        ParseBinding(recordBinding,  out _recordVk,  out _recordMods);
        ParseBinding(micBinding,     out _micVk,     out _micMods);
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_USER_REREG, IntPtr.Zero, IntPtr.Zero);
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
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance     = hInstance,
                lpszClassName = className,
            };
            ushort atom = RegisterClassW(ref wc);
            if (atom == 0) { ready.Set(); return; }

            _hwnd = CreateWindowExW(0, className, "ClipsyHotkey", 0, 0, 0, 0, 0,
                new IntPtr(HWND_MESSAGE), IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { ready.Set(); return; }

            DoRegister();
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
                if (_llHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_llHook);
                    _llHook = IntPtr.Zero;
                }
                if (_hwnd != IntPtr.Zero)
                {
                    UnregisterHotKey(_hwnd, HOTKEY_CAPTURE);
                    UnregisterHotKey(_hwnd, HOTKEY_RECORD);
                    UnregisterHotKey(_hwnd, HOTKEY_MIC);
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
            catch { }
            if (_llProcHandle.IsAllocated) _llProcHandle.Free();
            if (_wndProcHandle.IsAllocated) _wndProcHandle.Free();
        }
    }

    private void DoRegister()
    {
        UnregisterHotKey(_hwnd, HOTKEY_CAPTURE);
        UnregisterHotKey(_hwnd, HOTKEY_RECORD);
        UnregisterHotKey(_hwnd, HOTKEY_MIC);
        _captureViaLL = false;
        _recordViaLL  = false;
        _micViaLL     = false;
        IsCaptureRegistered = false;

        if (_captureVk != 0)
        {
            // LL hook sees PrintScreen before app-level hotkeys, so apps that
            // grab it (other tools, games, Win11 Snipping) no longer swallow it.
            _captureViaLL = true;
            IsCaptureRegistered = true;
        }

        if (_recordVk != 0 && _recordCallback != null)
        {
            if (!RegisterHotKey(_hwnd, HOTKEY_RECORD, _recordMods, _recordVk))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Clipsy] RegisterHotKey(record-stop) failed err=0x{Marshal.GetLastWin32Error():X} — falling back to LL hook");
                _recordViaLL = true;
            }
        }

        if (_micVk != 0 && _micCallback != null)
        {
            if (!RegisterHotKey(_hwnd, HOTKEY_MIC, _micMods, _micVk))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Clipsy] RegisterHotKey(mic-toggle) failed err=0x{Marshal.GetLastWin32Error():X} — falling back to LL hook");
                _micViaLL = true;
            }
        }

        SyncLowLevelHook();
    }

    private void SyncLowLevelHook()
    {
        bool need = _captureViaLL || _recordViaLL || _micViaLL;
        if (need && _llHook == IntPtr.Zero)
        {
            _llProc = LowLevelKbProc;
            _llProcHandle = GCHandle.Alloc(_llProc);
            var hMod = GetModuleHandle(null);
            _llHook = SetWindowsHookExW(WH_KEYBOARD_LL, _llProc, hMod, 0);
            if (_llHook == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Clipsy] SetWindowsHookEx(WH_KEYBOARD_LL) failed err=0x{Marshal.GetLastWin32Error():X}");
                if (_llProcHandle.IsAllocated) _llProcHandle.Free();
                _llProc = null;
            }
        }
        else if (!need && _llHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
            if (_llProcHandle.IsAllocated) _llProcHandle.Free();
            _llProc = null;
        }
    }

    private IntPtr LowLevelKbProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            int w = wParam.ToInt32();
            if (w == WM_KEYDOWN || w == WM_SYSKEYDOWN)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vk = kbd.vkCode;
                uint mods = CurrentModifiers();
                if (_captureViaLL && vk == _captureVk && mods == _captureMods)
                {
                    var cb = _captureCallback;
                    if (cb != null) _dispatcher.TryEnqueue(() => cb());
                    return new IntPtr(1); // swallow so OS shortcut doesn't also fire
                }
                if (_recordViaLL && vk == _recordVk && mods == _recordMods)
                {
                    var cb = _recordCallback;
                    if (cb != null) _dispatcher.TryEnqueue(() => cb());
                    return new IntPtr(1);
                }
                if (_micViaLL && vk == _micVk && mods == _micMods)
                {
                    var cb = _micCallback;
                    if (cb != null) _dispatcher.TryEnqueue(() => cb());
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        uint m = 0;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) m |= MOD_CONTROL;
        if ((GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0) m |= MOD_SHIFT;
        if ((GetAsyncKeyState(VK_MENU)    & 0x8000) != 0) m |= MOD_ALT;
        return m;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_CAPTURE)
            {
                var cb = _captureCallback;
                if (cb != null) _dispatcher.TryEnqueue(() => cb());
            }
            else if (id == HOTKEY_RECORD)
            {
                var cb = _recordCallback;
                if (cb != null) _dispatcher.TryEnqueue(() => cb());
            }
            else if (id == HOTKEY_MIC)
            {
                var cb = _micCallback;
                if (cb != null) _dispatcher.TryEnqueue(() => cb());
            }
            return IntPtr.Zero;
        }
        if (msg == WM_USER_REREG)
        {
            DoRegister();
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        try { _thread?.Join(500); } catch { }
    }

    // ---------- Binding parser ----------

    private static void ParseBinding(string? binding, out uint vk, out uint mods)
    {
        vk = 0; mods = 0;
        if (string.IsNullOrWhiteSpace(binding)) return;
        var parts = binding.Split('+');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "shift":                mods |= MOD_SHIFT;   break;
                case "alt":  case "menu":    mods |= MOD_ALT;     break;
            }
        }
        vk = KeyNameToVk(parts[^1].Trim());
    }

    private static uint KeyNameToVk(string name) => name.ToLowerInvariant() switch
    {
        "snapshot" or "printscreen" or "print screen" => 0x2C,
        "pause"    => 0x13,
        "escape"   => 0x1B,
        "tab"      => 0x09,
        "space"    => 0x20,
        "insert"   => 0x2D,
        "delete"   => 0x2E,
        "home"     => 0x24,
        "end"      => 0x23,
        "pageup"   => 0x21,
        "pagedown" => 0x22,
        "left"     => 0x25,
        "up"       => 0x26,
        "right"    => 0x27,
        "down"     => 0x28,
        "f1"  => 0x70, "f2"  => 0x71, "f3"  => 0x72, "f4"  => 0x73,
        "f5"  => 0x74, "f6"  => 0x75, "f7"  => 0x76, "f8"  => 0x77,
        "f9"  => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
        "number0" => 0x30, "number1" => 0x31, "number2" => 0x32,
        "number3" => 0x33, "number4" => 0x34, "number5" => 0x35,
        "number6" => 0x36, "number7" => 0x37, "number8" => 0x38,
        "number9" => 0x39,
        _ when name.Length == 1 && char.IsLetter(name[0]) => (uint)char.ToUpper(name[0]),
        _ => 0
    };

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

    // ---------- WH_KEYBOARD_LL ----------

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION      = 0;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int VK_SHIFT       = 0x10;
    private const int VK_CONTROL     = 0x11;
    private const int VK_MENU        = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
