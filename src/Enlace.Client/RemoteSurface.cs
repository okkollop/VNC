using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Enlace.Protocol;
using Enlace.Windows;
using static Enlace.Windows.Native;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Enlace.Client;

/// <summary>Muestra la pantalla remota y le envía ratón y teclado.</summary>
internal sealed class RemoteSurface : Control
{
    private const int WM_MOUSEHWHEEL = 0x020E;

    private readonly RfbClient _c;
    private readonly object _bmpLock = new();
    private Bitmap? _bmp;
    private bool _fit = true;
    private readonly ImageAttributes _attrs = new();

    private readonly object _dirtyLock = new();
    private RectI _dirty;
    private bool _invalidatePosted;

    private int _buttons, _wheelV, _wheelH;
    private Point _lastRemote;
    private readonly Dictionary<long, uint> _down = new();
    private bool _suppressChar;
    private char _highSurrogate;

    public RemoteSurface(RfbClient client)
    {
        _c = client;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable | ControlStyles.Opaque, true);
        TabStop = true;
        BackColor = Color.Black;
        _attrs.SetWrapMode(WrapMode.TileFlipXY);
        Cursor = Cursors.Arrow;

        lock (_c.FramebufferLock) lock (_bmpLock) _bmp = NewBitmap(_c.Width, _c.Height);
        _c.Updated += OnRemoteUpdated;
        _c.Resized += OnRemoteResized;
    }

    public event Action? RemoteSizeChanged;

    public Size RemoteSize => new(_c.Width, _c.Height);

    public bool Fit
    {
        get => _fit;
        set { _fit = value; Invalidate(); }
    }

    private static Bitmap NewBitmap(int w, int h) => new(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppRgb);

    // ---------------- Imagen ----------------

    private void OnRemoteUpdated(RectI r)
    {
        lock (_c.FramebufferLock)
        lock (_bmpLock)
        {
            var bmp = _bmp;
            if (bmp == null || bmp.Width != _c.Width || bmp.Height != _c.Height) return;
            var data = bmp.LockBits(new Rectangle(r.X, r.Y, r.Width, r.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                var fb = _c.Framebuffer;
                for (int y = 0; y < r.Height; y++)
                    Marshal.Copy(fb, (r.Y + y) * _c.Width + r.X, data.Scan0 + y * data.Stride, r.Width);
            }
            finally { bmp.UnlockBits(data); }
        }

        lock (_dirtyLock)
        {
            _dirty = _dirty.Union(r);
            if (_invalidatePosted) return;
            _invalidatePosted = true;
        }
        try { BeginInvoke(FlushDirty); } catch (InvalidOperationException) { }
    }

    private void FlushDirty()
    {
        RectI d;
        lock (_dirtyLock)
        {
            d = _dirty;
            _dirty = default;
            _invalidatePosted = false;
        }
        if (d.IsEmpty) return;
        var rect = RemoteToLocal(new Rectangle(d.X, d.Y, d.Width, d.Height));
        rect.Inflate(2, 2);
        Invalidate(rect);
    }

    private void OnRemoteResized(int w, int h)
    {
        lock (_c.FramebufferLock)
        lock (_bmpLock)
        {
            _bmp?.Dispose();
            _bmp = NewBitmap(w, h);
        }
        try { BeginInvoke(() => { RemoteSizeChanged?.Invoke(); Invalidate(); }); } catch (InvalidOperationException) { }
    }

    private Rectangle DestRect()
    {
        int rw = Math.Max(1, _c.Width), rh = Math.Max(1, _c.Height);
        if (!_fit) return new Rectangle(0, 0, rw, rh);
        double s = Math.Min((double)ClientSize.Width / rw, (double)ClientSize.Height / rh);
        int w = Math.Max(1, (int)(rw * s)), h = Math.Max(1, (int)(rh * s));
        return new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }

    private Rectangle RemoteToLocal(Rectangle r)
    {
        var d = DestRect();
        double sx = (double)d.Width / Math.Max(1, _c.Width), sy = (double)d.Height / Math.Max(1, _c.Height);
        int x1 = d.X + (int)Math.Floor(r.X * sx), y1 = d.Y + (int)Math.Floor(r.Y * sy);
        int x2 = d.X + (int)Math.Ceiling(r.Right * sx), y2 = d.Y + (int)Math.Ceiling(r.Bottom * sy);
        return Rectangle.FromLTRB(x1, y1, x2, y2);
    }

    private Point LocalToRemote(Point p)
    {
        var d = DestRect();
        int x = (int)((p.X - d.X) * (double)_c.Width / d.Width);
        int y = (int)((p.Y - d.Y) * (double)_c.Height / d.Height);
        return new Point(Math.Clamp(x, 0, Math.Max(0, _c.Width - 1)), Math.Clamp(y, 0, Math.Max(0, _c.Height - 1)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var d = DestRect();
        using (var outside = new Region(e.ClipRectangle))
        {
            outside.Exclude(d);
            g.FillRegion(Brushes.Black, outside);
        }

        lock (_bmpLock)
        {
            if (_bmp == null) return;
            var clip = Rectangle.Intersect(e.ClipRectangle, d);
            if (clip.IsEmpty) return;
            if (d.Width == _bmp.Width && d.Height == _bmp.Height)
            {
                g.DrawImage(_bmp, clip, new Rectangle(clip.X - d.X, clip.Y - d.Y, clip.Width, clip.Height), GraphicsUnit.Pixel);
                return;
            }
            // Escalado: sólo la parte de la imagen que corresponde a la zona a repintar
            g.InterpolationMode = d.Width < _bmp.Width ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            double sx = (double)_bmp.Width / d.Width, sy = (double)_bmp.Height / d.Height;
            float srcX = (float)((clip.X - d.X) * sx), srcY = (float)((clip.Y - d.Y) * sy);
            g.DrawImage(_bmp, clip, srcX, srcY, (float)(clip.Width * sx), (float)(clip.Height * sy), GraphicsUnit.Pixel, _attrs);
        }
    }

    // ---------------- Ratón ----------------

    private static int Bit(MouseButtons b) => b switch
    {
        MouseButtons.Left => 1,
        MouseButtons.Middle => 2,
        MouseButtons.Right => 4,
        _ => 0,
    };

    private void SendPointer(Point local, int mask)
    {
        var p = LocalToRemote(local);
        _lastRemote = p;
        _c.SendPointer(p.X, p.Y, mask);
    }

    protected override void OnMouseMove(MouseEventArgs e) => SendPointer(e.Location, _buttons);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _buttons |= Bit(e.Button);
        SendPointer(e.Location, _buttons);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _buttons &= ~Bit(e.Button);
        SendPointer(e.Location, _buttons);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _wheelV += e.Delta;
        while (Math.Abs(_wheelV) >= 120)
        {
            int bit = _wheelV > 0 ? 8 : 16;
            SendPointer(e.Location, _buttons | bit);
            SendPointer(e.Location, _buttons);
            _wheelV -= Math.Sign(_wheelV) * 120;
        }
    }

    private void OnHWheel(int delta, Point screen)
    {
        _wheelH += delta;
        var local = PointToClient(screen);
        while (Math.Abs(_wheelH) >= 120)
        {
            int bit = _wheelH > 0 ? 64 : 32;
            SendPointer(local, _buttons | bit);
            SendPointer(local, _buttons);
            _wheelH -= Math.Sign(_wheelH) * 120;
        }
    }

    // ---------------- Teclado ----------------

    protected override bool IsInputKey(Keys keyData) => true;
    protected override bool IsInputChar(char charCode) => true;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => false;
    protected override bool ProcessDialogKey(Keys keyData) => false;
    protected override bool ProcessDialogChar(char charCode) => false;
    protected override bool ProcessMnemonic(char charCode) => false;

    public void SendKey(uint keysym, bool down) => _c.SendKey(keysym, down);

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                KeyDownMsg((int)m.WParam, (long)m.LParam);
                return; // no pasar a Windows: evita el menú con Alt, F10, etc.
            case WM_KEYUP or WM_SYSKEYUP:
                KeyUpMsg((int)m.WParam, (long)m.LParam);
                return;
            case WM_CHAR or WM_SYSCHAR:
                CharMsg((char)(int)m.WParam);
                return;
            case WM_DEADCHAR or WM_SYSDEADCHAR:
                return;
            case WM_MOUSEHWHEEL:
            {
                long wp = (long)m.WParam, lp = (long)m.LParam;
                OnHWheel((short)(wp >> 16), new Point((short)lp, (short)(lp >> 16)));
                return;
            }
        }
        base.WndProc(ref m);
    }

    private static long KeyId(int vk, long lParam) => (long)(uint)vk | (((lParam >> 16) & 0x1FF) << 8);

    private void KeyDownMsg(int vk, long lParam)
    {
        _suppressChar = false;
        if (vk == 0xE5) return; // VK_PROCESSKEY (IME)
        bool ext = ((lParam >> 24) & 1) != 0;
        int scan = (int)((lParam >> 16) & 0xFF);
        long id = KeyId(vk, lParam);

        var special = KeyMap.VkToSpecialKeysym(vk, ext, scan);
        if (special != null)
        {
            _down[id] = special.Value;
            _c.SendKey(special.Value, true);
            return;
        }

        bool ctrl = GetKeyState(0x11) < 0, alt = GetKeyState(0x12) < 0;
        bool altGr = ctrl && alt && GetKeyState(0xA5) < 0;
        if ((ctrl || alt) && !altGr)
        {
            // Atajo (Ctrl+C, Alt+F4...): mandar la tecla "base" sin modificar
            uint ch = MapVirtualKeyW((uint)vk, 2) & 0xFFFF;
            if (ch != 0)
            {
                uint sym = KeyMap.CharToKeysym(char.ToLowerInvariant((char)ch));
                _down[id] = sym;
                _c.SendKey(sym, true);
                _suppressChar = true;
            }
        }
        // En otro caso el carácter llega con WM_CHAR (respeta distribución, AltGr y acentos)
    }

    private void KeyUpMsg(int vk, long lParam)
    {
        if (_down.Remove(KeyId(vk, lParam), out var sym)) _c.SendKey(sym, false);
        else if (vk is 0x10 or 0x11 or 0x12)
        {
            // Suelta de modificador cuyo "pulsar" no vimos (p. ej. foco recibido con la tecla pulsada)
            var s = KeyMap.VkToSpecialKeysym(vk, ((lParam >> 24) & 1) != 0, (int)((lParam >> 16) & 0xFF));
            if (s != null) _c.SendKey(s.Value, false);
        }
    }

    private void CharMsg(char ch)
    {
        if (_suppressChar) { _suppressChar = false; return; }
        if (char.IsHighSurrogate(ch)) { _highSurrogate = ch; return; }
        uint sym;
        if (char.IsLowSurrogate(ch) && _highSurrogate != 0)
        {
            sym = 0x01000000u | (uint)char.ConvertToUtf32(_highSurrogate, ch);
            _highSurrogate = '\0';
        }
        else
        {
            if (ch < 0x20 || ch == 0x7F) return; // ya enviado como tecla especial
            sym = KeyMap.CharToKeysym(ch);
        }
        _c.SendKey(sym, true);
        _c.SendKey(sym, false);
    }

    /// <summary>Suelta todo lo pulsado (al perder el foco), para que no se quede nada "enganchado" en remoto.</summary>
    public void ReleaseAll()
    {
        foreach (var sym in _down.Values) _c.SendKey(sym, false);
        _down.Clear();
        if (_buttons != 0)
        {
            _buttons = 0;
            _c.SendPointer(_lastRemote.X, _lastRemote.Y, 0);
        }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        ReleaseAll();
        base.OnLostFocus(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _c.Updated -= OnRemoteUpdated;
            _c.Resized -= OnRemoteResized;
            lock (_bmpLock) { _bmp?.Dispose(); _bmp = null; }
            _attrs.Dispose();
        }
        base.Dispose(disposing);
    }
}
