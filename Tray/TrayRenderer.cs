using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tman.Controls;

namespace Tman.Tray;

/// <summary>Draws tray icons (a number with a level strip, an LED meter, or one icon of bars) and turns them into HICONs.</summary>
internal static class TrayRenderer
{
    static readonly Typeface Bold = new(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Color Face = Color.FromRgb(0x0E, 0x11, 0x15);
    static readonly Color Ink = Color.FromRgb(0xF2, 0xF5, 0xF8);

    /// <summary>Renders at <paramref name="size"/> × <paramref name="size"/> physical pixels.</summary>
    public static BitmapSource Render(TrayReading? r, Color color, bool meter, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double s = size, radius = Math.Max(2, s * 0.16);
            var frame = new Rect(0.5, 0.5, s - 1, s - 1);
            dc.DrawRoundedRectangle(Led.Brush(Face), new Pen(Led.Brush(Led.Alpha(color, 0xD0)), 1), frame, radius, radius);
            double level = r == null ? 0 : Math.Clamp(double.IsFinite(r.Level) ? r.Level : 0, 0, 1);
            if (meter) DrawMeter(dc, s, level, color);
            else DrawNumber(dc, s, r?.Text ?? "--", level, color);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>One icon with a bar per metric, each in its own color. Whole pixels only, so it stays sharp at 16 px.</summary>
    public static BitmapSource RenderBars(IReadOnlyList<(double Level, Color Color)> bars, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double radius = Math.Max(2, size * 0.16);
            dc.DrawRoundedRectangle(Led.Brush(Face), new Pen(Led.Brush(Led.Alpha(Ink, 0x60)), 1), new Rect(0.5, 0.5, size - 1, size - 1), radius, radius);

            const int air = 2; // 1 px frame + 1 px clear space
            int room = size - 2 * air, gap = size >= 24 ? 2 : 1, count = Math.Max(1, bars.Count);
            int width = (room - (count - 1) * gap) / count;
            int x = air + (room - (count * width + (count - 1) * gap)) / 2;
            foreach (var (level, color) in bars)
            {
                int height = BarHeight(level, size);
                dc.DrawRectangle(Led.Brush(Led.Alpha(color, 0x30)), null, new Rect(x, air, width, room));
                if (height > 0) dc.DrawRectangle(Led.Brush(color), null, new Rect(x, air + room - height, width, height));
                x += width + gap;
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A bar's filled height in pixels. Anything above idle shows at least one pixel, so it never looks switched off.</summary>
    public static int BarHeight(double level, int size)
    {
        int room = size - 4;
        double clamped = double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        int height = (int)Math.Round(clamped * room);
        return height == 0 && clamped > 0.02 ? 1 : height;
    }

    static void DrawNumber(DrawingContext dc, double s, string text, double level, Color color)
    {
        // Level strip along the bottom edge, inside the frame.
        double strip = Math.Max(2, Math.Round(s / 8)), inset = 2;
        double width = s - 2 * inset, y = s - inset - strip;
        dc.DrawRectangle(Led.Brush(Led.Alpha(color, 0x38)), null, new Rect(inset, y, width, strip));
        if (level > 0) dc.DrawRectangle(Led.Brush(color), null, new Rect(inset, y, Math.Max(1, Math.Round(width * level)), strip));

        // Largest bold size that fits; hinted ("Display") glyphs stay crisp at 16 px.
        double room = s - 3, height = y - 1;
        FormattedText ft;
        double em = s * 0.66;
        do
        {
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, em, Led.Brush(Ink),
                null, TextFormattingMode.Display, 1.0);
            em -= 0.5;
        } while ((ft.Width > room || ft.Extent > height) && em > 5);
        // Centre the ink (not the line box) in the space above the strip.
        double inkTop = ft.Height + ft.OverhangAfter - ft.Extent;
        double x = Math.Round((s - ft.Width) / 2);
        double yText = Math.Round(1 + (height - ft.Extent) / 2 - inkTop);
        dc.DrawText(ft, new Point(x, yText));
    }

    static void DrawMeter(DrawingContext dc, double s, double level, Color color)
    {
        // Whole-pixel segments so nothing smears at 16-24 px: 2 px bars (3 px from 24 px up) with 1 px gaps.
        int size = (int)s, segH = size >= 24 ? 3 : 2, gap = 1;
        int avail = size - 4; // 1 px frame + 1 px air top and bottom
        int segments = Math.Clamp((avail + gap) / (segH + gap), 3, 10);
        int used = segments * segH + (segments - 1) * gap;
        int bottom = 2 + avail - (avail - used) / 2;
        int inset = Math.Max(3, (int)Math.Round(s * 0.22));
        int lit = (int)Math.Round(level * segments);
        if (lit == 0 && level > 0.02) lit = 1;
        for (int i = 0; i < segments; i++)
        {
            int y = bottom - (i + 1) * segH - i * gap;
            var brush = Led.Brush(i < lit ? color : Led.Alpha(color, 0x30));
            dc.DrawRectangle(brush, null, new Rect(inset, y, size - 2 * inset, segH));
        }
    }

    // ---- HICON ----

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    /// <summary>Converts a rendered icon into an HICON with a real alpha channel. The caller destroys it.</summary>
    public static IntPtr ToHIcon(BitmapSource source)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        var straight = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); // icons want un-premultiplied alpha
        var pixels = new byte[w * h * 4];
        straight.CopyPixels(pixels, w * 4, 0);

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        var mask = CreateBitmap(w, h, 1, 1, IntPtr.Zero);
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }
}
