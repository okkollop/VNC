using System.Text;

namespace Enlace.Protocol;

public enum FileTarget : byte
{
    /// <summary>Se guardan en la carpeta de recibidos del otro equipo.</summary>
    Folder = 0,
    /// <summary>Se guardan en temporal y se ponen en el portapapeles del otro equipo para pegarlos.</summary>
    Clipboard = 1,
}

public sealed record ReceivedBatch(FileTarget Target, IReadOnlyList<string> TopLevelPaths, int FileCount, long TotalBytes);

public readonly record struct TransferProgress(bool Sending, long Done, long Total, int Files);

/// <summary>
/// Extensión propia de Enlace sobre RFB: portapapeles en UTF-8 y transferencia de archivos/carpetas.
/// Sólo se activa si el cliente anuncia <see cref="PseudoEncoding"/> y el servidor responde con Hello,
/// así un visor VNC estándar nunca recibe estos mensajes.
///
/// Trama (ambos sentidos): [u8 196][u8 subtipo][u16 0][u32 longitud][carga]
/// </summary>
public sealed class ExtChannel : IDisposable
{
    public const byte MessageType = 196;
    public const int PseudoEncoding = -0x454E4C31; // "ENL1"
    public const uint Version = 1;
    private const int ChunkSize = 64 * 1024;
    private const int MaxPayload = 1024 * 1024 + 64;

    private enum Sub : byte { Hello = 1, Text = 2, BatchStart = 3, Dir = 4, FileBegin = 5, FileData = 6, FileEnd = 7, BatchEnd = 8, BatchAbort = 9 }

    private readonly Action<byte[]> _send;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private uint _nextBatch, _nextFile;

    private readonly Dictionary<uint, InBatch> _inBatches = new();
    private readonly Dictionary<uint, InFile> _inFiles = new();

    public ExtChannel(Action<byte[]> send) => _send = send;

    public bool Active { get; private set; }

    /// <summary>Carpeta donde se guardan los archivos enviados con <see cref="FileTarget.Folder"/>.</summary>
    public string ReceiveFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Enlace", "Recibidos");

    /// <summary>Raíz temporal para archivos que van al portapapeles.</summary>
    public string ClipboardFolder { get; set; } = Path.Combine(Path.GetTempPath(), "Enlace", "Portapapeles");

    public event Action? Activated;
    public event Action<string>? TextReceived;
    public event Action<ReceivedBatch>? BatchReceived;
    public event Action<TransferProgress>? Progress;
    public event Action<string>? TransferFailed;

    internal void Activate()
    {
        if (Active) return;
        Active = true;
        Activated?.Invoke();
    }

    internal void SendHello()
    {
        var p = new PacketWriter(8).U32(Version);
        Send(Sub.Hello, p);
    }

    private void Send(Sub sub, PacketWriter payload)
    {
        var w = new PacketWriter(payload.Length + 8);
        w.U8(MessageType).U8((byte)sub).U16(0).U32((uint)payload.Length).Bytes(payload.Span);
        _send(w.ToArray());
    }

    public void SendText(string text)
    {
        if (!Active) throw new InvalidOperationException("Extensión no activa");
        var b = Encoding.UTF8.GetBytes(text);
        if (b.Length > MaxPayload - 16) b = b.AsSpan(0, MaxPayload - 16).ToArray();
        Send(Sub.Text, new PacketWriter(b.Length).Bytes(b));
    }

    private static PacketWriter Str(PacketWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return w.U16(b.Length).Bytes(b);
    }

