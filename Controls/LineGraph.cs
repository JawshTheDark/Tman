using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Controls;

public enum GraphScale { Percent, Bytes, Bits }

/// <summary>Scrolling area graph over a <see cref="History"/>. Repaints on every monitor update while loaded.</summary>
public sealed class LineGraph : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = Register(nameof(Data), typeof(History), null);
    public static readonly DependencyProperty Data2Property = Register(nameof(Data2), typeof(History), null);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush), Brushes.DodgerBlue);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), typeof(double), 100.0);
    public static readonly DependencyProperty ScaleProperty = Register(nameof(Scale), typeof(GraphScale), GraphScale.Percent);
    public static readonly DependencyProperty ShowGridProperty = Register(nameof(ShowGrid), typeof(bool), true);
    public static readonly DependencyProperty ShowScaleLabelProperty = Register(nameof(ShowScaleLabel), typeof(bool), false);

    static DependencyProperty Register(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(LineGraph), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public History? Data { get => (History?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    /// <summary>Optional second series, drawn dashed without fill (e.g. disk write, network send).</summary>
    public History? Data2 { get => (History?)GetValue(Data2Property); set => SetValue(Data2Property, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Fixed top of the scale; 0 or less auto-scales.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public GraphScale Scale { get => (GraphScale)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowScaleLabel { get => (bool)GetValue(ShowScaleLabelProperty); set => SetValue(ShowScaleLabelProperty, value); }

    public LineGraph()
    {
        SnapsToDevicePixels = true;
        Loaded += (_, _) => SystemMonitor.Instance.Updated += OnUpdated;
        Unloaded += (_, _) => SystemMonitor.Instance.Updated -= OnUpdated;
    }

    void OnUpdated(Snapshot _) => InvalidateVisual();

    /// <summary>The scale top currently in use (useful for labels outside the graph).</summary>
    public double CurrentMaximum { get; private set; } = 100;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var rect = new Rect(0, 0, w, h);

        // Scope look: near-black screen, dotted graticule, glowing trace with a bright head on the newest sample.
        dc.DrawRectangle(Led.Brush(Color.FromArgb(0xFF, 0x0B, 0x0E, 0x12)), null, rect);
        dc.DrawRectangle(Led.Brush(Led.Alpha(color, 0x0A)), null, rect);

        if (ShowGrid)
        {
            var gridPen = new Pen(Led.Brush(Led.Alpha(color, 0x38)), 1) { DashStyle = new DashStyle(new double[] { 1, 3 }, 0) };
            for (int i = 1; i < 10; i++)
            {
                double x = Math.Round(w * i / 10) + 0.5;
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }
            for (int i = 1; i < 5; i++)
            {
                double y = Math.Round(h * i / 5) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
        }

        double max = Maximum > 0 ? Maximum : AutoMax();
        CurrentMaximum = max;

        if (Data != null) DrawSeries(dc, Data, max, w, h, color, fill: true, dashed: false);
        if (Data2 != null) DrawSeries(dc, Data2, max, w, h, color, fill: false, dashed: true);

        dc.DrawRectangle(null, new Pen(Led.Brush(Led.Alpha(color, 0x70)), 1), new Rect(0.5, 0.5, w - 1, h - 1));

        if (ShowScaleLabel && Maximum <= 0)
        {
            var text = Led.Text(Scale == GraphScale.Bits ? Fmt.Bits(max) : Fmt.Rate(max), 11,
                Led.Brush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(w - text.Width - 6, 4));
        }
    }

    static void DrawSeries(DrawingContext dc, History data, double max, double w, double h, Color color, bool fill, bool dashed)
    {
        int n = data.Length;
        if (n < 2 || data.Count < 2) return;
        int first = n - data.Count; // only draw real samples; the graph fills in from the right like Task Manager
        double step = w / (n - 1);
        double Y(int i) => h - Math.Clamp(data[i] / max, 0, 1) * (h - 1);

        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(first * step, h), true, true);
                for (int i = first; i < n; i++) ctx.LineTo(new Point(i * step, Y(i)), false, false);
                ctx.LineTo(new Point(w, h), false, false);
            }
            area.Freeze();
            // Fill fades toward the baseline so busy stretches read as brighter.
            var gradient = new LinearGradientBrush(Led.Alpha(color, 0x60), Led.Alpha(color, 0x06), new Point(0, 0), new Point(0, h))
            {
                MappingMode = BrushMappingMode.Absolute,
            };
            gradient.Freeze();
            dc.DrawGeometry(gradient, null, area);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(first * step, Y(first)), false, false);
            for (int i = first + 1; i < n; i++) ctx.LineTo(new Point(i * step, Y(i)), true, true);
        }
        line.Freeze();
        if (dashed)
        {
            var pen = new Pen(Led.Brush(Led.Alpha(color, 0xD0)), 1.2) { LineJoin = PenLineJoin.Round, DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
            dc.DrawGeometry(null, pen, line);
            return;
        }
        dc.DrawGeometry(null, new Pen(Led.Brush(Led.Alpha(color, 0x38)), 4) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawGeometry(null, new Pen(Led.Brush(color), 1.5) { LineJoin = PenLineJoin.Round }, line);
        var head = new Point((n - 1) * step - 1, Math.Min(h - 2, Y(n - 1)));
        dc.DrawEllipse(Led.Brush(Led.Alpha(color, 0x50)), null, head, 4.5, 4.5);
        dc.DrawEllipse(Led.Brush(Color.FromRgb(0xFF, 0xFF, 0xFF)), null, head, 1.8, 1.8);
    }

    double AutoMax()
    {
        double m = Math.Max(Data?.Max() ?? 0, Data2?.Max() ?? 0);
        return Scale == GraphScale.Bits ? NiceBits(m) : NiceBytes(m);
    }

    /// <summary>Rounds up to a 1/2/5 step in the unit the value would be shown in.</summary>
    static double Nice(double v)
    {
        if (v <= 1) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
    }

    internal static double NiceBytes(double v)
    {
        v = Math.Max(v * 1.1, 10 * 1024);
        double unit = 1;
        while (v / unit >= 1000) unit *= 1024;
        return Nice(v / unit) * unit;
    }

    internal static double NiceBits(double bytesPerSec)
    {
        double bits = Math.Max(bytesPerSec * 8 * 1.1, 100_000);
        double unit = 1;
        while (bits / unit >= 1000) unit *= 1000;
        return Nice(bits / unit) * unit / 8;
    }
}
