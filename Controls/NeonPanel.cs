using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tman.Controls;

/// <summary>
/// Instrument panel frame: dark face, an accent edge with a soft glow, bright corner brackets,
/// and an optional all-caps header with a right-aligned live readout.
/// </summary>
public sealed class NeonPanel : Decorator
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(NeonPanel),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x3F, 0xC8, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(NeonPanel),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HeaderRightProperty = DependencyProperty.Register(nameof(HeaderRight), typeof(string), typeof(NeonPanel),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(NeonPanel),
        new FrameworkPropertyMetadata(new Thickness(14, 11, 14, 14), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    const double HeaderHeight = 24, Radius = 4, Tick = 9;

    static readonly Brush Face = MakeFace();

    static Brush MakeFace()
    {
        var b = new LinearGradientBrush(Color.FromRgb(0x15, 0x19, 0x1F), Color.FromRgb(0x0F, 0x12, 0x16), 90);
        b.Freeze();
        return b;
    }

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    /// <summary>Shown upper-case in the accent color.</summary>
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string HeaderRight { get => (string)GetValue(HeaderRightProperty); set => SetValue(HeaderRightProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    double ContentTop => Padding.Top + (string.IsNullOrEmpty(Header) ? 0 : HeaderHeight);

    protected override Size MeasureOverride(Size constraint)
    {
        var p = Padding;
        double extraW = p.Left + p.Right, extraH = ContentTop + p.Bottom;
        if (Child == null) return new Size(extraW, extraH);
        Child.Measure(new Size(Math.Max(0, constraint.Width - extraW), Math.Max(0, constraint.Height - extraH)));
        return new Size(Child.DesiredSize.Width + extraW, Child.DesiredSize.Height + extraH);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var p = Padding;
        Child?.Arrange(new Rect(p.Left, ContentTop, Math.Max(0, size.Width - p.Left - p.Right), Math.Max(0, size.Height - ContentTop - p.Bottom)));
        return size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 * Tick || h < 2 * Tick) return;
        var c = Led.ColorOf(Accent);
        var rect = new Rect(0.5, 0.5, w - 1, h - 1);

        // Glow: two soft strokes that spill a few pixels outside the edge.
        dc.DrawRoundedRectangle(null, new Pen(Led.Brush(Led.Alpha(c, 0x0E)), 7), rect, Radius, Radius);
        dc.DrawRoundedRectangle(null, new Pen(Led.Brush(Led.Alpha(c, 0x1A)), 3.5), rect, Radius, Radius);
        dc.DrawRoundedRectangle(Face, new Pen(Led.Brush(Led.Alpha(c, 0x66)), 1), rect, Radius, Radius);

        // Corner brackets.
        var tick = new StreamGeometry();
        using (var ctx = tick.Open())
        {
            double l = 1, t = 1, r = w - 1, b = h - 1;
            ctx.BeginFigure(new Point(l, t + Tick), false, false); ctx.LineTo(new Point(l, t), true, false); ctx.LineTo(new Point(l + Tick, t), true, false);
            ctx.BeginFigure(new Point(r - Tick, t), false, false); ctx.LineTo(new Point(r, t), true, false); ctx.LineTo(new Point(r, t + Tick), true, false);
            ctx.BeginFigure(new Point(r, b - Tick), false, false); ctx.LineTo(new Point(r, b), true, false); ctx.LineTo(new Point(r - Tick, b), true, false);
            ctx.BeginFigure(new Point(l + Tick, b), false, false); ctx.LineTo(new Point(l, b), true, false); ctx.LineTo(new Point(l, b - Tick), true, false);
        }
        tick.Freeze();
        dc.DrawGeometry(null, new Pen(Led.Brush(c), 2) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square }, tick);

        if (string.IsNullOrEmpty(Header)) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var p = Padding;
        var title = Led.Text(Header.ToUpperInvariant(), 11, Led.Brush(c), dpi, bold: true);
        dc.DrawText(title, new Point(p.Left, p.Top));
        double ruleEnd = w - p.Right;
        if (!string.IsNullOrEmpty(HeaderRight))
        {
            var right = Led.Text(HeaderRight, 11, Led.Brush(Color.FromRgb(0xDC, 0xE3, 0xEA)), dpi, bold: true);
            right.MaxTextWidth = Math.Max(10, w - p.Left - p.Right - title.Width - 24);
            right.Trimming = TextTrimming.CharacterEllipsis;
            right.MaxLineCount = 1;
            dc.DrawText(right, new Point(w - p.Right - right.Width, p.Top));
            ruleEnd -= right.Width + 10;
        }
        double ruleStart = p.Left + title.Width + 10, y = Math.Round(p.Top + title.Height / 2) + 0.5;
        if (ruleEnd - ruleStart > 8)
            dc.DrawLine(new Pen(Led.Brush(Led.Alpha(c, 0x40)), 1), new Point(ruleStart, y), new Point(ruleEnd, y));
    }
}