    /// <summary>Calcula el total de bytes de una selección (archivos y carpetas).</summary>
    public static long Measure(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p)) total += new FileInfo(p).Length;
            else if (Directory.Exists(p))
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    total += new FileInfo(f).Length;
        }
        return total;
    }

    /// <summary>Envía archivos y carpetas (recursivo). Las transferencias se serializan.</summary>
    public async Task SendFilesAsync(IReadOnlyList<string> paths, FileTarget target, CancellationToken ct = default)
    {
        if (!Active) throw new InvalidOperationException("El otro equipo no admite transferencia de archivos");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        uint batch = Interlocked.Increment(ref _nextBatch);
        bool started = false;
        try
        {
            await Task.Run(() =>
            {
                var dirs = new List<string>();
                var files = new List<(string Full, string Rel, long Size)>();
                foreach (var raw in paths)
                {
                    var p = Path.TrimEndingDirectorySeparator(raw);
                    var name = Path.GetFileName(p);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (File.Exists(p)) files.Add((p, name, new FileInfo(p).Length));
                    else if (Directory.Exists(p))
                    {
                        dirs.Add(name);
                        foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories))
                            dirs.Add(name + "/" + Path.GetRelativePath(p, d).Replace('\\', '/'));
                        foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                            files.Add((f, name + "/" + Path.GetRelativePath(p, f).Replace('\\', '/'), new FileInfo(f).Length));
                    }
                }
                long total = files.Sum(f => f.Size);

                Send(Sub.BatchStart, new PacketWriter().U32(batch).U8((byte)target).U32((uint)files.Count).U64((ulong)total));
                started = true;
                foreach (var d in dirs) Send(Sub.Dir, Str(new PacketWriter().U32(batch), d));

                long done = 0;
                var buf = new byte[ChunkSize];
                var lastReport = Environment.TickCount64;
                foreach (var f in files)
                {
                    token.ThrowIfCancellationRequested();
                    uint id = Interlocked.Increment(ref _nextFile);
                    Send(Sub.FileBegin, Str(new PacketWriter().U32(batch).U32(id), f.Rel).U64((ulong)f.Size));
                    using (var fs = new FileStream(f.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize))
                    {
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            Send(Sub.FileData, new PacketWriter(n + 8).U32(id).Bytes(buf.AsSpan(0, n)));
                            done += n;
                            if (Environment.TickCount64 - lastReport > 250)
                            {
                                lastReport = Environment.TickCount64;
                                Progress?.Invoke(new TransferProgress(true, done, total, files.Count));
                            }
                        }
                    }
                    Send(Sub.FileEnd, new PacketWriter().U32(id));
                }
                Send(Sub.BatchEnd, new PacketWriter().U32(batch));
                Progress?.Invoke(new TransferProgress(true, total, total, files.Count));
            }, token).ConfigureAwait(false);
        }
        catch
        {
            if (started) { try { Send(Sub.BatchAbort, new PacketWriter().U32(batch)); } catch { } }
            throw;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ---------------- Recepción ----------------

    private sealed class InBatch
    {
        public required string Root;
        public required FileTarget Target;
        public required long Total;
        public readonly Dictionary<string, string> TopMap = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> TopPaths = new();
        public int Files;
        public long Received;
        public long LastReport;
    }

    private sealed class InFile
    {
        public required uint Batch;
        public required FileStream Stream;
        public required string Path;
    }

    /// <summary>Lee un mensaje de la extensión (el byte de tipo ya se ha consumido).</summary>
    internal void HandleIncoming(Stream s)
    {
        var sub = (Sub)s.ReadU8();
        s.Skip(2);
        uint len = s.ReadU32();
        if (len > MaxPayload) throw new RfbException("Mensaje de extensión demasiado grande");
        var payload = s.ReadBytes((int)len);
        try
        {
            Dispatch(sub, payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RfbException)
        {
            TransferFailed?.Invoke(ex.Message);
        }
    }

    private static uint U32(byte[] p, ref int o)
    {
        if (o + 4 > p.Length) throw new RfbException("Mensaje truncado");
        uint v = (uint)(p[o] << 24 | p[o + 1] << 16 | p[o + 2] << 8 | p[o + 3]);
        o += 4;
        return v;
    }

    private static ulong U64(byte[] p, ref int o) => ((ulong)U32(p, ref o) << 32) | U32(p, ref o);

    private static string ReadStr(byte[] p, ref int o)
    {
        if (o + 2 > p.Length) throw new RfbException("Mensaje truncado");
        int n = p[o] << 8 | p[o + 1];
        o += 2;
        if (o + n > p.Length) throw new RfbException("Mensaje truncado");
        var str = Encoding.UTF8.GetString(p, o, n);
        o += n;
        return str;
    }

    private void Dispatch(Sub sub, byte[] p)
    {
        int o = 0;
        switch (sub)
        {
            case Sub.Hello:
                Activate();
                break;

            case Sub.Text:
                TextReceived?.Invoke(Encoding.UTF8.GetString(p));
                break;

            case Sub.BatchStart:
            {
                uint id = U32(p, ref o);
                if (o >= p.Length) throw new RfbException("Mensaje truncado");
                var target = p[o++] == 1 ? FileTarget.Clipboard : FileTarget.Folder;
                U32(p, ref o); // número de archivos (informativo)
                long total = (long)U64(p, ref o);
                string root = target == FileTarget.Clipboard
                    ? Path.Combine(ClipboardFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{id}")
                    : ReceiveFolder;
                Directory.CreateDirectory(root);
                _inBatches[id] = new InBatch { Root = root, Target = target, Total = total };
                break;
            }

            case Sub.Dir:
            {
                uint id = U32(p, ref o);
                var b = GetBatch(id);
                Directory.CreateDirectory(MapPath(b, ReadStr(p, ref o)));
                break;
            }

            case Sub.FileBegin:
            {
                uint id = U32(p, ref o);
                uint fid = U32(p, ref o);
                var b = GetBatch(id);
                var path = MapPath(b, ReadStr(p, ref o));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize);
                _inFiles[fid] = new InFile { Batch = id, Stream = fs, Path = path };
                b.Files++;
                break;
            }

            case Sub.FileData:
            {
                uint fid = U32(p, ref o);
                if (!_inFiles.TryGetValue(fid, out var f)) throw new RfbException("Archivo desconocido");
                f.Stream.Write(p, o, p.Length - o);
                if (_inBatches.TryGetValue(f.Batch, out var b))
                {
                    b.Received += p.Length - o;
                    if (Environment.TickCount64 - b.LastReport > 250)
                    {
                        b.LastReport = Environment.TickCount64;
                        Progress?.Invoke(new TransferProgress(false, b.Received, b.Total, b.Files));
                    }
                }
                break;
            }

            case Sub.FileEnd:
            {
                uint fid = U32(p, ref o);
                if (_inFiles.Remove(fid, out var f)) f.Stream.Dispose();
                break;
            }

            case Sub.BatchEnd:
            {
                uint id = U32(p, ref o);
                if (!_inBatches.Remove(id, out var b)) break;
                CloseFilesOf(id, delete: false);
                Progress?.Invoke(new TransferProgress(false, b.Received, b.Total, b.Files));
                BatchReceived?.Invoke(new ReceivedBatch(b.Target, b.TopPaths, b.Files, b.Received));
                break;
            }

            case Sub.BatchAbort:
            {
                uint id = U32(p, ref o);
                CloseFilesOf(id, delete: true);
                if (_inBatches.Remove(id, out var b))
                {
                    foreach (var top in b.TopPaths) TryDelete(top);
                    TransferFailed?.Invoke("El otro equipo canceló el envío de archivos");
                }
                break;
            }

            default:
                break; // subtipos futuros: se ignoran
        }
    }

    private InBatch GetBatch(uint id) =>
        _inBatches.TryGetValue(id, out var b) ? b : throw new RfbException("Lote de archivos desconocido");

    private void CloseFilesOf(uint batch, bool delete)
    {
        foreach (var kv in _inFiles.Where(kv => kv.Value.Batch == batch).ToList())
        {
            kv.Value.Stream.Dispose();
            if (delete) TryDelete(kv.Value.Path);
            _inFiles.Remove(kv.Key);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static readonly char[] BadChars = "<>:\"|?*\\".ToCharArray();

    /// <summary>
    /// Convierte una ruta relativa recibida en una ruta local segura dentro del lote.
    /// Rechaza "..", rutas absolutas y caracteres no válidos (evita escribir fuera de la carpeta).
    /// </summary>
    internal static string[] SanitizeRelative(string rel)
    {
        var segs = rel.Split('/');
        if (segs.Length == 0 || segs.Length > 64) throw new RfbException("Ruta no válida");
        foreach (var s in segs)
        {
            if (s.Length == 0 || s == "." || s == ".." || s.Length > 255) throw new RfbException($"Ruta no válida: {rel}");
            if (s.IndexOfAny(BadChars) >= 0 || s.Any(char.IsControl)) throw new RfbException($"Ruta no válida: {rel}");
            if (s.EndsWith('.') || s.EndsWith(' ')) throw new RfbException($"Ruta no válida: {rel}");
        }
        return segs;
    }

    private static string MapPath(InBatch b, string rel)
    {
        var segs = SanitizeRelative(rel);
        if (!b.TopMap.TryGetValue(segs[0], out var top))
        {
            top = UniqueName(b.Root, segs[0]);
            b.TopMap[segs[0]] = top;
            b.TopPaths.Add(Path.Combine(b.Root, top));
        }
        segs[0] = top;
        var full = Path.GetFullPath(Path.Combine(b.Root, Path.Combine(segs)));
        var root = Path.GetFullPath(b.Root);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new RfbException("Ruta fuera de la carpeta de destino");
        return full;
    }

    private static string UniqueName(string root, string name)
    {
        if (!File.Exists(Path.Combine(root, name)) && !Directory.Exists(Path.Combine(root, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var cand = $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(root, cand)) && !Directory.Exists(Path.Combine(root, cand))) return cand;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var f in _inFiles.Values)
        {
            f.Stream.Dispose();
            TryDelete(f.Path);
        }
        _inFiles.Clear();
    }
}
