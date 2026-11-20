using System.Drawing.Drawing2D;
using Enlace.Windows;

namespace Enlace.Server;

/// <summary>Ventana principal del servidor: IP para conectarse, seguridad y conexiones activas.</summary>
internal sealed class StatusForm : Form
{
    private readonly TrayApp _app;
    private readonly Label _mode = new() { AutoSize = true, ForeColor = Ui.Muted, Font = Ui.Body };
    private readonly Dot _dot = new();
    private readonly FlowLayoutPanel _ips = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
    private readonly Label _portInfo = new() { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(330, 0), Margin = new Padding(26, 0, 0, 8) };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(330, 0), Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _pwState = new() { AutoSize = true, MaximumSize = new Size(320, 0), Margin = new Padding(26, 0, 0, 2) };
    private readonly FlowLayoutPanel _sessions = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };

    public StatusForm(TrayApp app)
    {
        _app = app;
        Text = Brand.ServerName;
        Icon = AppIcon.Get();
        Font = Ui.Body;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 470);

        // ---- Cabecera ----
        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Ui.Sidebar, Padding = new Padding(16, 0, 12, 0) };
        var logo = new PictureBox { Image = Brand.Logo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(14, 9) };
        var title = new Label { Text = Brand.ServerName, Font = Ui.Big, ForeColor = Ui.Accent, AutoSize = true, Location = new Point(66, 10) };
        _mode.Location = new Point(68, 36);
        var menuBtn = new HamburgerButton { Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(ClientSize.Width - 52, 13) };
        _dot.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _dot.Location = new Point(ClientSize.Width - 86, 21);
        menuBtn.Click += (_, _) => _app.BuildMenu(forTray: false).Show(menuBtn, new Point(menuBtn.Width - 2, menuBtn.Height), ToolStripDropDownDirection.BelowLeft);
        header.Controls.AddRange(new Control[] { logo, title, _mode, _dot, menuBtn });

        // ---- Cuerpo en dos columnas ----
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(24, 18, 24, 10) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var left = Column();
        left.Controls.Add(new Label { Text = "Conectividad", Font = Ui.Title, AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
        left.Controls.Add(_ips);
        left.Controls.Add(_portInfo);
        left.Controls.Add(_error);

        var right = Column();
        right.Padding = new Padding(22, 0, 0, 0);
        right.Controls.Add(new Label { Text = "Seguridad", Font = Ui.Title, AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
        right.Controls.Add(IconLine("🔒", "Contraseña"));
        right.Controls.Add(_pwState);
        var change = new LinkLabel { Text = "Cambiar contraseña", AutoSize = true, Margin = new Padding(26, 0, 0, 14), LinkColor = Ui.Accent };
        change.LinkClicked += (_, _) => _app.ChangePassword(this);
        right.Controls.Add(change);
        right.Controls.Add(IconLine("🖥", "Conexiones"));
        right.Controls.Add(_sessions);

        body.Controls.Add(left, 0, 0);
        body.Controls.Add(new Panel { BackColor = Ui.Line, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) }, 1, 0);
        body.Controls.Add(right, 2, 0);

        // ---- Pie ----
        var footer = new Label
        {
            Dock = DockStyle.Bottom, Height = 28, ForeColor = Ui.Muted, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(22, 0, 0, 0), Text = $"Versión {Brand.VersionText}   ·   {Brand.Copyright}",
        };

        Controls.Add(body);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Ui.Line });
        Controls.Add(header);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Line });
        Controls.Add(footer);

        _app.Backend.StatusChanged += Refresh;
        FormClosed += (_, _) => _app.Backend.StatusChanged -= Refresh;
        Refresh();
    }

    private static FlowLayoutPanel Column() => new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0),
    };

    private static Control IconLine(string icon, string text)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        p.Controls.Add(new Label { Text = icon, AutoSize = true, Font = new Font("Segoe UI Emoji", 10f), ForeColor = Ui.Muted, Margin = new Padding(0, 0, 4, 0) });
        p.Controls.Add(new Label { Text = text, AutoSize = true, Font = Ui.BodyBold, Margin = new Padding(0, 1, 0, 0) });
        return p;
    }

    public new void Refresh()
    {
        if (IsDisposed) return;
        var st = _app.Backend.Status;
        var cfg = _app.Backend.Config;
        _mode.Text = st.ServiceMode ? "Modo de servicio" : "Modo usuario";
        _dot.Color = st.Listening ? Color.FromArgb(40, 167, 69) : Color.FromArgb(220, 53, 69);
        _dot.Invalidate();

        _ips.SuspendLayout();
        _ips.Controls.Clear();
        var ips = AppPaths.LocalIPv4().ToList();
        if (ips.Count == 0) ips.Add("Sin red");
        foreach (var ip in ips)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
            row.Controls.Add(new Label { Text = "🔗", AutoSize = true, Font = new Font("Segoe UI Emoji", 10f), ForeColor = Ui.Muted, Margin = new Padding(0, 4, 4, 0) });
            var l = new Label { Text = cfg.Port == Protocol.Rfb.DefaultPort ? ip : $"{ip}:{cfg.Port}", Font = Ui.Big, AutoSize = true, Cursor = Cursors.Hand };
            new ToolTip().SetToolTip(l, "Clic para copiar");
            l.Click += (_, _) => { try { Clipboard.SetText(l.Text); } catch { } };
            row.Controls.Add(l);
            _ips.Controls.Add(row);
        }
        _ips.ResumeLayout();
        _portInfo.Text = $"Para conectarse a este equipo, escribe esta dirección en {Brand.ViewerName}. Puerto {cfg.Port}.";
        _error.Text = st.Error ?? "";
        _error.Visible = st.Error != null;

        _pwState.Text = (st.HasPassword ? "Configurada: se pide al conectarse." : "Sin contraseña: nadie puede conectarse.") +
                        (cfg.ViewOnly ? "\nModo sólo ver: no se puede controlar este equipo." : "");

        _sessions.SuspendLayout();
        _sessions.Controls.Clear();
        if (st.Sessions.Count == 0)
            _sessions.Controls.Add(new Label { Text = "Nadie conectado.", ForeColor = Ui.Muted, AutoSize = true, Margin = new Padding(26, 0, 0, 0) });
        foreach (var s in st.Sessions)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(26, 0, 0, 4) };
            row.Controls.Add(new Label { Text = $"{s.Address}  ·  desde las {s.Since:HH:mm}", AutoSize = true, Margin = new Padding(0, 1, 10, 0) });
            if (s.FullFeatures)
            {
                var chat = new LinkLabel { Text = "Chat", AutoSize = true, LinkColor = Ui.Accent };
                int id = s.Id;
                chat.LinkClicked += (_, _) => _app.OpenChat(id, s.Address);
                row.Controls.Add(chat);
            }
            _sessions.Controls.Add(row);
        }
        if (st.Sessions.Count > 0)
        {
            var all = new LinkLabel { Text = "Desconectar a todos", AutoSize = true, LinkColor = Color.Firebrick, Margin = new Padding(26, 6, 0, 0) };
            all.LinkClicked += (_, _) => { try { _app.Backend.DisconnectAll(); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
            _sessions.Controls.Add(all);
        }
        _sessions.ResumeLayout();
    }

    /// <summary>Círculo de estado (verde: escuchando).</summary>
    private sealed class Dot : Control
    {
        public Color Color = Color.Gray;

        public Dot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(22, 22);
            BackColor = Ui.Sidebar;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color);
            e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
            using var p = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (Color.G > Color.R)
                e.Graphics.DrawLines(p, new PointF[] { new(6, 11), new(9.5f, 14.5f), new(16, 7.5f) });
            else
            {
                e.Graphics.DrawLine(p, 7, 7, 14, 14);
                e.Graphics.DrawLine(p, 14, 7, 7, 14);
            }
        }
    }

    private sealed class HamburgerButton : Control
    {
        private bool _hover;

        public HamburgerButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(36, 36);
            Cursor = Cursors.Hand;
            BackColor = Ui.Sidebar;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover) using (var b = new SolidBrush(Ui.Line)) using (var path = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), 5)) g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(70, 75, 85), 2f);
            for (int i = 0; i < 3; i++) g.DrawLine(pen, 9, 11 + i * 7, Width - 10, 11 + i * 7);
        }
    }
}
