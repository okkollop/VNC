using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Enlace.Windows;

/// <summary>Colores y fuentes comunes, estilo claro y sobrio.</summary>
public static class Ui
{
    public static readonly Color Accent = Color.FromArgb(31, 67, 112);      // azul oscuro del logo
    public static readonly Color AccentLight = Color.FromArgb(162, 204, 255); // azul claro del logo
    public static readonly Color Sidebar = Color.FromArgb(246, 247, 250);
    public static readonly Color SidebarSelected = Color.FromArgb(226, 236, 250);
    public static readonly Color Line = Color.FromArgb(228, 230, 235);
    public static readonly Color Muted = Color.FromArgb(105, 112, 125);
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Big = new("Segoe UI Semibold", 13f);
    public static readonly Font Small = new("Segoe UI", 8.75f);

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] private static extern bool FlashWindow(IntPtr hwnd, bool invert);

    public static void Flash(Form f) { try { FlashWindow(f.Handle, true); } catch { } }
}

/// <summary>Interruptor deslizante (on/off).</summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(44, 24);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && Enabled) Checked = !Checked;
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1, 2, Width - 3, Height - 5);
        var bg = !Enabled ? Color.FromArgb(210, 212, 216) : _checked ? Ui.Accent : Color.FromArgb(190, 195, 205);
        using (var b = new SolidBrush(bg)) using (var p = Ui.Round(r, r.Height / 2)) g.FillPath(b, p);
        float d = r.Height - 6;
        float x = _checked ? r.Right - d - 3 : r.X + 3;
        g.FillEllipse(Brushes.White, x, r.Y + 3, d, d);
        if (Focused) using (var pen = new Pen(Ui.AccentLight, 1.5f)) using (var p = Ui.Round(r, r.Height / 2)) g.DrawPath(pen, p);
    }
}

/// <summary>
/// Ventana de opciones con barra lateral de secciones (estilo "Ajustes").
/// Cada sección es un panel con filas: título, descripción y control a la derecha.
/// </summary>
public class SidebarSettingsForm : Form
{
    private readonly Panel _side = new() { Dock = DockStyle.Left, Width = 190, BackColor = Ui.Sidebar, Padding = new Padding(6, 10, 6, 10) };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Color.White, AutoScroll = true, Padding = new Padding(26, 18, 26, 18) };
    private readonly List<(Label Tab, Panel Page)> _pages = new();

    public SidebarSettingsForm(string title)
    {
        Text = title;
        Icon = AppIcon.Get();
        Font = Ui.Body;
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 600);
        MinimumSize = new Size(640, 420);
        Controls.Add(_content);
        Controls.Add(new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Ui.Line });
        Controls.Add(_side);
    }

    /// <summary>Añade una sección; devuelve el panel donde añadir filas con <see cref="AddRow"/>.</summary>
    public Panel AddPage(string name, string? subtitle = null)
    {
        var page = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top,
            Visible = _pages.Count == 0, BackColor = Color.White,
        };
        page.Controls.Add(new Label { Text = name, Font = Ui.Title, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        if (subtitle != null) page.Controls.Add(new Label { Text = subtitle, ForeColor = Ui.Muted, AutoSize = true, MaximumSize = new Size(540, 0), Margin = new Padding(0, 0, 0, 14) });
        _content.Controls.Add(page);

        var tab = new Label
        {
            Text = "   " + name, AutoSize = false, Height = 34, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 2),
        };
        tab.Click += (_, _) => Select(page);
        // las pestañas se apilan en orden (DockStyle.Top añade arriba: insertar al final)
        _side.Controls.Add(tab);
        _side.Controls.SetChildIndex(tab, 0);
        _pages.Add((tab, page));
        Select(_pages[0].Page);
        return page;
    }

    public void Select(Panel page)
    {
        foreach (var (tab, p) in _pages)
        {
            bool on = p == page;
            p.Visible = on;
            tab.BackColor = on ? Ui.SidebarSelected : Ui.Sidebar;
            tab.ForeColor = on ? Ui.Accent : Color.FromArgb(40, 44, 52);
            tab.Font = on ? Ui.BodyBold : Ui.Body;
        }
    }

    /// <summary>Fila: título + descripción a la izquierda, control a la derecha, línea separadora.</summary>
    public static Control AddRow(Panel page, string title, string? description, Control? right)
    {
        var row = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, Width = 560, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 0), Padding = new Padding(0, 10, 0, 10),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        left.Controls.Add(new Label { Text = title, Font = Ui.BodyBold, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        if (description != null)
            left.Controls.Add(new Label { Text = description, ForeColor = Ui.Muted, AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0) });
        row.Controls.Add(left, 0, 0);
        if (right != null)
        {
            right.Anchor = AnchorStyles.Right;
            row.Controls.Add(right, 1, 0);
        }
        page.Controls.Add(row);
        page.Controls.Add(new Panel { Height = 1, Width = 560, BackColor = Ui.Line, Margin = new Padding(0) });
        return row;
    }

    /// <summary>Sección "Acerca de": logo, versión, copyright y botón de actualizaciones.</summary>
    public Panel AddAboutPage(string programName, Action? checkUpdates)
    {
        var page = AddPage("Acerca de");
        var logo = Brand.Logo();
        if (logo != null)
            page.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(120, 120), Margin = new Padding(0, 0, 0, 10) });
        AddRow(page, "Versión", $"{programName} {Brand.VersionText}", checkUpdates == null ? null : MakeButton("Buscar actualizaciones", checkUpdates));
        AddRow(page, "Copyright", Brand.Copyright, null);
        return page;
    }

    public static Button MakeButton(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.System, Padding = new Padding(6, 2, 6, 2) };
        b.Click += (_, _) => click();
        return b;
    }
}

