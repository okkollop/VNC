using System.Diagnostics;
using System.Net.Sockets;
using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Server;

/// <summary>Aplicación de bandeja: arranca el servidor y enlaza portapapeles y archivos con los clientes.</summary>
internal sealed class ServerTray : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _ips = new() { Enabled = false };
    private readonly ToolStripMenuItem _conns = new() { Enabled = false };
    private readonly ServerConfig _cfg;
    private readonly WindowsDesktop _desktop = new();
    private readonly RfbServerOptions _opt;
    private readonly ClipboardBridge _clip;
    private readonly SynchronizationContext _ui;
    private RfbServer? _server;
    private string? _lastBalloonFolder;

    public ServerTray(ServerConfig cfg)
    {
        _cfg = cfg;
        _ui = SynchronizationContext.Current!;
        _opt = new RfbServerOptions
        {
            Name = Environment.MachineName,
            Password = () => _cfg.Password ?? Guid.NewGuid().ToString(), // sin contraseña nadie entra
            ViewOnly = cfg.ViewOnly,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(_ips);
        menu.Items.Add(_conns);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configuración…", null, (_, _) => ShowSettings());
        menu.Items.Add("Abrir carpeta de recibidos", null, (_, _) => OpenFolder(AppPaths.ReceivedFolder));
        menu.Items.Add("Desconectar a todos", null, (_, _) => _server?.DisconnectAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Exit());
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon { Icon = AppIcon.Small(), ContextMenuStrip = menu, Visible = true, Text = "InfollopAssist Servidor" };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => { if (_lastBalloonFolder != null) OpenFolder(_lastBalloonFolder); };

        _clip = new ClipboardBridge();
        _clip.LocalText += t => { foreach (var s in Sessions()) s.SendClipboardText(t); };
        _clip.LocalFiles += OnLocalFiles;

        AppPaths.CleanClipboardFolder();
        StartServer();
    }

    private IReadOnlyList<ServerSession> Sessions() => _server?.Sessions ?? Array.Empty<ServerSession>();

    private void StartServer()
    {
        _server?.Dispose();
        _server = new RfbServer(_desktop, _opt);
        _server.SessionStarted += s => _ui.Post(_ => OnSessionStarted(s), null);
        _server.SessionEnded += s => _ui.Post(_ => Notify($"Desconectado: {s.RemoteAddress}"), null);
        _server.AuthFailed += ip => _ui.Post(_ => Notify($"Contraseña incorrecta desde {ip}", ToolTipIcon.Warning), null);
        try
        {
            _server.Start(_cfg.Port);
            _tray.Text = $"InfollopAssist Servidor — puerto {_cfg.Port}";
        }
        catch (SocketException ex)
        {
            _tray.Text = "InfollopAssist Servidor — sin escuchar";
            MessageBox.Show($"No se pudo abrir el puerto {_cfg.Port}: {ex.Message}\n\n¿Hay otro VNC (p. ej. RealVNC) usando ese puerto? " +
                            "Ciérralo o cambia el puerto en Configuración.", "InfollopAssist Servidor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSessionStarted(ServerSession s)
    {
        Notify($"Conectado: {s.RemoteAddress}");
        s.TextReceived += _clip.SetRemoteText;
        s.Ext.TextReceived += _clip.SetRemoteText;
        s.Ext.ReceiveFolder = AppPaths.ReceivedFolder;
        s.Ext.ClipboardFolder = AppPaths.ClipboardFolder;
        s.Ext.BatchReceived += b =>
        {
            if (b.Target == FileTarget.Clipboard) _clip.SetRemoteFiles(b.TopLevelPaths);
            else _ui.Post(_ => Notify($"Recibidos {b.FileCount} archivo(s), {AppPaths.FormatBytes(b.TotalBytes)} en Descargas\\InfollopAssist", folder: AppPaths.ReceivedFolder), null);
        };
        s.Ext.TransferFailed += msg => _ui.Post(_ => Notify("Transferencia fallida: " + msg, ToolTipIcon.Warning), null);
    }

    private void OnLocalFiles(string[] files)
    {
        var targets = Sessions().Where(s => s.Ext.Active).ToList();
        if (targets.Count == 0) return;
        _ = Task.Run(async () =>
        {
            long size;
            try { size = ExtChannel.Measure(files); }
            catch { return; }
            if (_cfg.MaxClipboardFilesMB > 0 && size > _cfg.MaxClipboardFilesMB * 1024L * 1024)
            {
                _ui.Post(_ => Notify($"No se envían al otro equipo: {AppPaths.FormatBytes(size)} supera el límite del portapapeles " +
                                     $"({_cfg.MaxClipboardFilesMB} MB).", ToolTipIcon.Info), null);
                return;
            }
            foreach (var s in targets)
            {
                try { await s.Ext.SendFilesAsync(files, FileTarget.Clipboard); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException) { }
            }
        });
    }

    private void RefreshMenu()
    {
        var n = Sessions().Count;
        _status.Text = _server is { Port: > 0 } ? $"Escuchando en el puerto {_server.Port}" : "No está escuchando";
        var ips = AppPaths.LocalIPv4().ToList();
        _ips.Text = "IP de este equipo: " + (ips.Count > 0 ? string.Join(", ", ips) : "—");
        _conns.Text = n == 0 ? "Sin conexiones" : "Conectados: " + string.Join(", ", Sessions().Select(s => s.RemoteAddress));
    }

    private void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info, string? folder = null)
    {
        _lastBalloonFolder = folder;
        _tray.ShowBalloonTip(4000, "InfollopAssist Servidor", text, icon);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void ShowSettings()
    {
        int oldPort = _cfg.Port;
        using var f = new SettingsForm(_cfg, firstRun: false);
        if (f.ShowDialog() != DialogResult.OK) return;
        _opt.ViewOnly = _cfg.ViewOnly;
        if (_cfg.Port != oldPort) StartServer();
    }

    private void Exit()
    {
        _server?.Dispose();
        _clip.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _desktop.Dispose();
        ExitThread();
    }
}
