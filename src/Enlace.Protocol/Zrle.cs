using System.IO.Compression;

namespace Enlace.Protocol;

/// <summary>
/// Codificador ZRLE (RFC 6143 §7.7.6). Un único flujo zlib por conexión; cada rectángulo
/// termina con un sync-flush. Teselas de 64x64: sólido, paleta empaquetada, paleta+RLE o crudo.
/// </summary>
internal sealed class ZrleEncoder : IDisposable
{
    private const int Tile = 64;
    private readonly MemoryStream _ms = new();
    private readonly ZLibStream _z;
    private readonly PacketWriter _tile = new(Tile * Tile * 4 + 1024);
    private readonly uint[] _px = new uint[Tile * Tile];
    private readonly uint[] _pal = new uint[128];
    private int _palN;

    // Tabla hash pequeña color -> índice de paleta
    private readonly uint[] _hKey = new uint[512];
    private readonly byte[] _hIdx = new byte[512];
    private readonly int[] _hStamp = new int[512];
    private int _stamp;

    public ZrleEncoder() => _z = new ZLibStream(_ms, CompressionLevel.Fastest, leaveOpen: true);

    public void EncodeRect(int[] fb, int stride, int x, int y, int w, int h, PixelCodec pc, PacketWriter output)
    {
        _ms.SetLength(0);
        for (int ty = y; ty < y + h; ty += Tile)
        {
            int th = Math.Min(Tile, y + h - ty);
            for (int tx = x; tx < x + w; tx += Tile)
            {
                int tw = Math.Min(Tile, x + w - tx);
                _tile.Clear();
                EncodeTile(fb, stride, tx, ty, tw, th, pc);
                _z.Write(_tile.Span);
            }
        }
        _z.Flush(); // Z_SYNC_FLUSH
        output.U32((uint)_ms.Length);
        output.Bytes(_ms.GetBuffer().AsSpan(0, (int)_ms.Length));
    }

    private int Lookup(uint v)
    {
        int h = (int)((v * 2654435761u) >> 23);
        while (_hStamp[h] == _stamp)
        {
            if (_hKey[h] == v) return _hIdx[h];
            h = (h + 1) & 511;
        }
        return -1;
    }

    private void Insert(uint v, int idx)
    {
        int h = (int)((v * 2654435761u) >> 23);
        while (_hStamp[h] == _stamp) h = (h + 1) & 511;
        _hStamp[h] = _stamp;
        _hKey[h] = v;
        _hIdx[h] = (byte)idx;
    }

    private void EncodeTile(int[] fb, int stride, int tx, int ty, int tw, int th, PixelCodec pc)
    {
        int n = tw * th;
        for (int r = 0, i = 0; r < th; r++)
        {
            int off = (ty + r) * stride + tx;
            for (int c = 0; c < tw; c++) _px[i++] = pc.FromArgb(fb[off + c]);
        }

        _stamp++;
        _palN = 0;
        bool overflow = false;
        for (int i = 0; i < n; i++)
        {
            uint v = _px[i];
            if (i > 0 && v == _px[i - 1]) continue;
            if (Lookup(v) >= 0) continue;
            if (_palN == 127) { overflow = true; break; }
            Insert(v, _palN);
            _pal[_palN++] = v;
        }

        int cb = pc.CBpp;
        if (!overflow && _palN == 1)
        {
            _tile.U8(1);
            pc.WriteCPixel(_tile.Reserve(cb), _pal[0]);
            return;
        }

        if (!overflow && _palN <= 16)
        {
            _tile.U8(_palN);
            for (int i = 0; i < _palN; i++) pc.WriteCPixel(_tile.Reserve(cb), _pal[i]);
            int bits = _palN == 2 ? 1 : _palN <= 4 ? 2 : 4;
            for (int r = 0; r < th; r++)
            {
                int cur = 0, nb = 0;
                for (int c = 0; c < tw; c++)
                {
                    cur = (cur << bits) | Lookup(_px[r * tw + c]);
                    nb += bits;
                    if (nb == 8) { _tile.U8(cur); cur = 0; nb = 0; }
                }
                if (nb > 0) _tile.U8(cur << (8 - nb));
            }
            return;
        }

        if (!overflow)
        {
            // Paleta + RLE
            _tile.U8(128 + _palN);
            for (int i = 0; i < _palN; i++) pc.WriteCPixel(_tile.Reserve(cb), _pal[i]);
            int p = 0;
            while (p < n)
            {
                uint v = _px[p];
                int run = 1;
                while (p + run < n && _px[p + run] == v) run++;
                int idx = Lookup(v);
                if (run == 1) _tile.U8(idx);
                else
                {
                    _tile.U8(idx | 128);
                    WriteRunLength(run);
                }
                p += run;
            }
            return;
        }

        // Crudo
        _tile.U8(0);
        var dst = _tile.Reserve(n * cb);
        for (int i = 0; i < n; i++) pc.WriteCPixel(dst.Slice(i * cb, cb), _px[i]);
    }

    private void WriteRunLength(int run)
    {
        int l = run - 1;
        while (l >= 255) { _tile.U8(255); l -= 255; }
        _tile.U8(l);
    }

