using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tman.Controls;

/// <summary>Segmented LED meter with peak hold. Value is 0..1; unlit segments stay faintly visible.</summary>
public sealed class LedBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(LedBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((LedBar)d)._peak.Push(Clamp((double)e.NewValue))));
    public static readonly DependencyProperty FillProperty = Register(nameof(Fill), typeof(Brush), Brushes.DeepSkyBlue);
    public static readonly DependencyProperty OrientationProperty = Register(nameof(Orientation), typeof(Orientation), Orientation.Vertical);
    public static readonly DependencyProperty UseScaleProperty = Register(nameof(UseScale), typeof(bool), false);
    public static readonly DependencyProperty ShowPeakProperty = Register(nameof(ShowPeak), typeof(bool), true);
    public static readonly DependencyProperty SegmentProperty = Register(nameof(Segment), typeof(double), 3.0);
    public static readonly DependencyProperty GapProperty = Register(nameof(Gap), typeof(double), 2.0);

    static DependencyProperty Register(string name, Type type, object def) =>
        DependencyProperty.Register(name, type, typeof(LedBar), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    readonly PeakHold _peak = new();

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    /// <summary>Color segments green → amber → red by position instead of using <see cref="Fill"/>.</summary>
    public bool UseScale { get => (bool)GetValue(UseScaleProperty); set => SetValue(UseScaleProperty, value); }
    public bool ShowPeak { get => (bool)GetValue(ShowPeakProperty); set => SetValue(ShowPeakProperty, value); }
    /// <summary>Nominal segment length in pixels; segments stretch slightly to fill the bar exactly.</summary>
    public double Segment { get => (double)GetValue(SegmentProperty); set => SetValue(SegmentProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    static double Clamp(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        bool vertical = Orientation == Orientation.Vertical;
        double length = vertical ? h : w, gap = Gap;
        int n = Math.Max(1, (int)((length + gap) / (Segment + gap)));
        double seg = (length - gap * (n - 1)) / n;
        int lit = (int)Math.Round(Clamp(Value) * n);
        int peak = ShowPeak ? (int)Math.Ceiling(_peak.Value * n) - 1 : -1;
        var baseColor = Led.ColorOf(Fill);

        for (int i = 0; i < n; i++)
        {
            var color = UseScale ? Led.Scale((i + 0.5) / n) : baseColor;
            var brush = i < lit ? Led.Brush(color)
                : i == peak ? Led.Brush(Led.Alpha(color, 0xC8))
                : Led.Brush(Led.Alpha(color, 0x22));
            double p = i * (seg + gap);
            dc.DrawRectangle(brush, null, vertical ? new Rect(0, h - p - seg, w, seg) : new Rect(p, 0, seg, h));
        }
    }
}
