using System.Diagnostics;
using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Server;

/// <summary>Opciones del servidor, con barra lateral de secciones. Los cambios se aplican al momento.</summary>
internal sealed class OptionsForm : SidebarSettingsForm
{
    private readonly TrayApp _app;
    private readonly Panel _about;
    private readonly Label _pwDesc;
    private bool _loading;

    public OptionsForm(TrayApp app) : base($"Opciones — {Brand.ServerName}")
    {
        _app = app;
        var cfg = app.Backend.Config;
        _loading = true;

        // ---- General ----
        var general = AddPage("General", $"{Brand.ServerName} vive junto al reloj y consume muy poco mientras nadie se conecta.");
        var service = new ToggleSwitch { Checked = app.ServiceMode };
        service.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _app.SetServiceMode(service.Checked, this);
            _loading = true; service.Checked = _app.ServiceMode; _loading = false; // si se canceló
        };
        AddRow(general, "Ejecutar como servicio",
            "Recomendado. Permite ver la pantalla de inicio de sesión, la de bloqueo y las ventanas de administrador, " +
            "y funciona aunque nadie haya iniciado sesión. Pide permiso de administrador.", service);
        if (!app.ServiceMode)
        {
            var autostart = new ToggleSwitch { Checked = SafeStartWithWindows() };
            autostart.CheckedChanged += (_, _) => { try { ServerConfig.StartWithWindows = autostart.Checked; } catch { } };
            AddRow(general, "Iniciar con Windows", "Arranca minimizado junto al reloj al iniciar sesión.", autostart);
        }
        AddRow(general, "Datos de la aplicación", AppPaths.MachineConfigDir, MakeButton("Abrir", () => TrayApp.OpenFolder(AppPaths.MachineConfigDir)));

        // ---- Seguridad ----
        var sec = AddPage("Seguridad");
        _pwDesc = new Label();
        AddRow(sec, "Contraseña", PwText(), MakeButton("Cambiar…", () => { _app.ChangePassword(this); RefreshPw(sec); }));
        var viewOnly = new ToggleSwitch { Checked = cfg.ViewOnly };
        viewOnly.CheckedChanged += (_, _) => Apply(c => c.ViewOnly = viewOnly.Checked);
        AddRow(sec, "Sólo ver", "Quien se conecte podrá ver la pantalla pero no mover el ratón ni escribir.", viewOnly);

        // ---- Conexión ----
        var net = AddPage("Conexión");
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = cfg.Port, Width = 90 };
        var portPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        portPanel.Controls.Add(port);
        portPanel.Controls.Add(MakeButton("Aplicar", () => Apply(c => c.Port = (int)port.Value)));
        AddRow(net, "Puerto", $"Por defecto {Rfb.DefaultPort}. Si lo cambias, en el visor hay que escribir IP:puerto.", portPanel);
        AddRow(net, "Direcciones IP de este equipo", string.Join("\n", AppPaths.LocalIPv4().DefaultIfEmpty("Sin red")), null);

        // ---- Archivos ----
        var files = AddPage("Archivos");
        var max = new NumericUpDown { Minimum = 0, Maximum = 1_000_000, Increment = 256, Value = cfg.MaxClipboardFilesMB, Width = 100 };
        max.ValueChanged += (_, _) => Apply(c => c.MaxClipboardFilesMB = (int)max.Value);
        AddRow(files, "Límite al copiar archivos (MB)", "Tamaño máximo que se envía al copiar archivos con Ctrl+C. 0 = sin límite.", max);
        AddRow(files, "Carpeta de recibidos", AppPaths.ReceivedFolder, MakeButton("Abrir", () => TrayApp.OpenFolder(AppPaths.ReceivedFolder)));

        // ---- Actualizaciones ----
        var upd = AddPage("Actualizaciones");
        var auto = new ToggleSwitch { Checked = cfg.AutoUpdate };
        auto.CheckedChanged += (_, _) => Apply(c => c.AutoUpdate = auto.Checked);
        AddRow(upd, "Actualizar automáticamente", "Busca versiones nuevas cada 6 horas y se instalan solas cuando nadie está conectado.", auto);
        var url = new TextBox { Text = cfg.UpdateUrl, Width = 280 };
        url.Leave += (_, _) => { if (url.Text.Trim() != _app.Backend.Config.UpdateUrl) Apply(c => c.UpdateUrl = url.Text.Trim()); };
        AddRow(upd, "Servidor de actualizaciones", "Dirección donde se publican las versiones.", url);

        _about = AddAboutPage(Brand.ServerName, async () => await Updater.CheckInteractiveAsync(this, AppKind.Servidor,
            beforeRestart: Program.ReleaseSingleInstance));

        _loading = false;
    }

    private static bool SafeStartWithWindows()
    {
        try { return ServerConfig.StartWithWindows; } catch { return false; }
    }

    private string PwText() => _app.Backend.Status.HasPassword
        ? "Configurada. Por compatibilidad con VNC sólo cuentan los 8 primeros caracteres."
        : "Sin contraseña: nadie puede conectarse hasta que pongas una.";

    private void RefreshPw(Panel sec)
    {
        foreach (var l in sec.Controls.OfType<TableLayoutPanel>().SelectMany(r => r.Controls.OfType<FlowLayoutPanel>()).SelectMany(f => f.Controls.OfType<Label>()))
            if (l.Text.StartsWith("Configurada") || l.Text.StartsWith("Sin contraseña")) l.Text = PwText();
    }

    private void Apply(Action<ServerConfig> change)
    {
        if (_loading) return;
        var cfg = _app.Backend.Config.Clone();
        change(cfg);
        _app.Save(cfg, this);
    }

    public void ShowAbout() => Select(_about);
}

/// <summary>Pide la contraseña nueva dos veces.</summary>
internal sealed class PasswordForm : Form
{
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, MaxLength = VncAuth.MaxPasswordLength, Width = 200 };
    private readonly TextBox _pw2 = new() { UseSystemPasswordChar = true, MaxLength = VncAuth.MaxPasswordLength, Width = 200 };

    public PasswordForm(bool required)
    {
        Text = required ? $"{Brand.ServerName} — Elige una contraseña" : "Cambiar contraseña";
        Icon = AppIcon.Get();
        Font = Ui.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(3, 3, 3, 10),
            Text = (required ? "Es la contraseña que tendrán que escribir para conectarse a este equipo.\n" : "") +
                   "Por compatibilidad con VNC sólo cuentan los 8 primeros caracteres.",
        };
        grid.Controls.Add(intro);
        grid.SetColumnSpan(intro, 2);
        grid.Controls.Add(new Label { Text = "Contraseña:", AutoSize = true, Margin = new Padding(3, 7, 10, 3) });
        grid.Controls.Add(_pw);
        grid.Controls.Add(new Label { Text = "Repetir:", AutoSize = true, Margin = new Padding(3, 7, 10, 3) });
        grid.Controls.Add(_pw2);
        var ok = new Button { Text = "Guardar", AutoSize = true };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            if (_pw.Text != _pw2.Text) { MessageBox.Show(this, "Las contraseñas no coinciden."); return; }
            if (_pw.Text.Length < 4) { MessageBox.Show(this, "Usa al menos 4 caracteres."); return; }
            DialogResult = DialogResult.OK;
            Close();
        };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Password => _pw.Text;
}
