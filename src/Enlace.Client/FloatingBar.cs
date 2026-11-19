using System.Drawing.Drawing2D;

namespace Enlace.Client;

internal enum BarIcon { Fullscreen, ExitFullscreen, Fit, CtrlAltDel, WinKey, SendFiles, Folder, ClipboardFiles, Close }

internal sealed class BarItem
{
    public required BarIcon Icon;
    public required string Tip;
    public required Action Click;
    public Func<bool>? IsChecked;
    public Func<BarIcon>? DynamicIcon;
    public bool SeparatorAfter;
    internal Rectangle Bounds;
}

/// <summary>
/// Barra de iconos flotante, centrada arriba. Se esconde sola y reaparece al acercar
/// el ratón al borde superior de la ventana.
/// </summary>
internal sealed class FloatingBar : Control
{
    private const int Btn = 34, Pad = 5, Sep = 9, Radius = 9;
    private readonly List<BarItem> _items = new();
    private readonly ToolTip _tip = new() { InitialDelay = 400, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private BarItem? _hover, _pressed;
    private bool _shown = true;
    private long _hideAt;
    private int _hiddenTop;

    public FloatingBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Color.FromArgb(250, 250, 252);
        Cursor = Cursors.Default;
        _timer.Tick += (_, _) => Animate();
    }

    public void SetItems(IEnumerable<BarItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        int x = Pad;
        foreach (var it in _items)
        {
            it.Bounds = new Rectangle(x, Pad, Btn, Btn - 2);
            x += Btn + (it.SeparatorAfter ? Sep : 0);
        }
        Size = new Size(x + Pad, Btn + Pad * 2 - 2);
        _hiddenTop = -Height + 4; // queda una franja de 4 px visible como pista
        using (var shape = Round(new RectangleF(0, -Radius, Width, Height + Radius), Radius)) Region = new Region(shape);
        Invalidate();
    }

    /// <summary>Se llama periódicamente con la posición del ratón en coordenadas del formulario.</summary>
    public void Track(Point mouseInParent, bool windowActive)
    {
        long now = Environment.TickCount64;
        bool near = windowActive && mouseInParent.X >= Left - 40 && mouseInParent.X <= Right + 40 &&
                    mouseInParent.Y >= 0 && mouseInParent.Y <= (_shown ? Bottom + 12 : 10);
        if (near) { _hideAt = now + 900; Show(true); }
        else if (_shown && now > _hideAt && _pressed == null) Show(false);
    }

    /// <summary>Muestra la barra unos segundos (al conectar).</summary>
    public void Peek(int ms)
    {
        _hideAt = Environment.TickCount64 + ms;
        Show(true);
    }

    private void Show(bool show)
    {
        if (_shown == show) return;
        _shown = show;
        if (!show) _tip.Hide(this);
        _timer.Start();
    }

    private void Animate()
    {
        int target = _shown ? 0 : _hiddenTop;
        int step = Math.Max(3, Math.Abs(target - Top) / 3);
        Top = Top < target ? Math.Min(target, Top + step) : Math.Max(target, Top - step);
        if (Top == target) _timer.Stop();
    }

    public void CenterIn(Size parent)
    {
        Left = Math.Max(0, (parent.Width - Width) / 2);
        if (!_timer.Enabled) Top = _shown ? 0 : _hiddenTop;
    }

    // ---------------- Ratón ----------------

