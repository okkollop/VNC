using System.Buffers.Binary;
using System.Text;

namespace Enlace.Protocol;

/// <summary>Lectura big-endian sobre un Stream (RFB usa orden de red).</summary>
internal static class StreamExt
{
    public static byte ReadU8(this Stream s)
    {
        int b = s.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public static ushort ReadU16(this Stream s)
    {
        Span<byte> b = stackalloc byte[2];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    public static uint ReadU32(this Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    public static int ReadS32(this Stream s) => unchecked((int)s.ReadU32());

    public static ulong ReadU64(this Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt64BigEndian(b);
    }

    public static byte[] ReadBytes(this Stream s, int n)
    {
        var b = new byte[n];
        s.ReadExactly(b);
        return b;
    }

    public static void Skip(this Stream s, long n)
    {
        Span<byte> tmp = stackalloc byte[512];
        while (n > 0)
        {
            int k = (int)Math.Min(n, tmp.Length);
            s.ReadExactly(tmp[..k]);
            n -= k;
        }
    }

    public static string ReadLatin1(this Stream s, int n) => Encoding.Latin1.GetString(s.ReadBytes(n));
}

/// <summary>Buffer creciente para construir mensajes big-endian.</summary>
internal sealed class PacketWriter
{
    private byte[] _buf;
    private int _len;

    public PacketWriter(int capacity = 256) => _buf = new byte[capacity];

    public int Length => _len;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);
    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    public void Clear() => _len = 0;

    public Span<byte> Reserve(int n)
    {
        Ensure(n);
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    private void Ensure(int n)
    {
        if (_len + n <= _buf.Length) return;
        int cap = Math.Max(_buf.Length * 2, _len + n);
        Array.Resize(ref _buf, cap);
    }

    public PacketWriter U8(int v) { Reserve(1)[0] = (byte)v; return this; }
    public PacketWriter U16(int v) { BinaryPrimitives.WriteUInt16BigEndian(Reserve(2), (ushort)v); return this; }
    public PacketWriter U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), v); return this; }
    public PacketWriter S32(int v) { BinaryPrimitives.WriteInt32BigEndian(Reserve(4), v); return this; }
    public PacketWriter U64(ulong v) { BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), v); return this; }
    public PacketWriter Zero(int n) { Reserve(n).Clear(); return this; }
    public PacketWriter Bytes(ReadOnlySpan<byte> b) { b.CopyTo(Reserve(b.Length)); return this; }

    public void PatchU16(int offset, int v) => BinaryPrimitives.WriteUInt16BigEndian(_buf.AsSpan(offset, 2), (ushort)v);
    public void PatchU32(int offset, uint v) => BinaryPrimitives.WriteUInt32BigEndian(_buf.AsSpan(offset, 4), v);
}

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectI Union(RectI o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        int x1 = Math.Min(X, o.X), y1 = Math.Min(Y, o.Y);
        int x2 = Math.Max(X + Width, o.X + o.Width), y2 = Math.Max(Y + Height, o.Y + o.Height);
        return new RectI(x1, y1, x2 - x1, y2 - y1);
    }
}

public static class Rfb
{
    public const int DefaultPort = 6900;

    // Tipos de seguridad
    public const byte SecInvalid = 0, SecNone = 1, SecVncAuth = 2;

    // Mensajes cliente -> servidor
    public const byte MsgSetPixelFormat = 0, MsgSetEncodings = 2, MsgFbUpdateRequest = 3,
        MsgKeyEvent = 4, MsgPointerEvent = 5, MsgClientCutText = 6;

    // Mensajes servidor -> cliente
    public const byte MsgFbUpdate = 0, MsgSetColourMap = 1, MsgBell = 2, MsgServerCutText = 3;

    // Codificaciones
    public const int EncRaw = 0, EncCopyRect = 1, EncZrle = 16;
    public const int EncDesktopSize = -223;

    /// <summary>Límite de texto de portapapeles aceptado (10 MB).</summary>
    public const int MaxCutText = 10 * 1024 * 1024;
}

public class RfbException : Exception
{
    public RfbException(string message) : base(message) { }
}

public sealed class RfbAuthException : RfbException
{
    public RfbAuthException(string message) : base(message) { }
}
