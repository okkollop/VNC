using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Enlace.Protocol;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Enlace.Windows;
using static Enlace.Windows.Native;

namespace Enlace.Server;

/// <summary>Escritorio de Windows: captura GDI de todas las pantallas e inyección con SendInput.</summary>
internal sealed class WindowsDesktop : IDesktop, IDisposable
{
    private readonly object _capLock = new();
    private Bitmap? _bmp;
    private int[] _frame = Array.Empty<int>();
    private int _w, _h;
    private long _lastCapture = -1000;
    private Rectangle _vs;

    private readonly object _inLock = new();

    /// <summary>Modo servicio: seguir al escritorio activo (inicio de sesión, bloqueo, UAC).</summary>
    public bool AllowDesktopSwitch { get; init; }

    /// <summary>Se dispara al recibir Ctrl+Alt+Supr (el agente lo convierte en la señal real de Windows).</summary>
    public event Action? SecureAttention;

    [ThreadStatic] private static IntPtr _threadDesk;
    [ThreadStatic] private static string? _threadDeskName;

    /// <summary>Cambia el hilo actual al escritorio que recibe la entrada si ha cambiado (p. ej. de Default a Winlogon).</summary>
    private void FollowInputDesktop()
    {
        if (!AllowDesktopSwitch) return;
        var h = DesktopApi.OpenInputDesktop(0, false, DesktopApi.GENERIC_ALL);
        if (h == IntPtr.Zero) return;
        var name = DesktopApi.NameOf(h);
        if (name == _threadDeskName) { DesktopApi.CloseDesktop(h); return; }
        if (DesktopApi.SetThreadDesktop(h))
        {
            if (_threadDesk != IntPtr.Zero) DesktopApi.CloseDesktop(_threadDesk);
            _threadDesk = h;
            _threadDeskName = name;
            lock (_capLock) _lastCapture = -1000; // forzar captura nueva
        }
        else DesktopApi.CloseDesktop(h);
    }
    private int _lastMask;
    private readonly Dictionary<uint, (ushort Vk, bool Ext, string? Text)> _pressed = new();

    public bool CaptureInto(ref int[] buffer, out int width, out int height)
    {
        FollowInputDesktop();
        lock (_capLock)
        {
            long now = Environment.TickCount64;
            if (now - _lastCapture >= 25 || _frame.Length == 0)
            {
                _lastCapture = now;
                try { Capture(); }
                catch (Win32Exception) { } // escritorio seguro (UAC/bloqueo): se mantiene el último fotograma
                catch (ExternalException) { }
            }
            width = _w;
            height = _h;
            if (_frame.Length == 0) return false;
            if (buffer.Length != _frame.Length) buffer = new int[_frame.Length];
            Array.Copy(_frame, buffer, _frame.Length);
            return true;
        }
    }

    private void Capture()
    {
        var vs = SystemInformation.VirtualScreen;
        if (_bmp == null || _bmp.Width != vs.Width || _bmp.Height != vs.Height)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
        }
        _vs = vs;

        using (var g = Graphics.FromImage(_bmp))
        {
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(g, vs);
        }

        var data = _bmp.LockBits(new Rectangle(0, 0, vs.Width, vs.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int n = vs.Width * vs.Height;
            if (_frame.Length != n) _frame = new int[n];
            if (data.Stride == vs.Width * 4) Marshal.Copy(data.Scan0, _frame, 0, n);
            else
                for (int y = 0; y < vs.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, _frame, y * vs.Width, vs.Width);
        }
        finally
        {
            _bmp.UnlockBits(data);
        }
        _w = vs.Width;
        _h = vs.Height;
    }

