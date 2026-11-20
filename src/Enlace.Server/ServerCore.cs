using System.Net.Sockets;
using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Server;

/// <summary>Una conexión activa, tal como se muestra en la ventana de estado.</summary>
internal sealed record SessionInfo(int Id, string Address, DateTime Since, bool FullFeatures);

internal sealed record ServerStatus(bool Listening, int Port, string? Error, bool ServiceMode, IReadOnlyList<SessionInfo> Sessions, bool HasPassword);

/// <summary>
/// Lo que la bandeja (interfaz) necesita del servidor. En modo usuario lo implementa <see cref="ServerCore"/>
/// directamente; en modo servicio, <see cref="PipeBackend"/> habla con el agente que corre como SYSTEM.
/// </summary>
internal interface IServerBackend : IDisposable
{
    event Action? StatusChanged;
    event Action<string, string?>? Notify;          // texto, carpeta a abrir al pulsar (opcional)
    event Action<int, string, string>? ChatReceived; // sesión, nombre, texto
    event Action<string>? RemoteText;
    event Action<IReadOnlyList<string>>? RemoteFiles;

    ServerStatus Status { get; }
    ServerConfig Config { get; }
    void SaveConfig(ServerConfig cfg);
    void DisconnectAll();
    void SendChat(int sessionId, string from, string text);
    void SendClipboardText(string text);
    void SendClipboardFiles(string[] paths);
}

/// <summary>El servidor propiamente dicho: escucha, sesiones, archivos, chat.</summary>
internal sealed class ServerCore : IServerBackend
{
    private readonly WindowsDesktop _desktop;
    private readonly RfbServerOptions _opt;
    private readonly bool _serviceMode;
    private readonly Func<string> _receivedFolder, _clipboardFolder;
    private readonly Dictionary<ServerSession, int> _ids = new();
    private RfbServer? _server;
    private string? _error;
    private int _nextId;

    public ServerCore(bool serviceMode, Func<string> receivedFolder, Func<string> clipboardFolder)
    {
        _serviceMode = serviceMode;
        _receivedFolder = receivedFolder;
        _clipboardFolder = clipboardFolder;
        _desktop = new WindowsDesktop { AllowDesktopSwitch = serviceMode };
        Config = ServerConfig.Load();
        _opt = new RfbServerOptions
        {
            Name = Environment.MachineName,
            Password = () => Config.Password ?? Guid.NewGuid().ToString(), // sin contraseña nadie entra
            ViewOnly = Config.ViewOnly,
        };
        Updater.BaseUrl = Config.UpdateUrl;
        Start();
    }

    public event Action? StatusChanged;
    public event Action<string, string?>? Notify;
    public event Action<int, string, string>? ChatReceived;
    public event Action<string>? RemoteText;
    public event Action<IReadOnlyList<string>>? RemoteFiles;

    public ServerConfig Config { get; private set; }

    private List<ServerSession> Sessions() => _server?.Sessions.ToList() ?? new();

    public ServerStatus Status
    {
        get
        {
            List<SessionInfo> list;
            lock (_ids)
                list = Sessions().Select(s => new SessionInfo(_ids.GetValueOrDefault(s), s.RemoteAddress.ToString(), s.ConnectedAt, s.Ext.Active)).ToList();
            return new ServerStatus(_server is { Port: > 0 } && _error == null, Config.Port, _error, _serviceMode, list, Config.Password != null);
        }
    }

    /// <summary>(Re)abre el puerto. Si está ocupado, reintenta cada pocos segundos.</summary>
    private void Start()
    {
        _server?.Dispose();
        var srv = new RfbServer(_desktop, _opt);
        srv.SessionStarted += OnSessionStarted;
        srv.SessionEnded += s =>
        {
            lock (_ids) _ids.Remove(s);
            Notify?.Invoke($"Desconectado: {s.RemoteAddress}", null);
            StatusChanged?.Invoke();
        };
        srv.AuthFailed += ip => Notify?.Invoke($"Contraseña incorrecta desde {ip}", null);
        _server = srv;
        try
        {
            srv.Start(Config.Port);
            _error = null;
        }
        catch (SocketException ex)
        {
            _error = $"No se puede usar el puerto {Config.Port}: {ex.Message}";
            var port = Config.Port;
            _ = Task.Delay(5000).ContinueWith(_ => { if (_server == srv && Config.Port == port) Start(); });
        }
        StatusChanged?.Invoke();
    }

    private void OnSessionStarted(ServerSession s)
    {
        int id;
        lock (_ids) _ids[s] = id = Interlocked.Increment(ref _nextId);
        s.TextReceived += t => RemoteText?.Invoke(t);
        s.Ext.TextReceived += t => RemoteText?.Invoke(t);
        s.Ext.ChatReceived += (from, text) => ChatReceived?.Invoke(id, from, text);
        s.Ext.Activated += () => StatusChanged?.Invoke();
        try
        {
            s.Ext.ReceiveFolder = _receivedFolder();
            s.Ext.ClipboardFolder = _clipboardFolder();
        }
        catch { }
        s.Ext.BatchReceived += b =>
        {
            if (b.Target == FileTarget.Clipboard) RemoteFiles?.Invoke(b.TopLevelPaths);
            else Notify?.Invoke($"Recibidos {b.FileCount} archivo(s), {AppPaths.FormatBytes(b.TotalBytes)}, en Descargas\\InfollopAssist", s.Ext.ReceiveFolder);
        };
        s.Ext.TransferFailed += m => Notify?.Invoke("Transferencia fallida: " + m, null);
        Notify?.Invoke($"Conectado: {s.RemoteAddress}", null);
        StatusChanged?.Invoke();
    }

    public void SaveConfig(ServerConfig cfg)
    {
        bool portChanged = cfg.Port != Config.Port;
        cfg.Save();
        Config = cfg;
        _opt.ViewOnly = cfg.ViewOnly;
        Updater.BaseUrl = cfg.UpdateUrl;
        if (portChanged) Start();
        StatusChanged?.Invoke();
    }

    public void DisconnectAll() => _server?.DisconnectAll();

    public void SendChat(int sessionId, string from, string text)
    {
        ServerSession? s;
        lock (_ids) s = _ids.FirstOrDefault(kv => kv.Value == sessionId).Key;
        if (s == null) throw new InvalidOperationException("Esa conexión ya no existe");
        s.Ext.SendChat(from, text);
    }

    public void SendClipboardText(string text)
    {
        foreach (var s in Sessions()) s.SendClipboardText(text);
    }

    public void SendClipboardFiles(string[] paths)
    {
        var targets = Sessions().Where(s => s.Ext.Active).ToList();
        if (targets.Count == 0) return;
        _ = Task.Run(async () =>
        {
            long size;
            try { size = ExtChannel.Measure(paths); }
            catch { return; }
            if (Config.MaxClipboardFilesMB > 0 && size > Config.MaxClipboardFilesMB * 1024L * 1024)
            {
                Notify?.Invoke($"No se envían: {AppPaths.FormatBytes(size)} supera el límite de {Config.MaxClipboardFilesMB} MB", null);
                return;
            }
            foreach (var s in targets)
            {
                try { await s.Ext.SendFilesAsync(paths, FileTarget.Clipboard); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException) { }
            }
        });
    }

    public bool HasSessions => Sessions().Count > 0;

    public WindowsDesktop Desktop => _desktop;

    public void Dispose()
    {
        _server?.Dispose();
        _desktop.Dispose();
    }
}