/// <summary>Ventana de chat (igual en servidor y visor).</summary>
public sealed class ChatForm : Form
{
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = Ui.Body, DetectUrls = true };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill, Multiline = true, Font = Ui.Body, BorderStyle = BorderStyle.FixedSingle };
    private readonly Action<string> _send;

    public ChatForm(string title, Action<string> send)
    {
        _send = send;
        Text = title;
        Icon = AppIcon.Get();
        Font = Ui.Body;
        Size = new Size(420, 460);
        MinimumSize = new Size(300, 260);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 70, ColumnCount = 2, Padding = new Padding(8) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var sendBtn = new Button { Text = "Enviar", AutoSize = true, Dock = DockStyle.Fill, FlatStyle = FlatStyle.System };
        sendBtn.Click += (_, _) => Send();
        bottom.Controls.Add(_input, 0, 0);
        bottom.Controls.Add(sendBtn, 1, 0);
        var hint = new Label { Text = "Intro para enviar · Mayús+Intro para nueva línea", Dock = DockStyle.Bottom, ForeColor = Ui.Muted, Font = Ui.Small, Padding = new Padding(10, 0, 0, 2), Height = 18 };

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 4), BackColor = Color.White };
        logPanel.Controls.Add(_log);
        Controls.Add(logPanel);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Line });
        Controls.Add(hint);
        Controls.Add(bottom);

        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _input.Focus();
    }

    private void Send()
    {
        var t = _input.Text.Trim();
        if (t.Length == 0) return;
        try
        {
            _send(t);
            Add("Yo", t, mine: true);
            _input.Clear();
        }
        catch (Exception ex)
        {
            AddSystem("No se pudo enviar: " + ex.Message);
        }
    }

    public void Add(string from, string text, bool mine = false)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionFont = Ui.BodyBold;
        _log.SelectionColor = mine ? Ui.Muted : Ui.Accent;
        _log.AppendText($"{from}  {DateTime.Now:HH:mm}\n");
        _log.SelectionFont = Ui.Body;
        _log.SelectionColor = Color.Black;
        _log.AppendText(text.Replace("\r\n", "\n") + "\n\n");
        _log.ScrollToCaret();
        if (!mine && !ContainsFocus) Ui.Flash(this);
    }

    public void AddSystem(string text)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionFont = Ui.Small;
        _log.SelectionColor = Ui.Muted;
        _log.AppendText(text + "\n\n");
        _log.ScrollToCaret();
    }

    /// <summary>Muestra la ventana delante (al llegar un mensaje).</summary>
    public void Present()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        TopMost = true;
        Activate();
        TopMost = false;
    }
}
