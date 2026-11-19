using System.Diagnostics;
using System.Media;
using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Client;

internal sealed class ViewerForm : Form
{
    private readonly RfbClient _c;
    private readonly ClientConfig _cfg;
    private readonly string _hostLabel;
    private readonly RemoteSurface _surface;
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Black };
    private readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, TabStop = false };
    private readonly ToolStripButton _fitBtn = new("Ajustar a la ventana") { CheckOnClick = true };
    private readonly ToolStripButton _fullBtn = new("Pantalla completa");
    private readonly ToolStripButton _syncBtn = new("Archivos por portapapeles") { CheckOnClick = true };
    private readonly StatusStrip _statusBar = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ClipboardBridge _clip;
    private readonly KeyboardHook _hook;
    private bool _fullscreen;
    private FormWindowState _prevState;
    private string? _disconnectError;
    private bool _closing;

    public ViewerForm(RfbClient client, ClientConfig cfg, string hostLabel)
    {
        _c = client;
        _cfg = cfg;
        _hostLabel = hostLabel;
        Text = $"{client.DesktopName} ({hostLabel}) — Enlace";
        Icon = AppIcon.Get();
        KeyPreview = false;
        AllowDrop = true;

        _surface = new RemoteSurface(client) { AllowDrop = true };
        _surface.RemoteSizeChanged += ApplyLayout;
        _hook = new KeyboardHook(_surface.SendKey);

        _fitBtn.Checked = cfg.FitToWindow;
        _fitBtn.CheckedChanged += (_, _) => { _cfg.FitToWindow = _fitBtn.Checked; _cfg.Save(); ApplyLayout(); };
        _fullBtn.Click += (_, _) => ToggleFullscreen();
        _syncBtn.Checked = cfg.SyncClipboardFiles;
        _syncBtn.ToolTipText = "Al copiar archivos en un equipo, se pueden pegar en el otro";
        _syncBtn.CheckedChanged += (_, _) => { _cfg.SyncClipboardFiles = _syncBtn.Checked; _cfg.Save(); };
        var sendBtn = new ToolStripButton("Enviar archivos…");
        sendBtn.Click += (_, _) => PickAndSend();
        var folderBtn = new ToolStripButton("Carpeta de recibidos");
        folderBtn.Click += (_, _) => OpenFolder(AppPaths.ReceivedFolder);
        var cadBtn = new ToolStripButton("Ctrl+Alt+Supr") { ToolTipText = "Envía Ctrl+Alt+Supr (Windows sólo lo permite a servicios del sistema: puede no tener efecto)" };
        cadBtn.Click += (_, _) => SendCombo(KeyMap.XK_Control_L, KeyMap.XK_Alt_L, 0xFFFF);
        var winBtn = new ToolStripButton("Tecla Windows");
        winBtn.Click += (_, _) => SendCombo(KeyMap.XK_Super_L);
        foreach (ToolStripItem i in new ToolStripItem[] { _fitBtn, _fullBtn, new ToolStripSeparator(), sendBtn, folderBtn, _syncBtn, new ToolStripSeparator(), winBtn, cadBtn })
            _bar.Items.Add(i);
        foreach (ToolStripItem i in _bar.Items) i.MouseUp += (_, _) => _surface.Focus();

        _statusBar.Items.Add(_status);
        _scroll.Controls.Add(_surface);
        Controls.Add(_scroll);
        Controls.Add(_bar);
        Controls.Add(_statusBar);

        // Tamaño inicial: la pantalla remota cabiendo en el 90 % del área de trabajo
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int chromeH = 120;
        double s = Math.Min(1.0, Math.Min(wa.Width * 0.9 / Math.Max(1, client.Width), (wa.Height * 0.9 - chromeH) / Math.Max(1, client.Height)));
        ClientSize = new Size(Math.Max(640, (int)(client.Width * s)), Math.Max(400, (int)(client.Height * s) + chromeH / 2));
        StartPosition = FormStartPosition.CenterScreen;
        ApplyLayout();

        // Portapapeles
        _clip = new ClipboardBridge();
        _clip.LocalText += t => _c.SendClipboardText(t);
        _clip.LocalFiles += OnLocalFiles;
        _c.TextReceived += _clip.SetRemoteText;
        _c.Ext.TextReceived += _clip.SetRemoteText;

        // Archivos
        _c.Ext.ReceiveFolder = AppPaths.ReceivedFolder;
        _c.Ext.ClipboardFolder = AppPaths.ClipboardFolder;
        _c.Ext.BatchReceived += b => UI(() =>
        {
            if (b.Target == FileTarget.Clipboard)
            {
                _clip.SetRemoteFiles(b.TopLevelPaths);
                SetStatus($"{b.FileCount} archivo(s) del otro equipo listos para pegar ({AppPaths.FormatBytes(b.TotalBytes)})");
            }
            else SetStatus($"Recibidos {b.FileCount} archivo(s) en Descargas\\Enlace");
        });
        _c.Ext.Progress += p => UI(() => SetStatus(
            $"{(p.Sending ? "Enviando" : "Recibiendo")} {p.Files} archivo(s): {AppPaths.FormatBytes(p.Done)} de {AppPaths.FormatBytes(p.Total)}" +
            (p.Total > 0 ? $" ({p.Done * 100 / p.Total} %)" : "")));
        _c.Ext.TransferFailed += m => UI(() => SetStatus("Transferencia fallida: " + m));
        _c.Ext.Activated += () => UI(UpdateDefaultStatus);

        _c.Bell += () => UI(() => SystemSounds.Beep.Play());
        _c.Disconnected += ex => UI(() =>
        {
            _disconnectError = ex?.Message;
            if (!_closing) Close();
        });

        _surface.DragEnter += OnDragEnter;
        _surface.DragDrop += OnDragDrop;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        UpdateDefaultStatus();
    }

    /// <summary>Mensaje de error si la conexión se cortó sola.</summary>
    public string? DisconnectError => _disconnectError;

    private void UI(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void SetStatus(string s) => _status.Text = s;

    private void UpdateDefaultStatus() =>
        SetStatus($"Conectado a {_hostLabel} — {_c.Width}×{_c.Height}" +
                  (_c.Ext.Active ? "" : "   (servidor VNC estándar: sin transferencia de archivos)"));

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _c.Start(); // a partir de aquí llegan imagen y eventos
        _surface.Focus();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _hook.Install();
        _surface.Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        _hook.Uninstall();
        _surface.ReleaseAll();
        base.OnDeactivate(e);
    }

    private void ApplyLayout()
    {
        _surface.Fit = _fitBtn.Checked;
        if (_fitBtn.Checked)
        {
            _scroll.AutoScroll = false;
            _surface.Dock = DockStyle.Fill;
        }
        else
        {
            _surface.Dock = DockStyle.None;
            _surface.Location = Point.Empty;
            _surface.Size = _surface.RemoteSize;
            _scroll.AutoScroll = true;
        }
        UpdateDefaultStatus();
        _surface.Invalidate();
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _prevState = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _statusBar.Visible = false;
            _fullBtn.Text = "Salir de pantalla completa";
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _prevState;
            _statusBar.Visible = true;
            _fullBtn.Text = "Pantalla completa";
        }
        _surface.Focus();
    }

    private void SendCombo(params uint[] keys)
    {
        foreach (var k in keys) _c.SendKey(k, true);
        for (int i = keys.Length - 1; i >= 0; i--) _c.SendKey(keys[i], false);
        _surface.Focus();
    }

    // ---------------- Archivos ----------------

    private bool CheckExt()
    {
        if (_c.Ext.Active) return true;
        MessageBox.Show(this, "El equipo remoto no es un servidor Enlace: sólo se puede ver, controlar y copiar texto.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void PickAndSend()
    {
        if (!CheckExt()) return;
        using var d = new OpenFileDialog { Multiselect = true, Title = "Archivos para enviar al equipo remoto (Descargas\\Enlace)" };
        if (d.ShowDialog(this) == DialogResult.OK) Send(d.FileNames, FileTarget.Folder);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && CheckExt())
            Send(files, FileTarget.Folder);
    }

    private void OnLocalFiles(string[] files)
    {
        if (!_cfg.SyncClipboardFiles || !_c.Ext.Active) return;
        _ = Task.Run(() =>
        {
            long size;
            try { size = ExtChannel.Measure(files); }
            catch { return; }
            if (_cfg.MaxClipboardFilesMB > 0 && size > _cfg.MaxClipboardFilesMB * 1024L * 1024)
            {
                UI(() => SetStatus($"No se copian al otro equipo: {AppPaths.FormatBytes(size)} supera el límite de " +
                                   $"{_cfg.MaxClipboardFilesMB} MB. Usa «Enviar archivos…» o arrástralos a la ventana."));
                return;
            }
            Send(files, FileTarget.Clipboard);
        });
    }

    private async void Send(IReadOnlyList<string> files, FileTarget target)
    {
        try
        {
            await _c.Ext.SendFilesAsync(files, target);
            UI(() => SetStatus(target == FileTarget.Clipboard
                ? "Archivos copiados: ya puedes pegarlos en el equipo remoto"
                : "Archivos enviados a Descargas\\Enlace del equipo remoto"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            UI(() => SetStatus("No se pudo enviar: " + ex.Message));
        }
        catch (OperationCanceledException) { }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _hook.Dispose();
        _c.TextReceived -= _clip.SetRemoteText;
        _c.Ext.TextReceived -= _clip.SetRemoteText;
        _clip.Dispose();
        _c.Dispose();
        base.OnFormClosing(e);
    }
}
