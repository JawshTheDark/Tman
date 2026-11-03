using System.Windows;
using System.Windows.Media;

namespace Tman.Controls;

/// <summary>Horizontal LED-segment fill bar; Value is 0..1. Unlit segments stay faintly visible in the same color.</summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(UsageBar),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    const double Segment = 3, Gap = 1;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public UsageBar() => Height = 8;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var color = Led.ColorOf(Fill);
        int n = Math.Max(1, (int)((w + Gap) / (Segment + Gap)));
        double seg = (w - Gap * (n - 1)) / n;
        double v = double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) : 0;
        int lit = (int)Math.Round(v * n);
        if (lit == 0 && v > 0) lit = 1; // anything above zero shows at least one segment
        var on = Led.Brush(color);
        var off = Led.Brush(Led.Alpha(color, 0x24));
        for (int i = 0; i < n; i++)
            dc.DrawRectangle(i < lit ? on : off, null, new Rect(i * (seg + Gap), 0, seg, h));
    }
}
