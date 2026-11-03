using System.Windows;
using System.Windows.Media;

namespace Tman.Controls;

/// <summary>
/// Segmented 240° arc gauge with peak hold, a big readout in the middle and a label underneath,
/// like a tachometer built from LEDs. Value is 0..1.
/// </summary>
public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((ArcGauge)d)._peak.Push(double.IsFinite((double)e.NewValue) ? Math.Clamp((double)e.NewValue, 0, 1) : 0)));
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), typeof(Brush), Brushes.DeepSkyBlue);
    public static readonly DependencyProperty UseScaleProperty = Register(nameof(UseScale), typeof(bool), false);
    public static readonly DependencyProperty TextProperty = Register(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), typeof(string), "");
    public static readonly DependencyProperty LabelProperty = Register(nameof(Label), typeof(string), "");
    public static readonly DependencyProperty SubProperty = Register(nameof(Sub), typeof(string), "");

    static DependencyProperty Register(string name, Type type, object def) =>
        DependencyProperty.Register(name, type, typeof(ArcGauge), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    const double StartAngle = 150, SweepAngle = 240, MaxDiameter = 132, LabelSpace = 40;
    const int Segments = 34;
    readonly PeakHold _peak = new();

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool UseScale { get => (bool)GetValue(UseScaleProperty); set => SetValue(UseScaleProperty, value); }
    /// <summary>Big readout in the middle of the arc.</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Small line under the readout (unit or context).</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    /// <summary>All-caps name under the gauge, drawn in the gauge color.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Sub { get => (string)GetValue(SubProperty); set => SetValue(SubProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        double d = double.IsInfinity(available.Width) ? MaxDiameter : Math.Min(MaxDiameter, available.Width - 8);
        return new Size(d + 8, d * 0.75 + 6 + LabelSpace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double d = Math.Min(Math.Min(MaxDiameter, w - 8), (h - 6 - LabelSpace) / 0.75);
        if (d < 20) return;
        double r = d / 2, inner = r * 0.76;
        var center = new Point(w / 2, r + 3);
        var color = Led.ColorOf(Fill);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        int lit = (int)Math.Round(Math.Clamp(Value, 0, 1) * Segments);
        int peak = (int)Math.Ceiling(_peak.Value * Segments) - 1;
        double span = SweepAngle / Segments;
        for (int i = 0; i < Segments; i++)
        {
            var c = UseScale ? Led.Scale((i + 0.5) / Segments) : color;
            var brush = i < lit ? Led.Brush(c) : i == peak ? Led.Brush(Led.Alpha(c, 0xC8)) : Led.Brush(Led.Alpha(c, 0x20));
            double a0 = StartAngle + i * span + span * 0.16, a1 = a0 + span * 0.68;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Polar(center, r, a0), true, true);
                ctx.LineTo(Polar(center, r, a1), false, false);
                ctx.LineTo(Polar(center, inner, a1), false, false);
                ctx.LineTo(Polar(center, inner, a0), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(brush, null, g);
        }

        // Thin guide ring just inside the LEDs.
        var ring = new StreamGeometry();
        using (var ctx = ring.Open())
        {
            double rr = inner - 4;
            ctx.BeginFigure(Polar(center, rr, StartAngle), false, false);
            ctx.ArcTo(Polar(center, rr, StartAngle + SweepAngle), new Size(rr, rr), 0, true, SweepDirection.Clockwise, true, false);
        }
        ring.Freeze();
        dc.DrawGeometry(null, new Pen(Led.Brush(Led.Alpha(color, 0x30)), 1), ring);

        if (Text.Length > 0)
        {
            var big = Led.Text(Text, Math.Max(12, r * 0.36), Led.Brush(Color.FromRgb(0xF2, 0xF5, 0xF8)), dpi, bold: true);
            big.MaxTextWidth = inner * 1.9;
            big.Trimming = TextTrimming.CharacterEllipsis;
            big.MaxLineCount = 1;
            dc.DrawText(big, new Point(center.X - big.Width / 2, center.Y - big.Height / 2 - 2));
        }
        if (Unit.Length > 0)
        {
            var unit = Led.Text(Unit, 10.5, Led.Brush(Color.FromRgb(0x8C, 0x96, 0xA3)), dpi);
            unit.MaxTextWidth = inner * 1.8;
            unit.Trimming = TextTrimming.CharacterEllipsis;
            unit.MaxLineCount = 1;
            dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + r * 0.2));
        }

        double y = center.Y + r * 0.5 + 8;
        if (Label.Length > 0)
        {
            var label = Led.Text(Label, 11, Led.Brush(color), dpi, bold: true);
            dc.DrawText(label, new Point(center.X - label.Width / 2, y));
            y += label.Height + 1;
        }
        if (Sub.Length > 0)
        {
            var sub = Led.Text(Sub, 10.5, Led.Brush(Color.FromRgb(0x8C, 0x96, 0xA3)), dpi);
            sub.MaxTextWidth = Math.Max(10, w - 4);
            sub.Trimming = TextTrimming.CharacterEllipsis;
            sub.MaxLineCount = 1;
            dc.DrawText(sub, new Point(Math.Max(2, center.X - sub.Width / 2), y));
        }
    }

    static Point Polar(Point c, double radius, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(c.X + radius * Math.Cos(a), c.Y + radius * Math.Sin(a));
    }
}
