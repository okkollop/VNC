using System.Diagnostics;
using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Server;

/// <summary>Icono junto al reloj (con la IP al pasar el ratón), ventanas, portapapeles, chat y actualizaciones.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ClipboardBridge _clip;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<int, ChatForm> _chats = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10_000 };
    private StatusForm? _status;
    private OptionsForm? _options;
    private string? _balloonFolder;
    private DateTime _nextUpdateCheck = DateTime.Now.AddMinutes(2);

    public IServerBackend Backend { get; private set; }
    public bool ServiceMode { get; }

    public TrayApp(IServerBackend backend, bool serviceMode, bool showWindow)
    {
        Backend = backend;
        ServiceMode = serviceMode;
        _ui = SynchronizationContext.Current!;

        _tray = new NotifyIcon { Icon = AppIcon.Small(), Visible = true, Text = Brand.ServerName };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        var trayMenu = new ContextMenuStrip { Font = Ui.Body };
        trayMenu.Items.Add("…");
        trayMenu.Opening += (_, e) =>
        {
            // Regenerar al abrir: refleja las conexiones de ese momento
            var fresh = BuildMenu(forTray: true);
            trayMenu.Items.Clear();
            trayMenu.Items.AddRange(fresh.Items.Cast<ToolStripItem>().ToArray());
            e.Cancel = false;
        };
        _tray.ContextMenuStrip = trayMenu;
        _tray.BalloonTipClicked += (_, _) => { if (_balloonFolder != null) OpenFolder(_balloonFolder); else ShowStatus(); };

        _clip = new ClipboardBridge();
        HookBackend();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        UpdateTooltip();
        if (showWindow) ShowStatus();
    }

    private void HookBackend()
    {
        _clip.LocalText += t => Backend.SendClipboardText(t);
        _clip.LocalFiles += f => Backend.SendClipboardFiles(f);
        Backend.RemoteText += _clip.SetRemoteText;
        Backend.RemoteFiles += _clip.SetRemoteFiles;
        Backend.StatusChanged += () => _ui.Post(_ => UpdateTooltip(), null);
        Backend.Notify += (t, f) => _ui.Post(_ => Notify(t, f), null);
        Backend.ChatReceived += (id, from, text) => _ui.Post(_ => { var c = Chat(id, from); c.Add(from, text); c.Present(); }, null);
    }

    /// <summary>Texto al pasar el ratón por el icono: nombre, IP y puerto (máximo 127 caracteres).</summary>
    private void UpdateTooltip()
    {
        var st = Backend.Status;
        var ips = AppPaths.LocalIPv4().Take(2).ToList();
        var text = $"{Brand.Product}\nIP: {(ips.Count > 0 ? string.Join(", ", ips) : "sin red")}\nPuerto: {Backend.Config.Port}";
        if (!st.Listening) text += "\nNo disponible";
        else if (st.Sessions.Count > 0) text += $"\nConectados: {st.Sessions.Count}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void Tick()
    {
        UpdateTooltip(); // la IP puede cambiar
        if (ServiceMode)
        {
            // El servicio se actualiza solo; la bandeja se reinicia si su .exe ya es más nuevo
            if (Updater.DiskVersion() is { } v && v > new Version(Brand.Version.Major, Brand.Version.Minor, Brand.Version.Build))
                RestartSelf("--bandeja");
        }
        else if (DateTime.Now >= _nextUpdateCheck)
        {
            _nextUpdateCheck = DateTime.Now.AddHours(6);
            if (Backend.Config.AutoUpdate) _ = AutoUpdateAsync();
        }
    }

    /// <summary>Modo usuario: actualización silenciosa cuando no hay nadie conectado.</summary>
    private async Task AutoUpdateAsync()
    {
        try
        {
            if (Backend.Status.Sessions.Count > 0) { _nextUpdateCheck = DateTime.Now.AddMinutes(30); return; }
            if (await Updater.CheckAsync() is not { } m) return;
            var file = await Updater.DownloadAsync(m, AppKind.Servidor);
            if (Backend.Status.Sessions.Count > 0) { _nextUpdateCheck = DateTime.Now.AddMinutes(30); return; }
            Updater.Install(file);
            RestartSelf("--bandeja");
        }
        catch { }
    }

    private void RestartSelf(string args)
    {
        Program.ReleaseSingleInstance();
        Cleanup();
        Updater.Restart(args);
    }

    // ---------------- Menú ----------------

    public ContextMenuStrip BuildMenu(bool forTray)
    {
        var m = new ContextMenuStrip { Font = Ui.Body };
        if (forTray)
        {
            var open = m.Items.Add($"Abrir {Brand.ServerName}", null, (_, _) => ShowStatus());
            open.Font = Ui.BodyBold;
            m.Items.Add(new ToolStripSeparator());
        }
        m.Items.Add("Opciones…", null, (_, _) => ShowOptions());
        var chat = new ToolStripMenuItem("Chat…");
        var sessions = Backend.Status.Sessions.Where(s => s.FullFeatures).ToList();
        chat.Enabled = sessions.Count > 0;
        if (sessions.Count == 1) chat.Click += (_, _) => OpenChat(sessions[0].Id, sessions[0].Address);
        else foreach (var s in sessions) chat.DropDownItems.Add(s.Address, null, (_, _) => OpenChat(s.Id, s.Address));
        m.Items.Add(chat);
        m.Items.Add("Abrir carpeta de recibidos", null, (_, _) => OpenFolder(AppPaths.ReceivedFolder));
        var disc = m.Items.Add("Desconectar visores", null, (_, _) => { try { Backend.DisconnectAll(); } catch { } });
        disc.Enabled = Backend.Status.Sessions.Count > 0;
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Buscar actualizaciones…", null, async (_, _) => await Updater.CheckInteractiveAsync(null, AppKind.Servidor,
            beforeRestart: () => { Program.ReleaseSingleInstance(); Cleanup(); }));
        m.Items.Add("Acerca de…", null, (_, _) => ShowOptions(about: true));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(ServiceMode ? "Cerrar icono (el servicio sigue activo)" : "Detener servidor", null, (_, _) => Exit());
        return m;
    }

    // ---------------- Ventanas ----------------

    public void ShowStatus()
    {
        if (_status == null || _status.IsDisposed)
        {
            _status = new StatusForm(this);
            _status.FormClosed += (_, _) => _status = null;
        }
        _status.Show();
        if (_status.WindowState == FormWindowState.Minimized) _status.WindowState = FormWindowState.Normal;
        _status.Activate();
    }

    public void ShowOptions(bool about = false)
    {
        if (_options == null || _options.IsDisposed)
        {
            _options = new OptionsForm(this);
            _options.FormClosed += (_, _) => _options = null;
        }
        if (about) _options.ShowAbout();
        _options.Show();
        _options.Activate();
    }

    public void ChangePassword(IWin32Window? owner)
    {
        using var f = new PasswordForm(required: false);
        if (f.ShowDialog(owner) != DialogResult.OK) return;
        var cfg = Backend.Config.Clone();
        cfg.SetPassword(f.Password);
        Save(cfg, owner);
    }

    public bool Save(ServerConfig cfg, IWin32Window? owner)
    {
        try
        {
            Backend.SaveConfig(cfg);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "No se pudo guardar: " + ex.Message, Brand.ServerName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private ChatForm Chat(int id, string peer)
    {
        if (_chats.TryGetValue(id, out var c) && !c.IsDisposed) return c;
        c = new ChatForm($"Chat con {peer} — {Brand.Product}", text => Backend.SendChat(id, Environment.UserName, text));
        c.FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; c.Hide(); } };
        _chats[id] = c;
        return c;
    }

    public void OpenChat(int id, string address) => Chat(id, address).Present();

    private void Notify(string text, string? folder)
    {
        _balloonFolder = folder;
        _tray.ShowBalloonTip(4000, Brand.ServerName, text, ToolTipIcon.Info);
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ---------------- Modo servicio ----------------

    /// <summary>Activa o desactiva el modo servicio (pide permisos de administrador).</summary>
    public void SetServiceMode(bool on, IWin32Window? owner)
    {
        if (on == ServiceMode) return;
        if (on)
        {
            if (MessageBox.Show(owner,
                    "Se instalará InfollopAssist como servicio de Windows en Archivos de programa.\n\n" +
                    "Así se podrá ver y controlar también la pantalla de inicio de sesión, la de bloqueo y las ventanas de administrador, " +
                    "y arrancará siempre con el equipo.\n\nWindows pedirá permiso de administrador. ¿Continuar?",
                    Brand.ServerName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            Backend.Dispose(); // liberar el puerto para el servicio
            if (Server.ServiceMode.RunElevated("--instalar-servicio"))
            {
                try { ServerConfig.StartWithWindows = false; } catch { }
                Program.ReleaseSingleInstance();
                Cleanup();
                Process.Start(new ProcessStartInfo(Server.ServiceMode.InstalledExe, "--abrir") { UseShellExecute = false });
                Environment.Exit(0);
            }
            MessageBox.Show(owner, "No se pudo instalar el servicio (¿se canceló el permiso de administrador?).", Brand.ServerName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Program.ReleaseSingleInstance();
            Cleanup();
            Updater.Restart("--abrir"); // volver al modo usuario con el servidor en marcha
        }
        else
        {
            if (MessageBox.Show(owner,
                    "Se quitará el servicio. InfollopAssist seguirá funcionando como programa normal mientras haya un usuario con sesión iniciada.\n\n" +
                    "Windows pedirá permiso de administrador. ¿Continuar?",
                    Brand.ServerName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (!Server.ServiceMode.RunElevated("--quitar-servicio"))
            {
                MessageBox.Show(owner, "No se pudo quitar el servicio.", Brand.ServerName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try { ServerConfig.StartWithWindows = true; } catch { }
            Program.ReleaseSingleInstance();
            Cleanup();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--abrir") { UseShellExecute = false });
            Environment.Exit(0);
        }
    }

    private void Cleanup()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _clip.Dispose();
        try { Backend.Dispose(); } catch { }
    }

    private void Exit()
    {
        if (!ServiceMode && Backend.Status.Sessions.Count > 0 &&
            MessageBox.Show("Hay visores conectados. Si detienes el servidor se desconectarán. ¿Continuar?", Brand.ServerName,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        Cleanup();
        ExitThread();
    }
}
