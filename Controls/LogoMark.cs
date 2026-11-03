using System.Windows;
using System.Windows.Media;

namespace Tman.Controls;

/// <summary>Tman's mark: a small LED tachometer with its needle, in the accent color.</summary>
public sealed class LogoMark : FrameworkElement
{
    const int Segments = 11;
    const double Start = 150, Sweep = 240, Level = 0.72;

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;
        var accent = Led.ColorOf(TryFindResource("AccentBrush") as Brush);
        var c = new Point(ActualWidth / 2, ActualHeight / 2 + size * 0.06);
        double r = size / 2 - 1, inner = r * 0.68, span = Sweep / Segments;

        dc.DrawEllipse(Led.Brush(Led.Alpha(accent, 0x18)), null, c, r + 1, r + 1);
        for (int i = 0; i < Segments; i++)
        {
            bool on = (i + 0.5) / Segments <= Level;
            double a0 = Start + i * span + span * 0.18, a1 = a0 + span * 0.64;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(P(c, r, a0), true, true);
                ctx.LineTo(P(c, r, a1), false, false);
                ctx.LineTo(P(c, inner, a1), false, false);
                ctx.LineTo(P(c, inner, a0), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(Led.Brush(on ? accent : Led.Alpha(accent, 0x30)), null, g);
        }
        double needle = Start + Sweep * Level;
        dc.DrawLine(new Pen(Led.Brush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            c, P(c, inner - 1, needle));
        dc.DrawEllipse(Led.Brush(accent), null, c, 2.2, 2.2);
    }

    static Point P(Point c, double radius, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
    }
}
