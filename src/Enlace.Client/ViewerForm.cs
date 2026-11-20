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
    private readonly FloatingBar _bar = new();
    private readonly Toast _toast = new();
    private readonly System.Windows.Forms.Timer _track = new() { Interval = 120 };
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
        Text = $"{client.DesktopName} ({hostLabel}) — InfollopAssist";
        Icon = AppIcon.Get();
        KeyPreview = false;
        AllowDrop = true;

        _surface = new RemoteSurface(client) { AllowDrop = true };
        _surface.RemoteSizeChanged += ApplyLayout;
        _hook = new KeyboardHook(_surface.SendKey);

        _bar.SetItems(new[]
        {
            new BarItem { Icon = BarIcon.Fullscreen, Tip = "Pantalla completa", Click = ToggleFullscreen,
                          DynamicIcon = () => _fullscreen ? BarIcon.ExitFullscreen : BarIcon.Fullscreen },
            new BarItem { Icon = BarIcon.Fit, Tip = "Ajustar a la ventana / tamaño real", IsChecked = () => _cfg.FitToWindow,
                          Click = () => { _cfg.FitToWindow = !_cfg.FitToWindow; _cfg.Save(); ApplyLayout(); }, SeparatorAfter = true },
            new BarItem { Icon = BarIcon.CtrlAltDel, Tip = "Enviar Ctrl+Alt+Supr (puede no tener efecto: Windows lo reserva al sistema)",
                          Click = () => SendCombo(KeyMap.XK_Control_L, KeyMap.XK_Alt_L, 0xFFFF) },
            new BarItem { Icon = BarIcon.WinKey, Tip = "Enviar tecla Windows", Click = () => SendCombo(KeyMap.XK_Super_L), SeparatorAfter = true },
            new BarItem { Icon = BarIcon.Chat, Tip = "Chat con el usuario remoto", Click = OpenChat },
            new BarItem { Icon = BarIcon.SendFiles, Tip = "Enviar archivos al equipo remoto (Descargas\\InfollopAssist)", Click = PickAndSend },
            new BarItem { Icon = BarIcon.Folder, Tip = "Abrir carpeta de recibidos", Click = () => OpenFolder(AppPaths.ReceivedFolder) },
            new BarItem { Icon = BarIcon.ClipboardFiles, Tip = "Copiar y pegar archivos entre equipos (activado/desactivado)", IsChecked = () => _cfg.SyncClipboardFiles,
                          Click = () => { _cfg.SyncClipboardFiles = !_cfg.SyncClipboardFiles; _cfg.Save();
                                          ShowToast(_cfg.SyncClipboardFiles ? "Copiar y pegar archivos: activado" : "Copiar y pegar archivos: desactivado"); },
                          SeparatorAfter = true },
            new BarItem { Icon = BarIcon.Close, Tip = "Desconectar", Click = Close },
        });
        _bar.MouseUp += (_, _) => { _bar.Invalidate(); _surface.Focus(); };
        _track.Tick += (_, _) => _bar.Track(PointToClient(Cursor.Position), Form.ActiveForm == this);

        _scroll.Controls.Add(_surface);
        Controls.Add(_scroll);
        Controls.Add(_bar);
        Controls.Add(_toast);
        _bar.BringToFront();
        Resize += (_, _) => { _bar.CenterIn(ClientSize); _toast.Reposition(); };

        // Tamaño inicial: la pantalla remota cabiendo en el 90 % del área de trabajo
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int chromeH = 60;
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
            else SetStatus($"Recibidos {b.FileCount} archivo(s) en Descargas\\InfollopAssist");
        });
        _c.Ext.Progress += p => UI(() => SetStatus(
            $"{(p.Sending ? "Enviando" : "Recibiendo")} {p.Files} archivo(s): {AppPaths.FormatBytes(p.Done)} de {AppPaths.FormatBytes(p.Total)}" +
            (p.Total > 0 ? $" ({p.Done * 100 / p.Total} %)" : "")));
        _c.Ext.TransferFailed += m => UI(() => SetStatus("Transferencia fallida: " + m));
        _c.Ext.Activated += () => UI(UpdateDefaultStatus);

        _c.Bell += () => UI(() => SystemSounds.Beep.Play());
        _c.Ext.ChatReceived += (from, text) => UI(() => { var chat = Chat(); chat.Add(from, text); chat.Present(); });
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

    private void SetStatus(string s) => ShowToast(s);

    private void ShowToast(string s, int ms = 4000) => _toast.ShowText(s, ms);

    private void UpdateDefaultStatus() { }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _c.Start(); // a partir de aquí llegan imagen y eventos
        _bar.CenterIn(ClientSize);
        _bar.Peek(2500);
        _track.Start();
        ShowToast($"Conectado a {_hostLabel} — {_c.Width}×{_c.Height}", 3000);
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
        _surface.Fit = _cfg.FitToWindow;
        if (_cfg.FitToWindow)
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
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _prevState;
        }
        _bar.Invalidate();
        _surface.Focus();
    }

    private ChatForm? _chat;

    private ChatForm Chat()
    {
        if (_chat == null || _chat.IsDisposed)
        {
            _chat = new ChatForm($"Chat con {_hostLabel} — {Brand.Product}", text => _c.Ext.SendChat(Environment.UserName, text));
            _chat.FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; _chat.Hide(); } };
        }
        return _chat;
    }

    private void OpenChat()
    {
        if (!CheckExt()) return;
        Chat().Present();
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
        MessageBox.Show(this, "El equipo remoto no es un servidor InfollopAssist: sólo se puede ver, controlar y copiar texto.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private void PickAndSend()
    {
        if (!CheckExt()) return;
        using var d = new OpenFileDialog { Multiselect = true, Title = "Archivos para enviar al equipo remoto (Descargas\\InfollopAssist)" };
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
                : "Archivos enviados a Descargas\\InfollopAssist del equipo remoto"));
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
        _track.Stop();
        _chat?.Dispose();
        _hook.Dispose();
        _c.TextReceived -= _clip.SetRemoteText;
        _c.Ext.TextReceived -= _clip.SetRemoteText;
        _clip.Dispose();
        _c.Dispose();
        base.OnFormClosing(e);
    }
}
