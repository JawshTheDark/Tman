namespace Tman.Core;

public enum LeakVerdict { Watching, Stable, Rose, Suspect, Leak }

public readonly record struct TrendPoint(double Hours, double Value);

/// <summary>
/// One number over time. Points stay evenly spaced across the whole history: when the list fills up, the spacing doubles
/// and neighbors merge, so a week of watching takes no more room than a day, and old hours count as much as recent ones.
/// </summary>
public sealed class LeakSeries
{
    public const int MostPoints = 480;

    /// <summary>Seconds since the store began.</summary>
    public List<int> T { get; set; } = new();
    public List<long> V { get; set; } = new();

    /// <summary>Least time between kept points. The newest point stays current until the next one is due.</summary>
    public int Spacing { get; set; }

    public void Add(int seconds, long value)
    {
        if (T.Count >= 2 && seconds - T[^2] < Spacing)
        {
            T[^1] = seconds;
            V[^1] = value;
            return;
        }
        T.Add(seconds);
        V.Add(value);
        if (T.Count > MostPoints) Thin();
    }

    /// <summary>Merges neighboring pairs. Keeping the low of each preserves the floor, which is what shows a leak.</summary>
    void Thin()
    {
        var times = new List<int>(T.Count / 2 + 1);
        var values = new List<long>(T.Count / 2 + 1);
        int i = T.Count % 2;
        if (i == 1)
        {
            times.Add(T[0]);
            values.Add(V[0]);
        }
        for (; i + 1 < T.Count; i += 2)
        {
            times.Add(T[i + 1]);
            values.Add(i + 2 == T.Count ? V[i + 1] : Math.Min(V[i], V[i + 1]));
        }
        T = times;
        V = values;
        Spacing = Math.Max(Spacing * 2, (T[^1] - T[0]) / Math.Max(1, T.Count - 1));
    }

    public List<TrendPoint> Points(int fromSeconds)
    {
        var points = new List<TrendPoint>(T.Count);
        for (int i = 0; i < T.Count; i++)
        {
            if (T[i] >= fromSeconds) points.Add(new TrendPoint(T[i] / 3600.0, V[i]));
        }
        return points;
    }
}

/// <summary>How much growth matters for one kind of number. Below Noticeable it's noise, and a leak verdict needs Serious.</summary>
public sealed record LeakLimits(double Noticeable, double Serious)
{
    public static readonly LeakLimits Memory = new(30e6, 100e6);
    public static readonly LeakLimits Handles = new(500, 2000);
    public static readonly LeakLimits Threads = new(30, 100);
    public static readonly LeakLimits Pool = new(10e6, 50e6);
    public static readonly LeakLimits Commit = new(200e6, 500e6);
    public static readonly LeakLimits Zombies = new(200, 1000);
}

public sealed record Trend(LeakVerdict Verdict, double First, double Last, double Growth, double PerHour, double Fit, double Hours, double Intercept, double Slope);

/// <summary>
/// Tells a leak from everything else that makes a number go up. A leak keeps climbing in steps of time, its lowest point
/// rises along with it, and it hasn't levelled off. A cache rises and stops, a garbage-collected app saws up and down around
/// a flat floor, and a single big allocation is one jump. Each of those fails one of these tests.
/// </summary>
public static class LeakAnalysis
{
    /// <summary>Shorter than this, and growth says nothing. Tests shorten it.</summary>
    public static double MinHours { get; set; } = 0.5;

    const int MinPoints = 6, Buckets = 8;

