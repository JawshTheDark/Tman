using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Tman.Core;
using Tman.Monitoring;

namespace Tman.Controls;

/// <summary>
/// Flight recorder view: stacked lanes (CPU, memory, GPU, disk, network, power) over a time window, a strip of
/// process start/exit ticks, and a time axis. Hover to read values; click or drag to pick a moment.
/// </summary>
public sealed class Timeline : FrameworkElement
{
    enum LaneScale { Percent, Bytes, Bits, Watts }

    sealed record Lane(string Title, string BrushKey, LaneScale Scale, Func<FlightFrame, double> Value, Func<FlightFrame, string>? Extra = null);

    const double LaneHeight = 36, LaneGap = 5, EventsHeight = 16, AxisHeight = 18;
    static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(6);
    static readonly Typeface Face = new("Segoe UI");

    static readonly Lane[] BaseLanes =
    {
        new("CPU", "CpuBrush", LaneScale.Percent, f => f.Cpu, f => f.Ghz > 0 ? $"{f.Ghz:0.00} GHz" : ""),
        new("Memory", "MemBrush", LaneScale.Percent, f => f.Memory, f => Fmt.Bytes(f.MemoryUsed)),
        new("GPU", "GpuBrush", LaneScale.Percent, f => f.Gpu),
        new("Disk", "DiskBrush", LaneScale.Bytes, f => f.DiskBps),
        new("Network", "NetBrush", LaneScale.Bits, f => f.NetBps),
    };
    static readonly Lane PowerLane = new("CPU power", "PowerBrush", LaneScale.Watts, f => f.Watts);

    DateTime? _hover;
    DateTime _viewStart, _viewEnd;
    bool _dragging;

