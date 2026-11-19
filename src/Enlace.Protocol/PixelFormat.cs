using System.Buffers.Binary;

namespace Enlace.Protocol;

/// <summary>Formato de píxel RFB (sección 7.4 del RFC 6143).</summary>
public readonly record struct PixelFormat(
    byte BitsPerPixel, byte Depth, bool BigEndian, bool TrueColor,
    ushort RedMax, ushort GreenMax, ushort BlueMax,
    byte RedShift, byte GreenShift, byte BlueShift)
{
    /// <summary>32 bpp, little endian, 0x00RRGGBB: coincide con la memoria de un bitmap de Windows.</summary>
    public static readonly PixelFormat Bgrx32 = new(32, 24, false, true, 255, 255, 255, 16, 8, 0);

    /// <summary>8 bpp BGR233, usado cuando el cliente pide mapa de colores.</summary>
    public static readonly PixelFormat Bgr233 = new(8, 8, false, true, 7, 7, 3, 0, 3, 6);

    public int BytesPerPixel => BitsPerPixel / 8;

    public static PixelFormat Read(ReadOnlySpan<byte> b) => new(
        b[0], b[1], b[2] != 0, b[3] != 0,
        BinaryPrimitives.ReadUInt16BigEndian(b[4..]),
        BinaryPrimitives.ReadUInt16BigEndian(b[6..]),
        BinaryPrimitives.ReadUInt16BigEndian(b[8..]),
        b[10], b[11], b[12]);

    internal void Write(PacketWriter w)
    {
        w.U8(BitsPerPixel).U8(Depth).U8(BigEndian ? 1 : 0).U8(TrueColor ? 1 : 0)
         .U16(RedMax).U16(GreenMax).U16(BlueMax)
         .U8(RedShift).U8(GreenShift).U8(BlueShift).Zero(3);
    }

    public bool IsSupported =>
        BitsPerPixel is 8 or 16 or 32 &&
        (!TrueColor || (RedMax > 0 && GreenMax > 0 && BlueMax > 0 &&
                        RedShift < 32 && GreenShift < 32 && BlueShift < 32));
}

/// <summary>
/// Conversión entre ARGB (0xAARRGGBB, como los bitmaps de Windows) y un PixelFormat concreto,
/// incluida la variante comprimida CPIXEL de ZRLE.
/// </summary>
internal sealed class PixelCodec
{
    public readonly PixelFormat Format;
    public readonly int Bpp;          // bytes por píxel
    public readonly int CBpp;         // bytes por CPIXEL
    private readonly bool _cHigh;     // CPIXEL toma los 3 bytes altos
    private readonly bool _identity;  // formato == Bgrx32

    public PixelCodec(PixelFormat pf)
    {
        if (!pf.IsSupported || !pf.TrueColor) throw new RfbException("Formato de píxel no soportado");
        Format = pf;
        Bpp = pf.BytesPerPixel;
        CBpp = Bpp;
        if (pf.BitsPerPixel == 32 && pf.Depth <= 24)
        {
            uint mask = ((uint)pf.RedMax << pf.RedShift) | ((uint)pf.GreenMax << pf.GreenShift) | ((uint)pf.BlueMax << pf.BlueShift);
            if ((mask & 0xFF000000u) == 0) CBpp = 3;
            else if ((mask & 0xFFu) == 0) { CBpp = 3; _cHigh = true; }
        }
        _identity = pf == PixelFormat.Bgrx32;
    }

    public uint FromArgb(int argb)
    {
        if (_identity) return (uint)argb & 0xFFFFFFu;
        var f = Format;
        uint r = ((uint)argb >> 16) & 255, g = ((uint)argb >> 8) & 255, b = (uint)argb & 255;
        if (f.RedMax != 255) r = (r * f.RedMax + 127) / 255;
        if (f.GreenMax != 255) g = (g * f.GreenMax + 127) / 255;
        if (f.BlueMax != 255) b = (b * f.BlueMax + 127) / 255;
        return (r << f.RedShift) | (g << f.GreenShift) | (b << f.BlueShift);
    }

    public int ToArgb(uint p)
    {
        if (_identity) return unchecked((int)(0xFF000000u | (p & 0xFFFFFFu)));
        var f = Format;
        uint r = (p >> f.RedShift) & f.RedMax, g = (p >> f.GreenShift) & f.GreenMax, b = (p >> f.BlueShift) & f.BlueMax;
        if (f.RedMax != 255) r = (r * 255 + f.RedMax / 2u) / f.RedMax;
        if (f.GreenMax != 255) g = (g * 255 + f.GreenMax / 2u) / f.GreenMax;
        if (f.BlueMax != 255) b = (b * 255 + f.BlueMax / 2u) / f.BlueMax;
        if (r > 255) r = 255;
        if (g > 255) g = 255;
        if (b > 255) b = 255;
        return unchecked((int)(0xFF000000u | (r << 16) | (g << 8) | b));
    }

    private static void WriteN(Span<byte> d, uint v, int n, bool be)
    {
        if (be) for (int i = 0; i < n; i++) d[i] = (byte)(v >> (8 * (n - 1 - i)));
        else for (int i = 0; i < n; i++) d[i] = (byte)(v >> (8 * i));
    }

    private static uint ReadN(ReadOnlySpan<byte> s, int n, bool be)
    {
        uint v = 0;
        if (be) for (int i = 0; i < n; i++) v = (v << 8) | s[i];
        else for (int i = n - 1; i >= 0; i--) v = (v << 8) | s[i];
        return v;
    }

    public void WritePixel(Span<byte> d, uint v) => WriteN(d, v, Bpp, Format.BigEndian);
    public uint ReadPixel(ReadOnlySpan<byte> s) => ReadN(s, Bpp, Format.BigEndian);

    public void WriteCPixel(Span<byte> d, uint v)
    {
        if (CBpp == 3) WriteN(d, _cHigh ? v >> 8 : v, 3, Format.BigEndian);
        else WritePixel(d, v);
    }

    public uint ReadCPixel(ReadOnlySpan<byte> s)
    {
        if (CBpp == 3)
        {
            uint v = ReadN(s, 3, Format.BigEndian);
            return _cHigh ? v << 8 : v;
        }
        return ReadPixel(s);
    }
}
