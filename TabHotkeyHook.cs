using System.Runtime.InteropServices;

namespace SwitchboardRM;

/// <summary>
/// Ctrl+Alt+Left / Ctrl+Alt+Right to switch tabs, also while a remote desktop has the keyboard.
///
/// The Remote Desktop control reads the keyboard outside Switchboard's own message loop (on its own thread, or through
/// a keyboard hook of its own), so neither the form's key handling nor a message filter ever sees those keys. A
/// low-level keyboard hook does: Windows offers every keystroke to these hooks first, newest first. So this one is
/// re-installed whenever a remote desktop tab is chosen or gets focus, keeping it ahead of any hook the RDP control
/// adds. It only acts when Switchboard's main window is active (and not for a terminal, which handles these keys
/// itself), and it swallows only the arrow of a Ctrl+Alt+arrow; every other key passes straight through.
/// </summary>
internal sealed class TabHotkeyHook : IDisposable
{
    private readonly Func<bool> _shouldHandle;
    private readonly Action<int> _switchTab;
    private readonly HookProc _proc;   // kept in a field: Windows calls it, so it mustn't be collected
    private IntPtr _hook;
    private bool _loggedFailure;

    // modifier state as seen by this hook (an RDP hook can hide these keys from Windows' own key state)
    private bool _lCtrl, _rCtrl, _lAlt, _rAlt, _lShift, _rShift, _lWin, _rWin;
    private uint _heldArrow;   // the arrow whose key-down was used, so its key-up (and repeats) are swallowed too

    /// <param name="shouldHandle">Whether Switchboard should take Ctrl+Alt+arrow right now (called on the UI thread).</param>
    /// <param name="switchTab">-1 = previous tab, +1 = next tab (posted, not run inside the hook).</param>
    public TabHotkeyHook(Func<bool> shouldHandle, Action<int> switchTab)
    {
        _shouldHandle = shouldHandle;
        _switchTab = switchTab;
        _proc = Callback;
    }

    /// <summary>Install, or re-install so it's the newest hook again (ahead of the RDP control's). UI thread only.</summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero && !_loggedFailure)
        {
            _loggedFailure = true;
            Log.Write("Couldn't install the tab-key hook (error " + Marshal.GetLastWin32Error() + "): Ctrl+Alt+arrows won't work inside remote desktops");
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = (int)wParam;
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool up = msg is WM_KEYUP or WM_SYSKEYUP;

                switch (key.vkCode)
                {
                    case VK_LCONTROL: _lCtrl = down; break;
                    case VK_RCONTROL: _rCtrl = down; break;
                    case VK_LMENU: _lAlt = down; break;
                    case VK_RMENU: _rAlt = down; break;
                    case VK_LSHIFT: _lShift = down; break;
                    case VK_RSHIFT: _rShift = down; break;
                    case VK_LWIN: _lWin = down; break;
                    case VK_RWIN: _rWin = down; break;
                }

                if (key.vkCode is VK_LEFT or VK_RIGHT)
                {
                    if (up && _heldArrow == key.vkCode)
                    {
                        _heldArrow = 0;
                        return (IntPtr)1;   // its key-down was ours, so is its key-up
                    }
                    if (down && _heldArrow == key.vkCode) return (IntPtr)1;   // held down: one switch, not a race through the tabs

                    bool ctrl = _lCtrl || _rCtrl || Pressed(VK_CONTROL);
                    bool alt = _lAlt || _rAlt || Pressed(VK_MENU);
                    bool shift = _lShift || _rShift;
                    bool win = _lWin || _rWin;
                    if (down && ctrl && alt && !shift && !win && _shouldHandle())
                    {
                        _heldArrow = key.vkCode;
                        _switchTab(key.vkCode == VK_LEFT ? -1 : +1);
                        return (IntPtr)1;   // the remote computer (or whatever has focus) doesn't get this arrow
                    }
                }
            }
            catch { /* never let a hook problem eat keystrokes */ }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint VK_LEFT = 0x25, VK_RIGHT = 0x27;
    private const uint VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3,
                       VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_CONTROL = 0x11, VK_MENU = 0x12;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
}
