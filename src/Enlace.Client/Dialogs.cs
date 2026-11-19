using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Client;

/// <summary>Base de los diálogos: rejilla etiqueta/control y botones Aceptar/Cancelar.</summary>
internal abstract class SimpleDialog : Form
{
    protected readonly TableLayoutPanel Grid = new() { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button _ok = new() { Text = "Aceptar", AutoSize = true };

    protected SimpleDialog(string title)
    {
        Text = title;
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Controls.Add(Grid);
        AcceptButton = _ok;
        _ok.Click += (_, _) => { if (ValidateInput()) { DialogResult = DialogResult.OK; Close(); } };
    }

    protected void Row(string label, Control c)
    {
        Grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
        Grid.Controls.Add(c);
    }

    protected void Span(Control c)
    {
        Grid.Controls.Add(c);
        Grid.SetColumnSpan(c, 2);
    }

    protected void AddButtons(string okText = "Aceptar")
    {
        _ok.Text = okText;
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        CancelButton = cancel;
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        p.Controls.Add(cancel);
        p.Controls.Add(_ok);
        Span(p);
    }

    protected virtual bool ValidateInput() => true;

    protected bool Warn(string msg)
    {
        MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
}

/// <summary>Alta o edición de un equipo.</summary>
internal sealed class EntryDialog : SimpleDialog
{
    private readonly TextBox _name = new() { Width = 280 };
    private readonly TextBox _host = new() { Width = 280 };
    private readonly ComboBox _folder = new() { Width = 280 };
    private readonly CheckBox _forget = new() { Text = "Olvidar la contraseña guardada", AutoSize = true };
    private readonly BookEntry _entry;

    public EntryDialog(BookEntry entry, IEnumerable<string> folders, bool isNew) : base(isNew ? "Nuevo equipo" : "Editar equipo")
    {
        _entry = entry;
        _name.Text = entry.Name;
        _host.Text = entry.Host;
        _folder.Items.Add("(sin carpeta)");
        foreach (var f in folders) _folder.Items.Add(f);
        _folder.Text = entry.Folder.Length > 0 ? entry.Folder : "(sin carpeta)";

        Row("Nombre:", _name);
        Row("Dirección (IP o nombre):", _host);
        Row("Carpeta:", _folder);
        Span(new Label { Text = "Puedes escribir una carpeta nueva.  Puerto distinto: 192.168.1.20:5901", AutoSize = true, ForeColor = SystemColors.GrayText });
        if (entry.ProtectedPassword != null) Span(_forget);
        AddButtons(isNew ? "Añadir" : "Guardar");
    }

    protected override bool ValidateInput()
    {
        var host = _host.Text.Trim();
        if (!ClientConfig.TryParseHost(host, out _, out _)) return Warn("La dirección no es válida.");
        _entry.Host = host;
        _entry.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : host;
        var f = _folder.Text.Trim();
        _entry.Folder = f == "(sin carpeta)" ? "" : f;
        if (_forget.Checked) _entry.ProtectedPassword = null;
        return true;
    }
}

/// <summary>Pide la contraseña al conectar y si se quiere guardar.</summary>
internal sealed class PasswordDialog : SimpleDialog
{
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 240 };
    private readonly CheckBox _save = new() { Text = "Guardar la contraseña para este equipo", AutoSize = true, Checked = true };

    public PasswordDialog(string target, string? error, bool allowSave) : base("Contraseña")
    {
        Span(new Label { Text = $"Contraseña de {target}:", AutoSize = true, Margin = new Padding(3, 3, 3, 8) });
        if (error != null) Span(new Label { Text = error, AutoSize = true, ForeColor = Color.Firebrick });
        Row("Contraseña:", _pw);
        if (allowSave) Span(_save);
        _save.Visible = allowSave;
        AddButtons("Conectar");
    }

    public string Password => _pw.Text;
    public bool SavePassword => _save.Visible && _save.Checked;
}

/// <summary>Pide un texto (nombre de carpeta).</summary>
internal sealed class TextPrompt : SimpleDialog
{
    private readonly TextBox _text = new() { Width = 260 };

    public TextPrompt(string title, string label, string initial = "") : base(title)
    {
        _text.Text = initial;
        Row(label, _text);
        AddButtons();
    }

    public string Value => _text.Text.Trim();

    protected override bool ValidateInput() => Value.Length > 0 || Warn("Escribe un nombre.");
}
