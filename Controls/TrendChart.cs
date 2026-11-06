using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Tman.Core;

namespace Tman.Controls;

/// <summary>A number over time with its straight-line trend, for seeing whether something really keeps climbing.</summary>
public sealed class TrendChart : FrameworkElement
{
    IReadOnlyList<TrendPoint> _points = Array.Empty<TrendPoint>();
    Trend? _trend;
    Func<double, string> _format = v => v.ToString("0");

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public void Show(IReadOnlyList<TrendPoint> points, Trend? trend, Func<double, string> format)
    {
        _points = points;
        _trend = trend;
        _format = format;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        const double left = 70, top = 8, bottom = 18, right = 8;
        double plotW = w - left - right, plotH = h - top - bottom;
        if (plotW < 20 || plotH < 20) return;

        var subtle = (Brush)FindResource("SubtleBrush");
        var grid = new Pen((Brush)FindResource("CardStrokeBrush"), 1);
        grid.Freeze();
        for (int i = 0; i <= 2; i++)
        {
            double y = Math.Round(top + plotH * i / 2) + 0.5;
            dc.DrawLine(grid, new Point(left, y), new Point(w - right, y));
        }
        if (_points.Count < 2) return;

        double x0 = _points[0].Hours, x1 = _points[^1].Hours;
        double min = _points.Min(p => p.Value), max = _points.Max(p => p.Value);
        if (max - min < Math.Max(1, max * 0.02))
        {
            double middle = (max + min) / 2, room = Math.Max(1, max * 0.05);
            (min, max) = (middle - room, middle + room);
        }
        Point At(double hours, double value) => new(left + (x1 > x0 ? (hours - x0) / (x1 - x0) : 0) * plotW, top + (1 - (value - min) / (max - min)) * plotH);

        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(At(_points[0].Hours, _points[0].Value), false, false);
            foreach (var p in _points.Skip(1)) c.LineTo(At(p.Hours, p.Value), true, false);
        }
        line.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(left, top, plotW, plotH)));
        dc.DrawGeometry(null, new Pen(Stroke, 1.5), line);
        if (_trend is { } t)
        {
            var dash = new Pen((Brush)FindResource("LabelBrush"), 1) { DashStyle = DashStyles.Dash };
            dash.Freeze();
            dc.DrawLine(dash, At(x0, t.Intercept + t.Slope * x0), At(x1, t.Intercept + t.Slope * x1));
        }
        dc.Pop();

        void Label(string text, double x, double y, TextAlignment align)
        {
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Led.Face, 10, subtle, null, TextFormattingMode.Display, 1.0) { TextAlignment = align };
            dc.DrawText(label, new Point(x, y));
        }
        Label(_format(max), left - 6, top - 6, TextAlignment.Right);
        Label(_format(min), left - 6, top + plotH - 6, TextAlignment.Right);
        Label($"{Fmt.Span(TimeSpan.FromHours(x1 - x0))} ago", left, h - 14, TextAlignment.Left);
        Label("now", w - right, h - 14, TextAlignment.Right);
    }
}