    private BarItem? Hit(Point p) => _items.FirstOrDefault(i => i.Bounds.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        if (h != _hover)
        {
            _hover = h;
            if (h != null) _tip.Show(h.Tip, this, h.Bounds.Left, Height + 4, 2500);
            else _tip.Hide(this);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = null;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = Hit(e.Location); Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var p = _pressed;
        _pressed = null;
        Invalidate();
        if (p != null && p == Hit(e.Location)) p.Click();
    }

    // ---------------- Dibujo ----------------

    private static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = new RectangleF(0.5f, -Radius, Width - 1, Height + Radius - 0.5f); // esquinas redondeadas sólo abajo
        using (var path = Round(body, Radius))
        using (var fill = new SolidBrush(Color.FromArgb(250, 250, 252)))
        using (var border = new Pen(Color.FromArgb(200, 202, 208)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        foreach (var it in _items)
        {
            bool on = it.IsChecked?.Invoke() == true;
            var r = it.Bounds;
            if (it == _pressed || it == _hover || on)
            {
                var c = it == _pressed ? Color.FromArgb(200, 215, 240) : on ? Color.FromArgb(222, 233, 252) : Color.FromArgb(232, 234, 240);
                using var b = new SolidBrush(c);
                using var pth = Round(new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), 6);
                g.FillPath(b, pth);
            }
            var color = on ? Color.FromArgb(22, 104, 214) : Color.FromArgb(55, 60, 70);
            DrawIcon(g, it.DynamicIcon?.Invoke() ?? it.Icon, new RectangleF(r.X + (r.Width - 20) / 2f, r.Y + (r.Height - 20) / 2f, 20, 20), color);
            if (it.SeparatorAfter)
            {
                using var sp = new Pen(Color.FromArgb(215, 217, 222));
                float sx = r.Right + Sep / 2f;
                g.DrawLine(sp, sx, r.Top + 6, sx, r.Bottom - 6);
            }
        }
    }

    private static void DrawIcon(Graphics g, BarIcon icon, RectangleF b, Color c)
    {
        using var pen = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(c);
        float x = b.X, y = b.Y, w = b.Width, h = b.Height;
        switch (icon)
        {
            case BarIcon.Fullscreen:
            case BarIcon.ExitFullscreen:
            {
                bool outward = icon == BarIcon.Fullscreen;
                float k = 6;
                // cuatro esquinas
                (float cx, float cy, float dx, float dy)[] corners = { (x + 2, y + 2, 1, 1), (x + w - 2, y + 2, -1, 1), (x + 2, y + h - 2, 1, -1), (x + w - 2, y + h - 2, -1, -1) };
                foreach (var (cx, cy, dx, dy) in corners)
                {
                    if (outward)
                    {
                        g.DrawLine(pen, cx, cy, cx + dx * k, cy);
                        g.DrawLine(pen, cx, cy, cx, cy + dy * k);
                        g.DrawLine(pen, cx, cy, cx + dx * 5, cy + dy * 5);
                    }
                    else
                    {
                        float ix = cx + dx * 6, iy = cy + dy * 6;
                        g.DrawLine(pen, ix, iy, ix - dx * k, iy);
                        g.DrawLine(pen, ix, iy, ix, iy - dy * k);
                        g.DrawLine(pen, ix, iy, cx, cy);
                    }
                }
                break;
            }
            case BarIcon.Fit:
                g.DrawRectangle(pen, x + 1, y + 3, w - 2, h - 6);
                g.DrawLine(pen, x + 6, y + h - 7, x + w - 6, y + 7);
                g.DrawLine(pen, x + w - 6, y + 7, x + w - 10, y + 7);
                g.DrawLine(pen, x + w - 6, y + 7, x + w - 6, y + 11);
                g.DrawLine(pen, x + 6, y + h - 7, x + 10, y + h - 7);
                g.DrawLine(pen, x + 6, y + h - 7, x + 6, y + h - 11);
                break;
            case BarIcon.CtrlAltDel:
            {
                using var f = new Font("Segoe UI", 5.6f, FontStyle.Bold, GraphicsUnit.Point);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("CTRL\nALT\nDEL", f, brush, new RectangleF(x - 4, y - 3, w + 8, h + 6), sf);
                break;
            }
            case BarIcon.WinKey:
            {
                float s = 7.5f, gap = 1.6f, ox = x + (w - 2 * s - gap) / 2, oy = y + (h - 2 * s - gap) / 2;
                g.FillRectangle(brush, ox, oy, s, s);
                g.FillRectangle(brush, ox + s + gap, oy, s, s);
                g.FillRectangle(brush, ox, oy + s + gap, s, s);
                g.FillRectangle(brush, ox + s + gap, oy + s + gap, s, s);
                break;
            }
            case BarIcon.SendFiles:
                // documento con flecha hacia arriba
                g.DrawLines(pen, new[] { new PointF(x + 4, y + 1), new PointF(x + 12, y + 1), new PointF(x + 16, y + 5), new PointF(x + 16, y + h - 1), new PointF(x + 4, y + h - 1), new PointF(x + 4, y + 1) });
                g.DrawLine(pen, x + 10, y + h - 5, x + 10, y + 8);
                g.DrawLine(pen, x + 10, y + 8, x + 7, y + 11);
                g.DrawLine(pen, x + 10, y + 8, x + 13, y + 11);
                break;
            case BarIcon.Folder:
                g.DrawLines(pen, new[] { new PointF(x + 1, y + 4), new PointF(x + 7, y + 4), new PointF(x + 9, y + 6), new PointF(x + w - 1, y + 6), new PointF(x + w - 1, y + h - 3), new PointF(x + 1, y + h - 3), new PointF(x + 1, y + 4) });
                g.DrawLine(pen, x + 1, y + 9, x + w - 1, y + 9);
                break;
            case BarIcon.ClipboardFiles:
                g.DrawRectangle(pen, x + 3, y + 3, w - 6, h - 4);
                g.FillRectangle(brush, x + 7, y + 1, w - 14, 4);
                g.DrawLine(pen, x + 7, y + 9, x + w - 7, y + 9);
                g.DrawLine(pen, x + 7, y + 13, x + w - 7, y + 13);
                g.DrawLine(pen, x + 7, y + 17, x + w - 10, y + 17);
                break;
            case BarIcon.Close:
                g.DrawLine(pen, x + 4, y + 4, x + w - 4, y + h - 4);
                g.DrawLine(pen, x + w - 4, y + 4, x + 4, y + h - 4);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Aviso flotante abajo en el centro (transferencias, etc.).</summary>
internal sealed class Toast : Control
{
    private readonly System.Windows.Forms.Timer _timer = new();

    public Toast()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false);
        Visible = false;
        Font = new Font("Segoe UI", 9.5f);
        _timer.Tick += (_, _) => { _timer.Stop(); Visible = false; };
    }

    public void ShowText(string text, int ms = 4000)
    {
        Text = text;
        var sz = TextRenderer.MeasureText(text, Font);
        Size = new Size(Math.Min(sz.Width + 28, Parent?.ClientSize.Width - 20 ?? 600), sz.Height + 14);
        Reposition();
        Visible = true;
        BringToFront();
        Invalidate();
        _timer.Stop();
        _timer.Interval = ms;
        _timer.Start();
    }

    public void Reposition()
    {
        if (Parent == null) return;
        Left = (Parent.ClientSize.Width - Width) / 2;
        Top = Parent.ClientSize.Height - Height - 18;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(45, 48, 55));
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
