using System.Collections.Concurrent;
using Enlace.Protocol;

namespace Enlace.Protocol.Tests;

internal sealed class FakeDesktop : IDesktop
{
    private readonly object _lock = new();
    public int W = 320, H = 240;
    public int[] Pixels;
    public readonly ConcurrentQueue<(int X, int Y, int Mask)> Pointers = new();
    public readonly ConcurrentQueue<(uint Sym, bool Down)> Keys = new();

    public FakeDesktop()
    {
        Pixels = new int[W * H];
        for (int i = 0; i < Pixels.Length; i++) Pixels[i] = unchecked((int)0xFF000000) | (i * 2654435761 % 0xFFFFFF > 0x800000 ? 0x2040A0 : 0xE0E0E0);
    }

    public void Mutate(Action<int[], int, int> f)
    {
        lock (_lock) f(Pixels, W, H);
    }

    public void Resize(int w, int h)
    {
        lock (_lock)
        {
            W = w;
            H = h;
            Pixels = Enumerable.Repeat(unchecked((int)0xFF112233), w * h).ToArray();
        }
    }

    public bool CaptureInto(ref int[] buffer, out int width, out int height)
    {
        lock (_lock)
        {
            if (buffer.Length != Pixels.Length) buffer = new int[Pixels.Length];
            Array.Copy(Pixels, buffer, Pixels.Length);
            width = W;
            height = H;
        }
        return true;
    }

    public void Pointer(int x, int y, int mask) => Pointers.Enqueue((x, y, mask));
    public void Key(uint keysym, bool down) => Keys.Enqueue((keysym, down));
}

public class LoopbackTests : IDisposable
{
    private readonly FakeDesktop _desk = new();
    private readonly RfbServer _server;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "enlace-test-" + Guid.NewGuid().ToString("N"));

    public LoopbackTests()
    {
        _server = new RfbServer(_desk, new RfbServerOptions { Name = "Prueba", Password = () => "secreto", MinFrameIntervalMs = 5 });
        _server.Start(0);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static void WaitUntil(Func<bool> cond, string what, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            Thread.Sleep(10);
        }
    }

    private bool FbMatches(RfbClient c)
    {
        lock (c.FramebufferLock)
        {
            if (c.Width != _desk.W || c.Height != _desk.H) return false;
            int[] snap = Array.Empty<int>();
            _desk.CaptureInto(ref snap, out _, out _);
            for (int i = 0; i < snap.Length; i++)
                if ((snap[i] | unchecked((int)0xFF000000)) != c.Framebuffer[i]) return false;
            return true;
        }
    }

    private async Task<RfbClient> Connect()
    {
        var c = await RfbClient.ConnectAsync("127.0.0.1", _server.Port, "secreto");
        c.Start();
        return c;
    }

    [Fact]
    public async Task WrongPasswordIsRejected()
    {
        await Assert.ThrowsAsync<RfbAuthException>(() => RfbClient.ConnectAsync("127.0.0.1", _server.Port, "malo"));
    }

    [Fact]
    public async Task ScreenInputAndResize()
    {
        using var c = await Connect();
        Assert.Equal("Prueba", c.DesktopName);
        WaitUntil(() => FbMatches(c), "primer fotograma");

        _desk.Mutate((p, w, h) => { for (int y = 100; y < 130; y++) for (int x = 10; x < 300; x++) p[y * w + x] = unchecked((int)0xFFFF0000); });
        WaitUntil(() => FbMatches(c), "actualización incremental");

        c.SendPointer(12, 34, 1);
        c.SendKey(0x61, true);
        c.SendKey(0x61, false);
        WaitUntil(() => _desk.Pointers.Contains((12, 34, 1)), "puntero");
        WaitUntil(() => _desk.Keys.Count == 2, "teclas");

        int resized = 0;
        c.Resized += (_, _) => resized++;
        _desk.Resize(400, 300);
        WaitUntil(() => resized == 1 && FbMatches(c), "cambio de resolución");
    }

    [Fact]
    public async Task ClipboardAndFiles()
    {
        ServerSession? session = null;
        _server.SessionStarted += s => session = s;
        using var c = await Connect();
        WaitUntil(() => session != null && c.Ext.Active && session.Ext.Active, "extensión activa");

        string? atServer = null, atClient = null;
        session!.Ext.TextReceived += t => atServer = t;
        c.Ext.TextReceived += t => atClient = t;
        c.SendClipboardText("Hola ñandú €");
        session.SendClipboardText("Adiós 日本");
        WaitUntil(() => atServer == "Hola ñandú €" && atClient == "Adiós 日本", "portapapeles");

        // Archivos: carpeta con subcarpeta y archivo grande del cliente al servidor
        var src = Path.Combine(_tmp, "origen");
        Directory.CreateDirectory(Path.Combine(src, "Fotos", "vacía"));
        var big = new byte[3 * 1024 * 1024 + 123];
        new Random(5).NextBytes(big);
        File.WriteAllBytes(Path.Combine(src, "Fotos", "grande.bin"), big);
        File.WriteAllText(Path.Combine(src, "nota.txt"), "contenido");

        session.Ext.ReceiveFolder = Path.Combine(_tmp, "recibidos");
        session.Ext.ClipboardFolder = Path.Combine(_tmp, "clip");
        var batches = new ConcurrentQueue<ReceivedBatch>();
        session.Ext.BatchReceived += b => batches.Enqueue(b);

        await c.Ext.SendFilesAsync(new[] { Path.Combine(src, "Fotos"), Path.Combine(src, "nota.txt") }, FileTarget.Folder);
        WaitUntil(() => batches.Count == 1, "lote recibido");
        batches.TryDequeue(out var b1);
        Assert.Equal(2, b1!.FileCount);
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(_tmp, "recibidos", "Fotos", "grande.bin")));
        Assert.True(Directory.Exists(Path.Combine(_tmp, "recibidos", "Fotos", "vacía")));

        // Mismo envío otra vez: no sobrescribe, renombra
        await c.Ext.SendFilesAsync(new[] { Path.Combine(src, "nota.txt") }, FileTarget.Folder);
        WaitUntil(() => batches.Count == 1, "segundo lote");
        batches.TryDequeue(out var b2);
        Assert.EndsWith("nota (2).txt", b2!.TopLevelPaths[0]);

        // Servidor -> cliente hacia el portapapeles
        c.Ext.ClipboardFolder = Path.Combine(_tmp, "clipcli");
        ReceivedBatch? b3 = null;
        c.Ext.BatchReceived += b => b3 = b;
        await session.Ext.SendFilesAsync(new[] { Path.Combine(src, "nota.txt") }, FileTarget.Clipboard);
        WaitUntil(() => b3 != null, "lote al cliente");
        Assert.Equal(FileTarget.Clipboard, b3!.Target);
        Assert.Equal("contenido", File.ReadAllText(b3.TopLevelPaths[0]));
    }
}
