using System.Net.Sockets;
using System.Text;

namespace Enlace.Protocol;

/// <summary>Cliente RFB 3.3/3.7/3.8. Pide siempre 32 bpp BGRX y decodifica Raw, CopyRect y ZRLE.</summary>
public sealed class RfbClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _net;
    private readonly BufferedStream _in;
    private readonly object _writeLock = new();
    private readonly PixelCodec _codec = new(PixelFormat.Bgrx32);
    private readonly ZrleDecoder _zrle = new();
    private int _closed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = "";

    /// <summary>Framebuffer ARGB. Protegido por <see cref="FramebufferLock"/>.</summary>
    public int[] Framebuffer { get; private set; } = Array.Empty<int>();
    public object FramebufferLock { get; } = new();

    public ExtChannel Ext { get; }

    public event Action<RectI>? Updated;
    public event Action<int, int>? Resized;
    public event Action<string>? TextReceived;
    public event Action? Bell;
    public event Action<Exception?>? Disconnected;

    private RfbClient(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _net = tcp.GetStream();
        _in = new BufferedStream(_net, 1 << 16);
        Ext = new ExtChannel(Write);
    }

    public static async Task<RfbClient> ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            var c = new RfbClient(tcp);
            tcp.ReceiveTimeout = 15000;
            await Task.Run(() => c.Handshake(password), timeout.Token).ConfigureAwait(false);
            tcp.ReceiveTimeout = 0;
            return c;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private string ReadReason()
    {
        uint n = _in.ReadU32();
        return n > 4096 ? "" : Encoding.UTF8.GetString(_in.ReadBytes((int)n));
    }

    private void Handshake(string password)
    {
        var ver = Encoding.ASCII.GetString(_in.ReadBytes(12));
        if (!ver.StartsWith("RFB ") || !int.TryParse(ver.AsSpan(4, 3), out int major) || !int.TryParse(ver.AsSpan(8, 3), out int minor))
            throw new RfbException("El equipo remoto no es un servidor VNC");
        if (major != 3) throw new RfbException($"Versión RFB no soportada: {ver.Trim()}");
        minor = minor >= 8 ? 8 : minor == 7 ? 7 : 3;
        _net.Write(Encoding.ASCII.GetBytes($"RFB 003.00{minor}\n"));

        byte sec;
        if (minor >= 7)
        {
            int n = _in.ReadU8();
            if (n == 0) throw new RfbException("El servidor rechazó la conexión: " + ReadReason());
            var types = _in.ReadBytes(n);
            if (types.Contains(Rfb.SecVncAuth)) sec = Rfb.SecVncAuth;
            else if (types.Contains(Rfb.SecNone)) sec = Rfb.SecNone;
            else throw new RfbException("El servidor sólo admite seguridad no compatible (p. ej. cifrado propio de RealVNC). " +
                                        "Configúralo para permitir autenticación VNC estándar.");
            _net.WriteByte(sec);
        }
        else
        {
            uint t = _in.ReadU32();
            if (t == 0) throw new RfbException("El servidor rechazó la conexión: " + ReadReason());
            if (t != Rfb.SecNone && t != Rfb.SecVncAuth) throw new RfbException("Tipo de seguridad no soportado");
            sec = (byte)t;
        }

        if (sec == Rfb.SecVncAuth)
        {
            var challenge = _in.ReadBytes(16);
            _net.Write(VncAuth.Response(challenge, password));
        }

        if (sec == Rfb.SecVncAuth || minor >= 8)
        {
            uint result = _in.ReadU32();
            if (result != 0)
            {
                string reason = minor >= 8 ? ReadReason() : "";
                throw new RfbAuthException(string.IsNullOrWhiteSpace(reason) ? "Contraseña incorrecta" : reason);
            }
        }

        _net.WriteByte(1); // ClientInit: compartido

        Width = _in.ReadU16();
        Height = _in.ReadU16();
        _in.Skip(16); // formato del servidor: pedimos el nuestro
        uint nameLen = _in.ReadU32();
        DesktopName = Encoding.UTF8.GetString(_in.ReadBytes((int)Math.Min(nameLen, 4096)));
        if (nameLen > 4096) _in.Skip(nameLen - 4096);
        Framebuffer = new int[Width * Height];

        var w = new PacketWriter();
        w.U8(Rfb.MsgSetPixelFormat).Zero(3);
        PixelFormat.Bgrx32.Write(w);
        int[] enc = { Rfb.EncZrle, Rfb.EncCopyRect, Rfb.EncRaw, Rfb.EncDesktopSize, ExtChannel.PseudoEncoding };
        w.U8(Rfb.MsgSetEncodings).U8(0).U16(enc.Length);
        foreach (var e in enc) w.S32(e);
        w.U8(Rfb.MsgFbUpdateRequest).U8(0).U16(0).U16(0).U16(Width).U16(Height);
        Write(w.ToArray());
    }

    /// <summary>Arranca el hilo de recepción. Los eventos se disparan en ese hilo.</summary>
    public void Start() => new Thread(ReadLoop) { IsBackground = true, Name = "rfb-client" }.Start();

    private void Write(byte[] data)
    {
        lock (_writeLock) _net.Write(data);
    }

    private void ReadLoop()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                byte t = _in.ReadU8();
                switch (t)
                {
                    case Rfb.MsgFbUpdate:
                        ReadUpdate();
                        RequestUpdate(true);
                        break;
                    case Rfb.MsgSetColourMap:
                        _in.Skip(1 + 2);
                        _in.Skip(_in.ReadU16() * 6L);
                        break;
                    case Rfb.MsgBell:
                        Bell?.Invoke();
                        break;
                    case Rfb.MsgServerCutText:
                    {
                        _in.Skip(3);
                        int len = _in.ReadS32();
                        if (len < 0) { _in.Skip(-(long)len); break; }
                        if (len > Rfb.MaxCutText) throw new RfbException("Texto demasiado largo");
                        var text = _in.ReadLatin1(len);
                        TextReceived?.Invoke(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                        break;
                    }
                    case ExtChannel.MessageType:
                        Ext.HandleIncoming(_in);
                        break;
                    default:
                        throw new RfbException($"Mensaje de servidor desconocido {t}");
                }
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closed) == 0) error = ex;
        }
        finally
        {
            Dispose();
            Disconnected?.Invoke(error);
        }
    }

    private void ReadUpdate()
    {
        _in.Skip(1);
        int n = _in.ReadU16();
        var dirty = default(RectI);
        for (int i = 0; i < n; i++)
        {
            int x = _in.ReadU16(), y = _in.ReadU16(), w = _in.ReadU16(), h = _in.ReadU16();
            int enc = _in.ReadS32();
            switch (enc)
            {
                case Rfb.EncRaw:
                {
                    var data = _in.ReadBytes(w * h * 4);
                    lock (FramebufferLock)
                    {
                        CheckBounds(x, y, w, h);
                        var fb = Framebuffer;
                        for (int r = 0, k = 0; r < h; r++)
                        {
                            int off = (y + r) * Width + x;
                            for (int c = 0; c < w; c++, k += 4)
                                fb[off + c] = unchecked((int)(0xFF000000u | (uint)data[k] | (uint)data[k + 1] << 8 | (uint)data[k + 2] << 16));
                        }
                    }
                    dirty = dirty.Union(new RectI(x, y, w, h));
                    break;
                }
                case Rfb.EncCopyRect:
                {
                    int sx = _in.ReadU16(), sy = _in.ReadU16();
                    lock (FramebufferLock)
                    {
                        CheckBounds(x, y, w, h);
                        CheckBounds(sx, sy, w, h);
                        var fb = Framebuffer;
                        var tmp = new int[w * h];
                        for (int r = 0; r < h; r++) Array.Copy(fb, (sy + r) * Width + sx, tmp, r * w, w);
                        for (int r = 0; r < h; r++) Array.Copy(tmp, r * w, fb, (y + r) * Width + x, w);
                    }
                    dirty = dirty.Union(new RectI(x, y, w, h));
                    break;
                }
                case Rfb.EncZrle:
                    lock (FramebufferLock)
                    {
                        CheckBounds(x, y, w, h);
                        _zrle.DecodeRect(_in, x, y, w, h, _codec, Framebuffer, Width, Height);
                    }
                    dirty = dirty.Union(new RectI(x, y, w, h));
                    break;
                case Rfb.EncDesktopSize:
                    lock (FramebufferLock)
                    {
                        Width = w;
                        Height = h;
                        Framebuffer = new int[w * h];
                    }
                    dirty = default;
                    Resized?.Invoke(w, h);
                    break;
                default:
                    throw new RfbException($"Codificación no soportada {enc}");
            }
        }
        if (!dirty.IsEmpty) Updated?.Invoke(dirty);
    }

    private void CheckBounds(int x, int y, int w, int h)
    {
        if (x + w > Width || y + h > Height) throw new RfbException("Rectángulo fuera de la pantalla");
    }

    public void RequestUpdate(bool incremental)
    {
        var w = new PacketWriter(10).U8(Rfb.MsgFbUpdateRequest).U8(incremental ? 1 : 0).U16(0).U16(0).U16(Width).U16(Height);
        Send(w);
    }

    private void Send(PacketWriter w)
    {
        try { Write(w.ToArray()); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
    }

    public void SendPointer(int x, int y, int buttonMask)
    {
        x = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        Send(new PacketWriter(6).U8(Rfb.MsgPointerEvent).U8(buttonMask).U16(x).U16(y));
    }

    public void SendKey(uint keysym, bool down) =>
        Send(new PacketWriter(8).U8(Rfb.MsgKeyEvent).U8(down ? 1 : 0).U16(0).U32(keysym));

    public void SendClipboardText(string text)
    {
        try
        {
            if (Ext.Active) { Ext.SendText(text); return; }
            var b = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
            Send(new PacketWriter(b.Length + 8).U8(Rfb.MsgClientCutText).Zero(3).U32((uint)b.Length).Bytes(b));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _tcp.Client.Shutdown(SocketShutdown.Both); } catch { }
        _tcp.Dispose();
        Ext.Dispose();
    }
}
