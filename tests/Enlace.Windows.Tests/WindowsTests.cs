using System.Runtime.InteropServices;
using Enlace.Client;
using Enlace.Protocol;
using Enlace.Server;
using Enlace.Windows;

namespace Enlace.Windows.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 6900)]
    [InlineData("pc-oficina", "pc-oficina", 6900)]
    [InlineData("192.168.1.20:1", "192.168.1.20", 5901)]
    [InlineData("192.168.1.20:5905", "192.168.1.20", 5905)]
    [InlineData("192.168.1.20::5999", "192.168.1.20", 5999)]
    [InlineData("[fe80::1]:6000", "fe80::1", 6000)]
    [InlineData("fe80::1", "fe80::1", 6900)]
    public void Hosts(string input, string host, int port)
    {
        Assert.True(ClientConfig.TryParseHost(input, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Fact]
    public void SpecialKeysRoundTrip()
    {
        // Cada tecla especial del cliente debe existir en la tabla del servidor con la misma VK
        foreach (var (vk, ext) in new[] { (0x0D, false), (0x0D, true), (0x25, true), (0x2E, true), (0x70, false), (0x87, false), (0xA2, false), (0xA5, true), (0x5B, true), (0x1B, false), (0x08, false), (0x09, false) })
        {
            var sym = KeyMap.VkToSpecialKeysym(vk, ext, 0);
            Assert.NotNull(sym);
            Assert.True(KeyMap.SpecialToVk.TryGetValue(sym!.Value, out var back), $"vk {vk:X}");
            Assert.Equal(vk, back.Vk);
        }
        Assert.Equal("ñ", KeyMap.KeysymToText(KeyMap.CharToKeysym('ñ')));
        Assert.Equal("€", KeyMap.KeysymToText(KeyMap.CharToKeysym('€')));
        Assert.Equal("日", KeyMap.KeysymToText(KeyMap.CharToKeysym('日')));
    }
}

public class DesktopTests
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT p);

    [Fact]
    public void CapturesScreen()
    {
        using var d = new WindowsDesktop();
        var buf = Array.Empty<int>();
        Assert.True(d.CaptureInto(ref buf, out int w, out int h));
        var vs = SystemInformation.VirtualScreen;
        Assert.Equal(vs.Width, w);
        Assert.Equal(vs.Height, h);
        Assert.Equal(w * h, buf.Length);
    }

    [Fact]
    public void MovesMouse()
    {
        using var d = new WindowsDesktop();
        var vs = SystemInformation.VirtualScreen;
        d.Pointer(50, 60, 0);
        Thread.Sleep(100);
        Assert.True(GetCursorPos(out var p));
        Assert.InRange(p.X - vs.Left, 48, 52);
        Assert.InRange(p.Y - vs.Top, 58, 62);
    }

    [Fact]
    public async Task EndToEndWithRealScreen()
    {
        using var d = new WindowsDesktop();
        using var server = new RfbServer(d, new RfbServerOptions { Name = "CI", Password = () => "prueba" });
        server.Start(0);
        using var c = await RfbClient.ConnectAsync("127.0.0.1", server.Port, "prueba");
        int updates = 0;
        c.Updated += _ => Interlocked.Increment(ref updates);
        c.Start();
        var end = DateTime.UtcNow.AddSeconds(10);
        while (updates == 0 && DateTime.UtcNow < end) Thread.Sleep(20);
        Assert.True(updates > 0, "no llegó ninguna imagen");
        Assert.Equal(SystemInformation.VirtualScreen.Width, c.Width);

        // La imagen recibida debe coincidir con una captura directa
        var buf = Array.Empty<int>();
        d.CaptureInto(ref buf, out _, out _);
        int same = 0;
        lock (c.FramebufferLock)
            for (int i = 0; i < buf.Length; i += 97)
                if ((buf[i] | unchecked((int)0xFF000000)) == c.Framebuffer[i]) same++;
        Assert.True(same > buf.Length / 97 * 0.9, $"coincidencia baja: {same}");
    }
}
