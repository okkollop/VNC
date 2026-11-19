using System.Runtime.InteropServices;
using Enlace.Windows;
using static Enlace.Windows.Native;

namespace Enlace.Client;

/// <summary>
/// Gancho de teclado de bajo nivel, activo sólo mientras el visor tiene el foco, para mandar al
/// equipo remoto combinaciones que Windows se quedaría: tecla Windows, Alt+Tab, Alt+Esc, Ctrl+Esc.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action<uint, bool> _send;
    private IntPtr _hook;

    public KeyboardHook(Action<uint, bool> send)
    {
        _send = send;
        _proc = Callback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool alt = (k.flags & LLKHF_ALTDOWN) != 0;
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                uint? sym = k.vkCode switch
                {
                    0x5B => KeyMap.XK_Super_L,
                    0x5C => KeyMap.XK_Super_R,
                    0x09 when alt => KeyMap.XK_Tab,
                    0x1B when alt || ctrl => KeyMap.XK_Escape,
                    _ => null,
                };
                if (sym != null)
                {
                    _send(sym.Value, down);
                    return 1; // no llega a Windows local
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
