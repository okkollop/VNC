using System.Drawing.Drawing2D;
using Enlace.Windows;

namespace Enlace.Client;

/// <summary>Iconos dibujados para la libreta (etiqueta, cuadrícula, pantalla).</summary>
internal static class Icons
{
    private static (Bitmap, Graphics) New(int w, int h)
    {
        var b = new Bitmap(w, h);
        var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        return (b, g);
    }

    public static Bitmap Tag(int s)
    {
        var (b, g) = New(s, s);
        using (g)
        using (var pen = new Pen(Color.FromArgb(110, 116, 128), 1.5f) { LineJoin = LineJoin.Round })
        {
            float k = s / 20f;
            var pts = new[] { new PointF(3 * k, 3 * k), new PointF(10 * k, 3 * k), new PointF(17 * k, 10 * k), new PointF(10 * k, 17 * k), new PointF(3 * k, 10 * k) };
            g.DrawPolygon(pen, pts);
            g.DrawEllipse(pen, 6 * k, 6 * k, 2.6f * k, 2.6f * k);
        }
        return b;
    }

    public static Bitmap Grid(int s)
    {
        var (b, g) = New(s, s);
        using (g)
        using (var pen = new Pen(Color.FromArgb(110, 116, 128), 1.5f))
        {
            float k = s / 20f, q = 6.5f * k;
            foreach (var (x, y) in new[] { (3f, 3f), (11f, 3f), (3f, 11f), (11f, 11f) })
                g.DrawRectangle(pen, x * k, y * k, q, q);
        }
        return b;
    }

    public static Bitmap Monitor(int w, int h, Color screen)
    {
        var (b, g) = New(w, h);
        using (g)
        {
            var r = new RectangleF(3, 2, w - 6, h - 9);
            using (var fill = new SolidBrush(screen)) g.FillRectangle(fill, r);
            using (var pen = new Pen(Color.FromArgb(70, 76, 88), 1.4f))
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                g.DrawLine(pen, w / 2f, r.Bottom, w / 2f, h - 3);
                g.DrawLine(pen, w / 2f - 6, h - 2.5f, w / 2f + 6, h - 2.5f);
            }
            // pequeño cursor en la pantalla
            using var cur = new SolidBrush(Ui.Accent);
            float cx = r.X + r.Width * 0.62f, cy = r.Y + r.Height * 0.3f;
            g.FillPolygon(cur, new[] { new PointF(cx, cy), new PointF(cx, cy + 9), new PointF(cx + 2.5f, cy + 6.5f), new PointF(cx + 6.5f, cy + 6.5f) });
        }
        return b;
    }
}