    public void Dispose()
    {
        _z.Dispose();
        _ms.Dispose();
    }
}

/// <summary>Decodificador ZRLE con flujo zlib persistente entre rectángulos.</summary>
internal sealed class ZrleDecoder : IDisposable
{
    private const int Tile = 64;
    private readonly FeedStream _feed = new();
    private readonly ZLibStream _z;
    private readonly BufferedStream _zb;
    private readonly int[] _tile = new int[Tile * Tile];
    private readonly int[] _pal = new int[128];
    private readonly byte[] _cp = new byte[4];

    public ZrleDecoder()
    {
        _z = new ZLibStream(_feed, CompressionMode.Decompress, leaveOpen: true);
        _zb = new BufferedStream(_z, 1 << 16);
    }

    public void DecodeRect(Stream net, int x, int y, int w, int h, PixelCodec pc, int[] fb, int stride, int fbHeight)
    {
        uint len = net.ReadU32();
        if (len > 64 * 1024 * 1024) throw new RfbException("Rectángulo ZRLE demasiado grande");
        _feed.Append(net.ReadBytes((int)len));

        for (int ty = y; ty < y + h; ty += Tile)
        {
            int th = Math.Min(Tile, y + h - ty);
            for (int tx = x; tx < x + w; tx += Tile)
            {
                int tw = Math.Min(Tile, x + w - tx);
                DecodeTile(tw, th, pc);
                // Volcar la tesela al framebuffer (recortando por seguridad)
                for (int r = 0; r < th; r++)
                {
                    int yy = ty + r;
                    if (yy >= fbHeight) break;
                    int cw = Math.Min(tw, stride - tx);
                    if (cw <= 0) break;
                    Array.Copy(_tile, r * tw, fb, yy * stride + tx, cw);
                }
            }
        }
    }

    private int ReadCPixel(PixelCodec pc)
    {
        var s = _cp.AsSpan(0, pc.CBpp);
        ReadZ(s);
        return pc.ToArgb(pc.ReadCPixel(s));
    }

    private int ReadByteZ()
    {
        int b = _zb.ReadByte();
        if (b < 0) throw new RfbException("Datos ZRLE truncados");
        return b;
    }

    private void ReadZ(Span<byte> s)
    {
        while (s.Length > 0)
        {
            int n = _zb.Read(s);
            if (n <= 0) throw new RfbException("Datos ZRLE truncados");
            s = s[n..];
        }
    }

    private int ReadRunLength()
    {
        int l = 1, b;
        do { b = ReadByteZ(); l += b; } while (b == 255);
        return l;
    }

    private void DecodeTile(int tw, int th, PixelCodec pc)
    {
        int n = tw * th;
        int sub = ReadByteZ();
        if (sub == 0)
        {
            for (int i = 0; i < n; i++) _tile[i] = ReadCPixel(pc);
        }
        else if (sub == 1)
        {
            Array.Fill(_tile, ReadCPixel(pc), 0, n);
        }
        else if (sub <= 16)
        {
            for (int i = 0; i < sub; i++) _pal[i] = ReadCPixel(pc);
            int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4;
            int mask = (1 << bits) - 1;
            for (int r = 0; r < th; r++)
            {
                int b = 0, nb = 0;
                for (int c = 0; c < tw; c++)
                {
                    if (nb == 0) { b = ReadByteZ(); nb = 8; }
                    nb -= bits;
                    _tile[r * tw + c] = _pal[(b >> nb) & mask];
                }
            }
        }
        else if (sub == 128)
        {
            int p = 0;
            while (p < n)
            {
                int v = ReadCPixel(pc);
                int run = ReadRunLength();
                if (p + run > n) throw new RfbException("Run ZRLE inválido");
                Array.Fill(_tile, v, p, run);
                p += run;
            }
        }
        else if (sub >= 130)
        {
            int ps = sub - 128;
            for (int i = 0; i < ps; i++) _pal[i] = ReadCPixel(pc);
            int p = 0;
            while (p < n)
            {
                int b = ReadByteZ();
                int run = 1;
                if ((b & 128) != 0) run = ReadRunLength();
                int idx = b & 127;
                if (idx >= ps || p + run > n) throw new RfbException("Run ZRLE inválido");
                Array.Fill(_tile, _pal[idx], p, run);
                p += run;
            }
        }
        else throw new RfbException($"Subcodificación ZRLE desconocida {sub}");
    }

    public void Dispose()
    {
        _zb.Dispose();
        _z.Dispose();
    }

    /// <summary>Stream de lectura alimentado por trozos; devuelve 0 cuando no queda nada.</summary>
    private sealed class FeedStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _cur;
        private int _pos;

        public void Append(byte[] data) { if (data.Length > 0) _chunks.Enqueue(data); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0)
            {
                if (_cur == null || _pos >= _cur.Length)
                {
                    if (!_chunks.TryDequeue(out _cur)) { _cur = null; break; }
                    _pos = 0;
                }
                int k = Math.Min(buffer.Length, _cur.Length - _pos);
                _cur.AsSpan(_pos, k).CopyTo(buffer);
                _pos += k;
                total += k;
                buffer = buffer[k..];
            }
            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
