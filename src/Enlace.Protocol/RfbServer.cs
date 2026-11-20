using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Enlace.Protocol;

/// <summary>Lo que el servidor necesita del sistema: capturar pantalla e inyectar entrada.</summary>
public interface IDesktop
{
    /// <summary>
    /// Copia el último fotograma (ARGB, fila a fila) en <paramref name="buffer"/>, redimensionándolo si hace falta.
    /// </summary>
    bool CaptureInto(ref int[] buffer, out int width, out int height);

    void Pointer(int x, int y, int buttonMask);
    void Key(uint keysym, bool down);
}

public sealed class RfbServerOptions
{
    public string Name { get; set; } = "InfollopAssist";
    public Func<string> Password { get; set; } = () => "";
    public int MinFrameIntervalMs { get; set; } = 33;
    public bool ViewOnly { get; set; }
}

/// <summary>Servidor RFB 3.8 (compatible 3.3/3.7) con autenticación VNC.</summary>
public sealed class RfbServer : IDisposable
{
    private readonly IDesktop _desktop;
    private readonly RfbServerOptions _opt;
    private TcpListener? _listener;
    private readonly List<ServerSession> _sessions = new();
    private readonly Dictionary<IPAddress, (int Fails, DateTime Until)> _throttle = new();

    public RfbServer(IDesktop desktop, RfbServerOptions options)
    {
        _desktop = desktop;
        _opt = options;
    }

    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? 0;

    public event Action<ServerSession>? SessionStarted;
    public event Action<ServerSession>? SessionEnded;
    public event Action<string>? AuthFailed;

    public IReadOnlyList<ServerSession> Sessions { get { lock (_sessions) return _sessions.ToList(); } }

    public void Start(int port)
    {
        TcpListener l;
        try
        {
            l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            l.Start();
        }
        catch (SocketException)
        {
            l = new TcpListener(IPAddress.Any, port); // equipo sin IPv6
            l.Start();
        }
        _listener = l;
        new Thread(AcceptLoop) { IsBackground = true, Name = "rfb-accept" }.Start();
    }

    private void AcceptLoop()
    {
        var l = _listener!;
        while (true)
        {
            Socket s;
            try { s = l.AcceptSocket(); }
            catch { return; }
            var ip = ((IPEndPoint)s.RemoteEndPoint!).Address;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IsBlocked(ip)) { s.Dispose(); continue; }
            var session = new ServerSession(s, ip, _desktop, _opt, this);
            new Thread(session.Run) { IsBackground = true, Name = "rfb-session" }.Start();
        }
    }

    private bool IsBlocked(IPAddress ip)
    {
        lock (_throttle)
            return _throttle.TryGetValue(ip, out var t) && t.Until > DateTime.UtcNow;
    }

    internal void ReportAuth(IPAddress ip, bool ok)
    {
        lock (_throttle)
        {
            if (ok) { _throttle.Remove(ip); return; }
            _throttle.TryGetValue(ip, out var t);
            int fails = t.Fails + 1;
            // A partir del 5º fallo, bloqueo creciente
            var until = fails >= 5 ? DateTime.UtcNow.AddSeconds(Math.Min(600, 30 * (fails - 4))) : DateTime.MinValue;
            _throttle[ip] = (fails, until);
        }
        if (!ok) AuthFailed?.Invoke(ip.ToString());
    }

    internal void Added(ServerSession s)
    {
        lock (_sessions) _sessions.Add(s);
        SessionStarted?.Invoke(s);
    }

    internal void Removed(ServerSession s)
    {
        bool had;
        lock (_sessions) had = _sessions.Remove(s);
        if (had) SessionEnded?.Invoke(s);
    }

    public void DisconnectAll()
    {
        foreach (var s in Sessions) s.Close();
    }

    public void Dispose()
    {
        _listener?.Stop();
        DisconnectAll();
    }
}

public sealed class ServerSession
{
    private const int Tile = 64;
    private readonly Socket _sock;
    private readonly NetworkStream _net;
    private readonly BufferedStream _in;
    private readonly IDesktop _desktop;
    private readonly RfbServerOptions _opt;
    private readonly RfbServer _server;
    private readonly object _writeLock = new();
    private readonly object _stateLock = new();
    private readonly object _reqLock = new();

    private PixelCodec _codec = new(PixelFormat.Bgrx32);
    private bool _colourMapPending;
    private bool _zrle, _desktopSize;
    private int _fullGen = 1, _sentFullGen;
    private bool _pending;
    private volatile bool _closed;
    private int _closedFlag;
    private int _minor;

