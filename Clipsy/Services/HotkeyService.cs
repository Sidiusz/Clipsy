using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Clipsy.Services;

/// <summary>Global hotkeys via a message-only window on a background STA pump
/// (subclassing the WinUI hwnd's WndProc for WM_HOTKEY is unreliable).</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY       = 0x0312;
    private const int WM_QUIT         = 0x0012;
    private const int WM_USER_REREG   = 0x0401;
    private const uint MOD_ALT        = 0x0001;
    private const uint MOD_CONTROL    = 0x0002;
    private const uint MOD_SHIFT      = 0x0004;
    private const uint MOD_WIN        = 0x0008;
    private const uint MOD_NOREPEAT   = 0x4000;
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

    // Low-level keyboard hook: intercepts the key before any other hotkey
    // handler, so apps that own the binding can't swallow it (ShareX-style).
    private IntPtr _llHook;
    private LowLevelKeyboardProc? _llProc;
    private GCHandle _llProcHandle;
    private bool _captureViaLL;
    private bool _recordViaLL;
    private bool _micViaLL;
    private uint _heldVk;

    public bool IsCaptureRegistered { get; private set; }

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
            // High priority so the WH_KEYBOARD_LL hook is serviced within the OS
            // hook timeout under load, else typing lags system-wide.
            Priority = ThreadPriority.Highest,
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
        // Posted to the window, not the thread: thread messages never reach WndProc.
        if (_hwnd != IntPtr.Zero)
            PostMessageW(_hwnd, WM_USER_REREG, IntPtr.Zero, IntPtr.Zero);
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
            Diagnostics.Log("Hotkey pump crash", ex);
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

        bool captureHotkey = false;
        if (_captureVk != 0)
        {
            // LL hook beats other apps' hotkeys; RegisterHotKey still fires when an
            // elevated window is focused, which UIPI hides from the hook.
            _captureViaLL = true;
            captureHotkey = RegisterHotKey(_hwnd, HOTKEY_CAPTURE, _captureMods | MOD_NOREPEAT, _captureVk);
        }

        if (_recordVk != 0 && _recordCallback != null
            && !RegisterHotKey(_hwnd, HOTKEY_RECORD, _recordMods | MOD_NOREPEAT, _recordVk))
        {
            Diagnostics.Log($"RegisterHotKey(record-stop) failed err=0x{Marshal.GetLastWin32Error():X}; using LL hook");
            _recordViaLL = true;
        }

        if (_micVk != 0 && _micCallback != null
            && !RegisterHotKey(_hwnd, HOTKEY_MIC, _micMods | MOD_NOREPEAT, _micVk))
        {
            Diagnostics.Log($"RegisterHotKey(mic-toggle) failed err=0x{Marshal.GetLastWin32Error():X}; using LL hook");
            _micViaLL = true;
        }

        SyncLowLevelHook();

        if (_captureVk != 0)
            IsCaptureRegistered = _llHook != IntPtr.Zero || captureHotkey;
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
                Diagnostics.Log($"SetWindowsHookEx(WH_KEYBOARD_LL) failed err=0x{Marshal.GetLastWin32Error():X}");
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
        try
        {
            if (nCode == HC_ACTION && HandleLowLevelKey(wParam.ToInt32(), Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode))
                return new IntPtr(1); // swallow so the OS shortcut doesn't also fire
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Hotkey LL hook", ex);
        }
        return CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private bool HandleLowLevelKey(int message, uint vk)
    {
        if (message == WM_KEYUP || message == WM_SYSKEYUP)
        {
            if (vk == _heldVk) _heldVk = 0;
            return false;
        }
        if (message != WM_KEYDOWN && message != WM_SYSKEYDOWN) return false;

        uint mods = CurrentModifiers();
        Action? cb;
        bool onUi = true;
        if (_captureViaLL && vk == _captureVk && mods == _captureMods) { cb = _captureCallback; onUi = false; }
        else if (_recordViaLL && vk == _recordVk && mods == _recordMods) cb = _recordCallback;
        else if (_micViaLL && vk == _micVk && mods == _micMods) cb = _micCallback;
        else return false;

        // Auto-repeat while held must not retrigger.
        if (_heldVk == vk) return true;
        _heldVk = vk;
        if (cb == null) return true;
        if (onUi) _dispatcher.TryEnqueue(() => cb());
        else cb(); // capture callback only signals the dedicated capture worker
        return true;
    }

    private static uint CurrentModifiers()
    {
        uint m = 0;
        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) m |= MOD_CONTROL;
        if ((GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0) m |= MOD_SHIFT;
        if ((GetAsyncKeyState(VK_MENU)    & 0x8000) != 0) m |= MOD_ALT;
        if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0) m |= MOD_WIN;
        return m;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_CAPTURE)
                {
                    _captureCallback?.Invoke();
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
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Hotkey WndProc", ex);
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
                case "win":  case "windows": mods |= MOD_WIN;     break;
            }
        }
        vk = KeyNameToVk(parts[^1].Trim());
    }

    public static bool IsValidBinding(string? binding)
    {
        ParseBinding(binding, out uint vk, out _);
        return vk != 0;
    }

    public static bool MatchesBinding(string? binding, Windows.System.VirtualKey key)
    {
        ParseBinding(binding, out uint vk, out uint mods);
        return vk != 0 && vk == (uint)key && mods == CurrentModifiers();
    }

    internal static uint KeyNameToVk(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        switch (name.ToLowerInvariant())
        {
            case "printscreen": case "print screen": case "prtsc": return 0x2C;
            case "esc": return 0x1B;
            case "del": return 0x2E;
            case "ins": return 0x2D;
            case "pgup": return 0x21;
            case "pgdn": return 0x22;
        }
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        // Settings stores VirtualKey.ToString(); keys without a name (OEM) are stored as numbers.
        if (Enum.TryParse<Windows.System.VirtualKey>(name, ignoreCase: true, out var key))
        {
            uint v = (uint)key;
            if (v is > 0 and < 0xFF && !IsModifierVk(v)) return v;
        }
        return 0;
    }

    private static bool IsModifierVk(uint vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

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
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int VK_SHIFT       = 0x10;
    private const int VK_CONTROL     = 0x11;
    private const int VK_MENU        = 0x12;
    private const int VK_LWIN        = 0x5B;
    private const int VK_RWIN        = 0x5C;

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