    private static void DrawCursor(Graphics g, Rectangle vs)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot;
            hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, ci.ptScreenPos.X - vs.Left - hx, ci.ptScreenPos.Y - vs.Top - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
    }

    // ---------------- Ratón ----------------

    public void Pointer(int x, int y, int mask)
    {
        FollowInputDesktop();
        var vs = SystemInformation.VirtualScreen;
        int ax = vs.Width > 1 ? (int)((long)x * 65535 / (vs.Width - 1)) : 0;
        int ay = vs.Height > 1 ? (int)((long)y * 65535 / (vs.Height - 1)) : 0;

        var list = new List<INPUT>(4) { Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay, 0) };
        int prev;
        lock (_inLock) { prev = _lastMask; _lastMask = mask; }
        int changed = prev ^ mask;
        void Btn(int bit, uint down, uint up)
        {
            if ((changed & bit) != 0) list.Add(Mouse(((mask & bit) != 0 ? down : up) | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay, 0));
        }
        Btn(1, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
        Btn(2, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP);
        Btn(4, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
        // Rueda: botones 4/5 (vertical) y 6/7 (horizontal), al pulsarse
        int pressed = mask & ~prev;
        if ((pressed & 8) != 0) list.Add(Mouse(MOUSEEVENTF_WHEEL, 0, 0, 120));
        if ((pressed & 16) != 0) list.Add(Mouse(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)-120)));
        if ((pressed & 32) != 0) list.Add(Mouse(MOUSEEVENTF_HWHEEL, 0, 0, unchecked((uint)-120)));
        if ((pressed & 64) != 0) list.Add(Mouse(MOUSEEVENTF_HWHEEL, 0, 0, 120));
        Send(list);
    }

    private static INPUT Mouse(uint flags, int x, int y, uint data) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = x, dy = y, dwFlags = flags, mouseData = data } },
    };

    // ---------------- Teclado ----------------

    private static INPUT KeyVk(ushort vk, bool ext, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKeyW(vk, 0),
                dwFlags = (ext ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0),
            },
        },
    };

    private static INPUT KeyUnicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Key(uint keysym, bool down)
    {
        FollowInputDesktop();
        // Ctrl+Alt+Supr: Windows no deja simularlo con teclas; se pide la secuencia segura al sistema
        if (down && keysym is 0xFFFF or 0xFF9F && Down(0x11) && Down(0x12) && SecureAttention != null)
        {
            SecureAttention();
            return;
        }
        lock (_inLock)
        {
            if (!down)
            {
                if (!_pressed.Remove(keysym, out var p))
                {
                    // Suelta de una tecla especial que no registramos (p. ej. tras reconectar)
                    if (KeyMap.SpecialToVk.TryGetValue(keysym, out var sp)) Send(new() { KeyVk(sp.Vk, sp.Ext, true) });
                    return;
                }
                if (p.Text != null) Send(p.Text.Select(c => KeyUnicode(c, true)).ToList());
                else Send(new() { KeyVk(p.Vk, p.Ext, true) });
                return;
            }

            if (KeyMap.SpecialToVk.TryGetValue(keysym, out var s))
            {
                _pressed[keysym] = (s.Vk, s.Ext, null);
                Send(new() { KeyVk(s.Vk, s.Ext, false) });
                return;
            }

            var text = KeyMap.KeysymToText(keysym);
            if (text == null) return;

            if (text.Length == 1)
            {
                // Distribución de teclado de la ventana activa
                var hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));
                short r = VkKeyScanExW(text[0], hkl);
                if (r != -1)
                {
                    ushort vk = (ushort)(r & 0xFF);
                    int mods = (r >> 8) & 0xFF; // 1 Mayús, 2 Ctrl, 4 Alt
                    bool shift = Down(0x10), ctrl = Down(0x11), alt = Down(0x12);
                    bool altGrHeld = ctrl && alt;
                    bool needShift = (mods & 1) != 0, needAltGr = (mods & 6) == 6;
                    // Atajos (Ctrl+C, Alt+F...): tecla física tal cual.
                    // Si no, sólo si los modificadores pulsados producen exactamente ese carácter.
                    if (((ctrl || alt) && !altGrHeld) || (needShift == shift && needAltGr == altGrHeld && (mods & 6) is 0 or 6))
                    {
                        _pressed[keysym] = (vk, false, null);
                        Send(new() { KeyVk(vk, false, false) });
                        return;
                    }
                }
            }

            // Carácter que no está en el teclado o modificadores distintos: se escribe como Unicode
            _pressed[keysym] = (0, false, text);
            Send(text.Select(c => KeyUnicode(c, false)).ToList());
        }
    }

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        lock (_capLock) _bmp?.Dispose();
    }
}

internal static class DesktopApi
{
    public const uint GENERIC_ALL = 0x10000000;
    private const int UOI_NAME = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool CloseDesktop(IntPtr hDesktop);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr h, int index, char[] buf, int len, out int needed);

    public static string NameOf(IntPtr h)
    {
        var buf = new char[256];
        return GetUserObjectInformationW(h, UOI_NAME, buf, buf.Length * 2, out _) ? new string(buf).TrimEnd('\0') : "";
    }
}