    private int _fbW, _fbH;
    private int[] _cap = Array.Empty<int>();
    private int[] _frame = Array.Empty<int>();
    private int[] _prev = Array.Empty<int>();
    private ZrleEncoder? _zrleEnc;
    private readonly HashSet<uint> _downKeys = new();
    private int _lastMask, _lastX, _lastY;

    internal ServerSession(Socket sock, IPAddress ip, IDesktop desktop, RfbServerOptions opt, RfbServer server)
    {
        _sock = sock;
        _sock.NoDelay = true;
        _net = new NetworkStream(sock, ownsSocket: true);
        _in = new BufferedStream(_net, 1 << 16);
        _desktop = desktop;
        _opt = opt;
        _server = server;
        RemoteAddress = ip;
        Ext = new ExtChannel(Write);
    }

    public IPAddress RemoteAddress { get; }
    public ExtChannel Ext { get; }
    public DateTime ConnectedAt { get; } = DateTime.Now;

    public event Action<string>? TextReceived;

    private void Write(byte[] data)
    {
        lock (_writeLock) _net.Write(data);
    }

    private void Write(PacketWriter w)
    {
        lock (_writeLock) _net.Write(w.Span);
    }

    internal void Run()
    {
        bool added = false;
        try
        {
            _sock.ReceiveTimeout = 30000; // durante el saludo
            if (!Handshake()) return;
            _sock.ReceiveTimeout = 0;
            _server.Added(this);
            added = true;
            new Thread(UpdateLoop) { IsBackground = true, Name = "rfb-update" }.Start();
            ReadLoop();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or RfbException or EndOfStreamException)
        {
        }
        finally
        {
            Close();
            if (added) _server.Removed(this);
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closedFlag, 1) != 0) return;
        _closed = true;
        lock (_reqLock) Monitor.PulseAll(_reqLock);
        ReleaseInput();
        try { _sock.Shutdown(SocketShutdown.Both); } catch { }
        _net.Dispose();
        Ext.Dispose();
        lock (_writeLock) _zrleEnc?.Dispose();
    }

    /// <summary>Si la conexión cae con teclas o botones pulsados, los suelta en este equipo.</summary>
    private void ReleaseInput()
    {
        try
        {
            uint[] keys;
            lock (_downKeys) { keys = _downKeys.ToArray(); _downKeys.Clear(); }
            foreach (var k in keys) _desktop.Key(k, false);
            if ((_lastMask & 7) != 0) _desktop.Pointer(_lastX, _lastY, 0);
        }
        catch { }
    }

    private bool Handshake()
    {
        _net.Write("RFB 003.008\n"u8);
        var ver = Encoding.ASCII.GetString(_in.ReadBytes(12));
        if (!ver.StartsWith("RFB ") || !int.TryParse(ver.AsSpan(8, 3), out _minor)) throw new RfbException("Versión no válida");
        _minor = _minor >= 8 ? 8 : _minor == 7 ? 7 : 3;

        if (_minor >= 7)
        {
            _net.Write(new byte[] { 1, Rfb.SecVncAuth });
            if (_in.ReadU8() != Rfb.SecVncAuth) throw new RfbException("Tipo de seguridad no válido");
        }
        else
        {
            _net.Write(new PacketWriter().U32(Rfb.SecVncAuth).Span);
        }

        var challenge = VncAuth.NewChallenge();
        _net.Write(challenge);
        var response = _in.ReadBytes(16);
        bool ok = VncAuth.Verify(challenge, response, _opt.Password());
        _server.ReportAuth(RemoteAddress, ok);
        if (!ok)
        {
            Thread.Sleep(1000);
            var w = new PacketWriter().U32(1);
            if (_minor >= 8)
            {
                var msg = Encoding.ASCII.GetBytes("Contrasena incorrecta");
                w.U32((uint)msg.Length).Bytes(msg);
            }
            _net.Write(w.Span);
            return false;
        }
        _net.Write(new PacketWriter().U32(0).Span);

        _in.ReadU8(); // ClientInit: flag compartido (siempre compartimos)

        _desktop.CaptureInto(ref _cap, out _fbW, out _fbH);
        if (_fbW <= 0 || _fbH <= 0) { _fbW = 1024; _fbH = 768; }
        var name = Encoding.UTF8.GetBytes(_opt.Name);
        var init = new PacketWriter().U16(_fbW).U16(_fbH);
        PixelFormat.Bgrx32.Write(init);
        init.U32((uint)name.Length).Bytes(name);
        Write(init);
        return true;
    }

    private void ReadLoop()
    {
        while (!_closed)
        {
            byte t = _in.ReadU8();
            switch (t)
            {
                case Rfb.MsgSetPixelFormat:
                {
                    _in.Skip(3);
                    var pf = PixelFormat.Read(_in.ReadBytes(16));
                    if (!pf.IsSupported) throw new RfbException("Formato de píxel no soportado");
                    bool cmap = false;
                    if (!pf.TrueColor)
                    {
                        if (pf.BitsPerPixel != 8) throw new RfbException("Mapa de colores no soportado");
                        pf = PixelFormat.Bgr233;
                        cmap = true;
                    }
                    lock (_stateLock)
                    {
                        _codec = new PixelCodec(pf);
                        _colourMapPending = cmap;
                        _fullGen++;
                    }
                    break;
                }
                case Rfb.MsgSetEncodings:
                {
                    _in.Skip(1);
                    int n = _in.ReadU16();
                    bool zrle = false, raw = false, dsz = false, ext = false;
                    int first = int.MinValue;
                    for (int i = 0; i < n; i++)
                    {
                        int e = _in.ReadS32();
                        if (e == Rfb.EncZrle) { zrle = true; if (first == int.MinValue) first = e; }
                        else if (e == Rfb.EncRaw) { raw = true; if (first == int.MinValue) first = e; }
                        else if (e == Rfb.EncDesktopSize) dsz = true;
                        else if (e == ExtChannel.PseudoEncoding) ext = true;
                    }
                    lock (_stateLock)
                    {
                        _zrle = zrle && !(raw && first == Rfb.EncRaw);
                        _desktopSize = dsz;
                        _fullGen++;
                    }
                    if (ext && !Ext.Active)
                    {
                        Ext.SendHello();
                        Ext.Activate();
                    }
                    break;
                }
                case Rfb.MsgFbUpdateRequest:
                {
                    byte incremental = _in.ReadU8();
                    _in.Skip(8);
                    if (incremental == 0) lock (_stateLock) _fullGen++;
                    lock (_reqLock) { _pending = true; Monitor.PulseAll(_reqLock); }
                    break;
                }
                case Rfb.MsgKeyEvent:
                {
                    bool down = _in.ReadU8() != 0;
                    _in.Skip(2);
                    uint keysym = _in.ReadU32();
                    if (_opt.ViewOnly) break;
                    lock (_downKeys) { if (down) _downKeys.Add(keysym); else _downKeys.Remove(keysym); }
                    _desktop.Key(keysym, down);
                    break;
                }
                case Rfb.MsgPointerEvent:
                {
                    int mask = _in.ReadU8();
                    int x = _in.ReadU16(), y = _in.ReadU16();
                    if (_opt.ViewOnly) break;
                    _lastMask = mask; _lastX = x; _lastY = y;
                    _desktop.Pointer(x, y, mask);
                    break;
                }
                case Rfb.MsgClientCutText:
                {
                    _in.Skip(3);
                    int len = _in.ReadS32();
                    if (len < 0) { _in.Skip(Math.Min((long)-(long)len, Rfb.MaxCutText)); break; } // portapapeles extendido: no anunciado
                    if (len > Rfb.MaxCutText) throw new RfbException("Texto demasiado largo");
                    var text = _in.ReadLatin1(len);
                    TextReceived?.Invoke(text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
                    break;
                }
                case ExtChannel.MessageType:
                    Ext.HandleIncoming(_in);
                    break;
                default:
                    throw new RfbException($"Mensaje de cliente desconocido {t}");
            }
        }
    }

    /// <summary>Envía texto al portapapeles del cliente (UTF-8 si es Enlace, Latin-1 si es un visor estándar).</summary>
    public void SendClipboardText(string text)
    {
        if (_closed) return;
        try
        {
            if (Ext.Active) { Ext.SendText(text); return; }
            var b = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
            var w = new PacketWriter(b.Length + 8).U8(Rfb.MsgServerCutText).Zero(3).U32((uint)b.Length).Bytes(b);
            Write(w);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Close(); }
    }

    private void UpdateLoop()
    {
        var sw = Stopwatch.StartNew();
        long lastFrame = -10_000;
        try
        {
            while (!_closed)
            {
                lock (_reqLock)
                {
                    while (!_pending && !_closed) Monitor.Wait(_reqLock, 1000);
                }
                if (_closed) break;

                long wait = _opt.MinFrameIntervalMs - (sw.ElapsedMilliseconds - lastFrame);
                if (wait > 0) Thread.Sleep((int)wait);
                lastFrame = sw.ElapsedMilliseconds;

                if (!TrySendUpdate()) Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Close();
        }
    }

    /// <summary>Captura, compara con lo último enviado y manda lo que ha cambiado. False si no había cambios.</summary>
    internal bool TrySendUpdate()
    {
        if (!_desktop.CaptureInto(ref _cap, out int cw, out int ch) || cw <= 0 || ch <= 0) return false;

        PixelCodec codec;
        bool zrle, dsz, cmap;
        int gen;
        lock (_stateLock)
        {
            codec = _codec;
            zrle = _zrle;
            dsz = _desktopSize;
            gen = _fullGen;
            cmap = _colourMapPending;
            _colourMapPending = false;
        }

        if (cmap) SendBgr233ColourMap();

        bool full = gen != _sentFullGen;
        bool resized = false;
        if ((cw != _fbW || ch != _fbH) && dsz)
        {
            _fbW = cw;
            _fbH = ch;
            resized = true;
            full = true;
        }

        int W = _fbW, H = _fbH;
        int[] frame;
        if (cw == W && ch == H) frame = _cap;
        else
        {
            // El visor no admite cambio de tamaño: recortar/rellenar al tamaño original
            if (_frame.Length != W * H) _frame = new int[W * H];
            else Array.Clear(_frame);
            int cpw = Math.Min(W, cw), cph = Math.Min(H, ch);
            for (int r = 0; r < cph; r++) Array.Copy(_cap, r * cw, _frame, r * W, cpw);
            frame = _frame;
        }

        if (_prev.Length != W * H) full = true;

        var rects = new List<RectI>();
        if (full) rects.Add(new RectI(0, 0, W, H));
        else Diff(frame, _prev, W, H, rects);

        if (rects.Count == 0 && !resized) return false;

        lock (_reqLock) _pending = false;
        _sentFullGen = gen;

        var w = new PacketWriter(1 << 16);
        w.U8(Rfb.MsgFbUpdate).U8(0).U16(rects.Count + (resized ? 1 : 0));
        if (resized) w.U16(0).U16(0).U16(W).U16(H).S32(Rfb.EncDesktopSize);
        lock (_writeLock)
        {
            foreach (var r in rects)
            {
                w.U16(r.X).U16(r.Y).U16(r.Width).U16(r.Height);
                if (zrle)
                {
                    w.S32(Rfb.EncZrle);
                    _zrleEnc ??= new ZrleEncoder();
                    _zrleEnc.EncodeRect(frame, W, r.X, r.Y, r.Width, r.Height, codec, w);
                }
                else
                {
                    w.S32(Rfb.EncRaw);
                    var dst = w.Reserve(r.Width * r.Height * codec.Bpp);
                    int k = 0;
                    for (int yy = r.Y; yy < r.Y + r.Height; yy++)
                    {
                        int off = yy * W;
                        for (int xx = r.X; xx < r.X + r.Width; xx++, k += codec.Bpp)
                            codec.WritePixel(dst.Slice(k, codec.Bpp), codec.FromArgb(frame[off + xx]));
                    }
                }
            }
            _net.Write(w.Span);
        }

        if (_prev.Length != W * H) _prev = new int[W * H];
        Array.Copy(frame, _prev, W * H);
        return true;
    }

    private static void Diff(int[] cur, int[] prev, int W, int H, List<RectI> rects)
    {
        for (int ty = 0; ty < H; ty += Tile)
        {
            int th = Math.Min(Tile, H - ty);
            int runStart = -1;
            for (int tx = 0; tx < W; tx += Tile)
            {
                int tw = Math.Min(Tile, W - tx);
                bool changed = false;
                for (int r = 0; r < th && !changed; r++)
                {
                    int off = (ty + r) * W + tx;
                    if (!cur.AsSpan(off, tw).SequenceEqual(prev.AsSpan(off, tw))) changed = true;
                }
                if (changed && runStart < 0) runStart = tx;
                else if (!changed && runStart >= 0)
                {
                    rects.Add(new RectI(runStart, ty, tx - runStart, th));
                    runStart = -1;
                }
            }
            if (runStart >= 0) rects.Add(new RectI(runStart, ty, W - runStart, th));
        }
    }

    private void SendBgr233ColourMap()
    {
        var w = new PacketWriter(256 * 6 + 8);
        w.U8(Rfb.MsgSetColourMap).U8(0).U16(0).U16(256);
        for (int i = 0; i < 256; i++)
        {
            int r = i & 7, g = (i >> 3) & 7, b = (i >> 6) & 3;
            w.U16(r * 65535 / 7).U16(g * 65535 / 7).U16(b * 65535 / 3);
        }
        Write(w);
    }
}