    public IReadOnlyList<FlightFrame> Frames { get; set; } = Array.Empty<FlightFrame>();
    public IReadOnlyList<FlightEvent> Events { get; set; } = Array.Empty<FlightEvent>();
    public TimeSpan Span { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Right edge of the view; null follows the newest sample.</summary>
    public DateTime? End { get; set; }
    public DateTime? Selected { get; set; }
    /// <summary>The right edge actually drawn last time (what <see cref="End"/> resolves to).</summary>
    public DateTime ViewEnd => _viewEnd;

    /// <summary>Raised when the user clicks or drags to a moment.</summary>
    public event Action<DateTime>? Picked;

    public Timeline()
    {
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    List<Lane> Lanes()
    {
        var lanes = BaseLanes.ToList();
        if (Frames.Count > 0 && Frames[^1].Watts > 0) lanes.Add(PowerLane);
        return lanes;
    }

    protected override Size MeasureOverride(Size available)
    {
        double h = Lanes().Count * (LaneHeight + LaneGap) + EventsHeight + AxisHeight;
        return new Size(double.IsInfinity(available.Width) ? 600 : available.Width, h);
    }

    public void Redraw()
    {
        if (Math.Abs(DesiredSize.Height - (Lanes().Count * (LaneHeight + LaneGap) + EventsHeight + AxisHeight)) > 0.5) InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 20) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable everywhere, gaps included
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

        var frames = Frames;
        _viewEnd = End ?? (frames.Count > 0 ? frames[^1].Time : DateTime.Now);
        _viewStart = _viewEnd - Span;
        double secs = Span.TotalSeconds;
        double X(DateTime t) => (t - _viewStart).TotalSeconds / secs * w;

        int first = Math.Max(0, LowerBound(frames, _viewStart) - 1);
        int last = Math.Min(frames.Count - 1, LowerBound(frames, _viewEnd));
        var labelFrame = Nearest(frames, _hover ?? Selected ?? _viewEnd);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x1C, 0x1C, 0x1C)));
        var text = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
        var subtle = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));

        var lanes = Lanes();
        double y = 0;
        double lanesBottom = lanes.Count * (LaneHeight + LaneGap) - LaneGap;
        DrawGrid(dc, w, lanesBottom + EventsHeight, h, dpi, subtle, X);

        foreach (var lane in lanes)
        {
            var rect = new Rect(0, y, w, LaneHeight);
            var color = (FindResource(lane.BrushKey) as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
            dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromArgb(0x12, color.R, color.G, color.B))), null, rect);

            // Shade logged spikes in the lane they belong to.
            var spikeKind = lane.Title == "CPU" ? FlightEventKind.HighCpu : lane.Title == "Memory" ? FlightEventKind.HighMemory : (FlightEventKind?)null;
            if (spikeKind != null)
            {
                var shade = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x8A, 0x80)));
                foreach (var e in Events)
                {
                    if (e.Kind != spikeKind || e.End is not { } end || end < _viewStart || e.Time > _viewEnd) continue;
                    double x0 = Math.Max(0, X(e.Time)), x1 = Math.Min(w, X(end));
                    dc.DrawRectangle(shade, null, new Rect(x0, y, Math.Max(1, x1 - x0), LaneHeight));
                }
            }

            double peak = 0;
            for (int i = first; i <= last; i++) peak = Math.Max(peak, lane.Value(frames[i]));
            double max = lane.Scale switch
            {
                LaneScale.Percent => 100,
                LaneScale.Bytes => LineGraph.NiceBytes(peak),
                LaneScale.Bits => LineGraph.NiceBits(peak),
                _ => Math.Max(25, Math.Ceiling(peak * 1.1 / 25) * 25),
            };
            DrawSeries(dc, frames, first, last, lane.Value, max, rect, color, X);
            dc.DrawRectangle(null, new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B))), 1),
                new Rect(0.5, y + 0.5, w - 1, LaneHeight - 1));

            var value = labelFrame == null ? "" : Format(lane.Scale, lane.Value(labelFrame));
            var extra = labelFrame != null && lane.Extra != null ? lane.Extra(labelFrame) : "";
            Label(dc, $"{lane.Title}  {value}" + (extra != "" ? $"  ·  {extra}" : ""), new Point(6, y + 3), text, labelBg, dpi, false);
            if (lane.Scale != LaneScale.Percent)
                Label(dc, Format(lane.Scale, max), new Point(w - 6, y + 3), subtle, labelBg, dpi, true);
            y += LaneHeight + LaneGap;
        }

        // Process starts (green, upper half) and exits (red, lower half).
        y -= LaneGap;
        var strip = new Rect(0, y + 2, w, EventsHeight - 2);
        dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF))), null, strip);
        var startPen = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x6C, 0xCB, 0x5F))), 1);
        var exitPen = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0x8A, 0x80))), 1);
        double mid = strip.Top + strip.Height / 2;
        foreach (var e in Events)
        {
            if (e.IsSpike || e.Time < _viewStart || e.Time > _viewEnd) continue;
            double x = Math.Round(X(e.Time)) + 0.5;
            if (e.Kind == FlightEventKind.Started) dc.DrawLine(startPen, new Point(x, strip.Top), new Point(x, mid));
            else dc.DrawLine(exitPen, new Point(x, mid), new Point(x, strip.Bottom));
        }
        double stripBottom = strip.Bottom;

        if (Selected is { } sel && sel >= _viewStart && sel <= _viewEnd)
        {
            double x = Math.Round(X(sel)) + 0.5;
            var accent = (Brush)FindResource("AccentBrush");
            dc.DrawLine(new Pen(accent, 2), new Point(x, 0), new Point(x, stripBottom));
        }
        if (_hover is { } hv)
        {
            double x = Math.Round(X(hv)) + 0.5;
            var pen = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF))), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(pen, new Point(x, 0), new Point(x, stripBottom));
            var at = labelFrame?.Time ?? hv;
            var ft = Text(at.ToString("T"), text, dpi, 11);
            double lx = Math.Clamp(x - ft.Width / 2, 0, w - ft.Width);
            dc.DrawRoundedRectangle(Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A))), null, new Rect(lx - 4, h - AxisHeight + 1, ft.Width + 8, ft.Height + 1), 3, 3);
            dc.DrawText(ft, new Point(lx, h - AxisHeight + 1.5));
        }
        dc.Pop();
    }

    /// <summary>Vertical grid lines and time labels at a step that fits the window.</summary>
    void DrawGrid(DrawingContext dc, double w, double gridBottom, double h, double dpi, Brush label, Func<DateTime, double> X)
    {
        int[] stepsMin = { 1, 2, 5, 10, 15, 30, 60 };
        double minutes = Span.TotalMinutes;
        int step = stepsMin.FirstOrDefault(s => minutes / s <= Math.Max(3, w / 110), 60);
        var t = new DateTime(_viewStart.Year, _viewStart.Month, _viewStart.Day, _viewStart.Hour, 0, 0, _viewStart.Kind);
        while (t < _viewStart) t = t.AddMinutes(step);
        var pen = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))), 1);
        for (; t <= _viewEnd; t = t.AddMinutes(step))
        {
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(pen, new Point(x, 0), new Point(x, gridBottom));
            var ft = Text(t.ToString("t"), label, dpi, 11);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, 0, w - ft.Width), h - AxisHeight + 2));
        }
    }

    static void DrawSeries(DrawingContext dc, IReadOnlyList<FlightFrame> frames, int first, int last, Func<FlightFrame, double> value,
        double max, Rect r, Color color, Func<DateTime, double> X)
    {
        if (last < first) return;
        double Y(double v) => r.Bottom - 1 - Math.Clamp(v / max, 0, 1) * (r.Height - 2);
        var fill = new StreamGeometry();
        var line = new StreamGeometry();
        var segment = new List<Point>();
        using (var fc = fill.Open())
        using (var lc = line.Open())
        {
            void Flush()
            {
                if (segment.Count >= 2)
                {
                    fc.BeginFigure(new Point(segment[0].X, r.Bottom), true, true);
                    foreach (var p in segment) fc.LineTo(p, false, false);
                    fc.LineTo(new Point(segment[^1].X, r.Bottom), false, false);
                    lc.BeginFigure(segment[0], false, false);
                    for (int i = 1; i < segment.Count; i++) lc.LineTo(segment[i], true, true);
                }
                segment.Clear();
            }

            // Keep the max per pixel column so short spikes survive when an hour is squeezed into a few hundred pixels.
            int column = int.MinValue;
            for (int i = first; i <= last; i++)
            {
                var f = frames[i];
                if (i > first && f.Time - frames[i - 1].Time > GapThreshold)
                {
                    Flush();
                    column = int.MinValue;
                }
                double x = X(f.Time);
                int c = (int)Math.Floor(x);
                double y = Y(value(f));
                if (c == column)
                {
                    if (y < segment[^1].Y) segment[^1] = new Point(segment[^1].X, y);
                }
                else
                {
                    column = c;
                    segment.Add(new Point(x, y));
                }
            }
            Flush();
        }
        fill.Freeze();
        line.Freeze();
        dc.DrawGeometry(Frozen(new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B))), null, fill);
        dc.DrawGeometry(null, new Pen(Frozen(new SolidColorBrush(color)), 1.2) { LineJoin = PenLineJoin.Round }, line);
    }

    static string Format(LaneScale scale, double v) => scale switch
    {
        LaneScale.Percent => $"{v:0}%",
        LaneScale.Bytes => Fmt.Rate(v),
        LaneScale.Bits => Fmt.Bits(v),
        _ => Fmt.Watts(v),
    };

    static FormattedText Text(string s, Brush brush, double dpi, double size) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, dpi);

    static void Label(DrawingContext dc, string s, Point at, Brush fg, Brush bg, double dpi, bool rightAlign)
    {
        var ft = Text(s, fg, dpi, 11);
        double x = rightAlign ? at.X - ft.Width : at.X;
        dc.DrawRoundedRectangle(bg, null, new Rect(x - 4, at.Y - 1, ft.Width + 8, ft.Height + 2), 3, 3);
        dc.DrawText(ft, new Point(x, at.Y));
    }

    static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Index of the first frame at or after <paramref name="t"/>.</summary>
    static int LowerBound(IReadOnlyList<FlightFrame> frames, DateTime t)
    {
        int lo = 0, hi = frames.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (frames[mid].Time < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    static FlightFrame? Nearest(IReadOnlyList<FlightFrame> frames, DateTime t)
    {
        if (frames.Count == 0) return null;
        int i = Math.Min(LowerBound(frames, t), frames.Count - 1);
        if (i > 0 && t - frames[i - 1].Time < frames[i].Time - t) i--;
        return frames[i];
    }

    DateTime TimeAt(double x) => _viewStart + TimeSpan.FromSeconds(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * Span.TotalSeconds);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var t = TimeAt(e.GetPosition(this).X);
        _hover = t;
        if (_dragging) Picked?.Invoke(t);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = CaptureMouse();
        Picked?.Invoke(TimeAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }
}
