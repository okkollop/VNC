using System.Security.Cryptography;
using Enlace.Protocol;

namespace Enlace.Protocol.Tests;

public class DesTests
{
    [Fact]
    public void KnownVector()
    {
        var des = new Des(Convert.FromHexString("133457799BBCDFF1"));
        var o = new byte[8];
        des.EncryptBlock(Convert.FromHexString("0123456789ABCDEF"), o);
        Assert.Equal("85E813540F0AB405", Convert.ToHexString(o));
    }

    [Fact]
    public void MatchesDotNetDes()
    {
        var rnd = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var key = new byte[8];
            var block = new byte[8];
            rnd.NextBytes(key);
            rnd.NextBytes(block);
            if (DES.IsWeakKey(key) || DES.IsSemiWeakKey(key)) continue;
            using var d = DES.Create();
            d.Key = key;
            var expected = d.EncryptEcb(block, PaddingMode.None);
            var o = new byte[8];
            new Des(key).EncryptBlock(block, o);
            Assert.Equal(expected, o);
        }
    }

    [Fact]
    public void EmptyPasswordWorks()
    {
        var ch = VncAuth.NewChallenge();
        Assert.True(VncAuth.Verify(ch, VncAuth.Response(ch, ""), ""));
        Assert.False(VncAuth.Verify(ch, VncAuth.Response(ch, "x"), ""));
        // Sólo cuentan 8 caracteres
        Assert.True(VncAuth.Verify(ch, VncAuth.Response(ch, "12345678zzz"), "12345678"));
    }
}

public class ZrleTests
{
    public static IEnumerable<object[]> Formats() => new[]
    {
        new object[] { PixelFormat.Bgrx32 },
        new object[] { new PixelFormat(32, 24, true, true, 255, 255, 255, 16, 8, 0) },   // BE, CPIXEL bajo
        new object[] { new PixelFormat(32, 24, false, true, 255, 255, 255, 24, 16, 8) }, // CPIXEL alto
        new object[] { new PixelFormat(32, 32, false, true, 255, 255, 255, 0, 8, 16) },  // CPIXEL = 4 bytes
        new object[] { new PixelFormat(16, 16, false, true, 31, 63, 31, 11, 5, 0) },     // RGB565
        new object[] { PixelFormat.Bgr233 },
    };

    private static int[] MakeImage(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        var img = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int zone = (x / 64 + y / 64 * 7) % 5;
                img[y * w + x] = zone switch
                {
                    0 => unchecked((int)0xFF336699),                                          // sólido
                    1 => (x + y) % 2 == 0 ? unchecked((int)0xFF000000) : -1,                 // 2 colores
                    2 => unchecked((int)0xFF000000) | ((x * 3 + y) % 12) * 0x101010,        // ≤16 colores
                    3 => unchecked((int)0xFF000000) | (((x / 3) % 90) * 0x020301),          // paleta RLE
                    _ => unchecked((int)0xFF000000) | rnd.Next(0x1000000),                  // crudo
                };
            }
        return img;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void RoundTrip(PixelFormat pf)
    {
        var codec = new PixelCodec(pf);
        int W = 300, H = 200;
        using var enc = new ZrleEncoder();
        using var dec = new ZrleDecoder();
        var fbOut = new int[W * H];

        // Varios rectángulos y fotogramas con el mismo flujo zlib
        for (int frame = 0; frame < 3; frame++)
        {
            var img = MakeImage(W, H, frame);
            var rects = new[] { new RectI(0, 0, W, 100), new RectI(0, 100, 150, 100), new RectI(150, 100, 150, 100) };
            foreach (var r in rects)
            {
                var w = new PacketWriter();
                enc.EncodeRect(img, W, r.X, r.Y, r.Width, r.Height, codec, w);
                using var ms = new MemoryStream(w.ToArray());
                dec.DecodeRect(ms, r.X, r.Y, r.Width, r.Height, codec, fbOut, W, H);
                Assert.Equal(ms.Length, ms.Position);
            }
            for (int i = 0; i < img.Length; i++)
            {
                int expected = codec.ToArgb(codec.FromArgb(img[i]));
                Assert.True(expected == fbOut[i], $"frame {frame} px {i % W},{i / W}: {expected:X8} != {fbOut[i]:X8}");
            }
        }
    }
}

public class PathTests
{
    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\x")]
    [InlineData("a//b")]
    [InlineData("a/b:c")]
    [InlineData("nombre.")]
    public void RejectsBadPaths(string p) => Assert.Throws<RfbException>(() => ExtChannel.SanitizeRelative(p));

    [Fact]
    public void AcceptsNormal() => Assert.Equal(3, ExtChannel.SanitizeRelative("Carpeta/sub/año ñ.txt").Length);
}