    public static Trend Measure(IReadOnlyList<TrendPoint> points, LeakLimits limits)
    {
        if (points.Count == 0) return new Trend(LeakVerdict.Watching, 0, 0, 0, 0, 0, 0, 0, 0);
        double span = points[^1].Hours - points[0].Hours;
        var (slope, intercept, fit) = Line(points);
        int edge = Math.Max(1, points.Count / 7);
        double first = Median(points.Take(edge)), last = Median(points.Skip(points.Count - edge));
        double growth = last - first;
        double perHour = span > 0 ? growth / span : 0;
        LeakVerdict Result(LeakVerdict verdict) => verdict;
        var trend = new Trend(LeakVerdict.Watching, first, last, growth, perHour, fit, span, intercept, slope);
        if (points.Count < MinPoints || span < MinHours) return trend;
        if (growth < limits.Noticeable) return trend with { Verdict = LeakVerdict.Stable };

        int third = Math.Max(1, points.Count / 3);
        double earlyFloor = points.Take(third).Min(p => p.Value), lateFloor = points.Skip(points.Count - third).Min(p => p.Value);
        bool floorRises = lateFloor - earlyFloor >= growth * 0.5;

        var (lateSlope, _, _) = Line(points.Skip(points.Count / 2).ToList());
        bool stillRising = slope > 0 && lateSlope >= slope * 0.35;

        int risingSteps = RisingSteps(points);
        if (growth >= limits.Serious && fit >= 0.8 && floorRises && stillRising && risingSteps >= 5 && span >= MinHours * 2)
            return trend with { Verdict = Result(LeakVerdict.Leak) };
        if (floorRises && stillRising && risingSteps >= 4)
            return trend with { Verdict = LeakVerdict.Suspect };
        return trend with { Verdict = LeakVerdict.Rose };
    }

    /// <summary>Splits the time into equal parts and counts how many averaged higher than the part before. One big jump scores 1.</summary>
    static int RisingSteps(IReadOnlyList<TrendPoint> points)
    {
        double start = points[0].Hours, width = (points[^1].Hours - start) / Buckets;
        if (width <= 0) return 0;
        var means = points.GroupBy(p => Math.Min(Buckets - 1, (int)((p.Hours - start) / width))).OrderBy(g => g.Key).Select(g => g.Average(p => p.Value)).ToList();
        int rising = 0;
        for (int i = 1; i < means.Count; i++)
        {
            if (means[i] > means[i - 1]) rising++;
        }
        return rising;
    }

    static (double Slope, double Intercept, double Fit) Line(IReadOnlyList<TrendPoint> points)
    {
        int n = points.Count;
        if (n < 2) return (0, n == 1 ? points[0].Value : 0, 0);
        double meanX = points.Average(p => p.Hours), meanY = points.Average(p => p.Value);
        double xx = 0, xy = 0, yy = 0;
        foreach (var p in points)
        {
            double dx = p.Hours - meanX, dy = p.Value - meanY;
            xx += dx * dx;
            xy += dx * dy;
            yy += dy * dy;
        }
        if (xx <= 0) return (0, meanY, 0);
        double slope = xy / xx;
        return (slope, meanY - slope * meanX, yy > 0 ? xy * xy / (xx * yy) : 0);
    }

    static double Median(IEnumerable<TrendPoint> points)
    {
        var values = points.Select(p => p.Value).OrderBy(v => v).ToList();
        return values[values.Count / 2];
    }

    public static string Name(LeakVerdict verdict) => verdict switch
    {
        LeakVerdict.Leak => "Likely leak",
        LeakVerdict.Suspect => "Suspect",
        LeakVerdict.Rose => "Rose, then eased",
        LeakVerdict.Stable => "Stable",
        _ => "Watching",
    };

    public static string Explain(Trend t) => t.Verdict switch
    {
        LeakVerdict.Leak => "Climbing steadily, its lowest point is rising too, and it hasn't levelled off. That's how a leak looks.",
        LeakVerdict.Suspect => "Climbing, and not levelling off so far, but not yet steady or large enough to be sure. Give it more time.",
        LeakVerdict.Rose => "It went up, but in a jump or a burst that then slowed. That usually means a cache or a big job, not a leak.",
        LeakVerdict.Stable => "Holding steady.",
        _ => "Not enough history yet to judge.",
    };
}
