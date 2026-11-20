using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Server;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, MaxLength = VncAuth.MaxPasswordLength, Width = 200 };
    private readonly TextBox _pw2 = new() { UseSystemPasswordChar = true, MaxLength = VncAuth.MaxPasswordLength, Width = 200 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    private readonly NumericUpDown _maxMb = new() { Minimum = 0, Maximum = 1_000_000, Width = 100, Increment = 256 };
    private readonly CheckBox _autostart = new() { Text = "Iniciar con Windows", AutoSize = true };
    private readonly CheckBox _viewOnly = new() { Text = "Sólo ver (no permitir controlar este equipo)", AutoSize = true };
    private readonly ServerConfig _cfg;
    private readonly bool _requirePassword;

    public SettingsForm(ServerConfig cfg, bool firstRun)
    {
        _cfg = cfg;
        _requirePassword = firstRun || cfg.Password == null;
        Text = "InfollopAssist Servidor — Configuración";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _port.Value = cfg.Port;
        _maxMb.Value = cfg.MaxClipboardFilesMB;
        _autostart.Checked = ServerConfig.StartWithWindows;
        _viewOnly.Checked = cfg.ViewOnly;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 3) });
            grid.Controls.Add(c);
        }

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Text = (_requirePassword ? "Elige la contraseña con la que se conectarán a este equipo.\n" : "Deja la contraseña vacía para no cambiarla.\n") +
                   "Por compatibilidad con VNC sólo cuentan los 8 primeros caracteres.",
            Margin = new Padding(3, 3, 3, 10),
        };
        grid.Controls.Add(intro);
        grid.SetColumnSpan(intro, 2);
        Row("Contraseña:", _pw);
        Row("Repetir contraseña:", _pw2);
        Row("Puerto:", _port);
        Row("Límite archivos portapapeles (MB):", _maxMb);
        grid.Controls.Add(_autostart);
        grid.SetColumnSpan(_autostart, 2);
        grid.Controls.Add(_viewOnly);
        grid.SetColumnSpan(_viewOnly, 2);

        var ok = new Button { Text = "Guardar", DialogResult = DialogResult.None, AutoSize = true };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Save();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Save()
    {
        if (_pw.Text != _pw2.Text)
        {
            MessageBox.Show(this, "Las contraseñas no coinciden.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_requirePassword && _pw.Text.Length < 4)
        {
            MessageBox.Show(this, "Usa una contraseña de al menos 4 caracteres.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_pw.Text.Length > 0) _cfg.ProtectedPassword = Secret.Protect(_pw.Text);
        _cfg.Port = (int)_port.Value;
        _cfg.MaxClipboardFilesMB = (int)_maxMb.Value;
        _cfg.ViewOnly = _viewOnly.Checked;
        _cfg.Save();
        try { ServerConfig.StartWithWindows = _autostart.Checked; } catch { }
        DialogResult = DialogResult.OK;
        Close();
    }
}
