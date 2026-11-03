using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Tman.Controls;

/// <summary>Shared colors, brushes and text helpers for the LED-style instruments.</summary>
internal static class Led
{
    static readonly Dictionary<uint, SolidColorBrush> Cache = new();
    public static readonly Typeface Face = new(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface Bold = new(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    static readonly Color Green = Color.FromRgb(0x3D, 0xDC, 0x84);
    static readonly Color Amber = Color.FromRgb(0xFF, 0xD3, 0x4D);
    static readonly Color Red = Color.FromRgb(0xFF, 0x5A, 0x5A);

    /// <summary>Frozen, cached brush for a color (instruments repaint every second, so this saves a lot of garbage).</summary>
    public static SolidColorBrush Brush(Color c)
    {
        uint key = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
        if (!Cache.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            Cache[key] = b;
        }
        return b;
    }

    public static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color ColorOf(Brush? b) => (b as SolidColorBrush)?.Color ?? Color.FromRgb(0x3F, 0xC8, 0xFF);

    /// <summary>Classic meter scale: green, then amber, then red near the top.</summary>
    public static Color Scale(double t) => t < 0.6 ? Green : t < 0.85 ? Amber : Red;

    public static FormattedText Text(string s, double size, Brush brush, double dpi, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? Bold : Face, size, brush, dpi);
}

/// <summary>VU-meter peak hold: jumps up at once, holds for a few updates, then falls back gradually.</summary>
internal sealed class PeakHold
{
    int _hold;

    public double Value { get; private set; }

    public void Push(double v)
    {
        if (v >= Value)
        {
            Value = v;
            _hold = 3;
        }
        else if (_hold > 0) _hold--;
        else Value = Math.Max(v, Value - 0.06);
    }
}
