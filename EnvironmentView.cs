using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Sada;

public sealed class EnvironmentView : FrameworkElement
{
    private const double Tau = Math.PI * 2;
    private static readonly Color Midnight = Color.FromRgb(7, 13, 24);
    private static readonly Color DarkSlate = Color.FromRgb(13, 23, 38);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double elapsed;
    private double phase;
    private double level;
    private double pulse;
    private Color primary;
    private Color secondary;
    private Color accent;

    public EnvironmentView()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public new int Style { get; set; }
    public AudioFrame? Frame { get; set; }
    public VisualSettings Settings { get; set; } = new();

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;

        var time = clock.Elapsed.TotalSeconds;
        var duration = Math.Clamp(time - elapsed, 0, .12);
        elapsed = time;
        phase += duration * (double.IsFinite(Settings.Speed) ? Math.Clamp(Settings.Speed, .15, 4) : 1);
        var target = Math.Clamp(Frame?.Level ?? 0, 0, 1);
        level += (target - level) * (1 - Math.Exp(-duration * 8));
        pulse = Math.Clamp(Frame?.Beat ?? 0, 0, 1);
        (primary, secondary, accent) = Visualizer.ResolvePalette(Settings);

        switch (Style)
        {
            case 1: DrawTides(drawing, width, height); break;
            case 2: DrawSkyline(drawing, width, height); break;
            default: DrawDusk(drawing, width, height); break;
        }
    }

    private void DrawDusk(DrawingContext drawing, double width, double height)
    {
        var horizon = height * .64;
        var bounds = new Rect(0, 0, width, height);
        var evening = Mix(Midnight, accent, .21 + level * .08);
        drawing.DrawRectangle(Vertical((Midnight, 0), (evening, .56), (DarkSlate, 1)), null, bounds);

        var scale = Math.Min(width, height);
        DrawStars(drawing, width, horizon, 78, accent);
        var sunColor = Mix(Color.FromRgb(251, 178, 169), accent, .46);
        var sunCenter = new Point(width * (.62 + Math.Sin(phase * .09) * .023), height * .42);
        var sun = scale * (.105 + pulse * .014 + level * .016);
        drawing.DrawEllipse(Soft(sunColor, .22 + pulse * .12), null, sunCenter, sun * 2.9, sun * 2.9);
        drawing.DrawEllipse(Vertical((Mix(sunColor, Colors.White, .35), 0), (Alpha(sunColor, .56), 1)), null, sunCenter, sun, sun);

        for (var layer = 0; layer < 5; layer++)
        {
            var floor = height * (.622 + layer * .072);
            var wave = new Point[63];
            for (var i = 0; i <= 60; i++)
            {
                var t = i / 60.0;
                var hills = Math.Sin(t * (Tau * 1.42) + .88 * layer) * .052;
                hills += Math.Cos(t * 20.1 - layer * 1.3) * .032;
                hills += Math.Sin(t * 54 + layer * .9) * .009;
                var peak = Math.Pow(Math.Max(0, Math.Sin(t * (Tau * .72) + 1.4 + layer * .9)), 5) * .10;
                wave[i] = new Point(t * width, floor + height * (hills - peak));
            }
            wave[61] = new Point(width, height);
            wave[62] = new Point(0, height);
            var tint = Mix(Midnight, layer > 2 ? primary : accent, .24 - .033 * layer);
            var stroke = new Pen(ColorBrush(layer > 2 ? primary : accent, .16 - .021 * layer), Math.Max(.65, scale * .0017));
            drawing.DrawGeometry(Vertical((tint, 0), (Mix(Midnight, tint, .4), 1)), stroke, Shape(wave, true));
        }
    }

    private void DrawTides(DrawingContext drawing, double width, double height)
    {
        var bounds = new Rect(0, 0, width, height);
        drawing.DrawRectangle(Vertical((Midnight, 0), (Mix(DarkSlate, secondary, .17), .55), (Midnight, 1)), null, bounds);
        var scale = Math.Min(width, height);
        var center = new Point(width * (.50 + Math.Cos(phase * .18) * .15), height * (.38 + Math.Sin(phase * .12) * .08));
        drawing.DrawEllipse(Soft(secondary, .13 + level * .23), null, center, width * .48, height * .47);
        DrawStars(drawing, width, height, 56, primary);

        for (var row = 0; row < 8; row++)
        {
            var frequency = .09 + row * .115;
            var amplitude = .036 + row * .008 + Spectrum(frequency) * .073;
            var depth = row * .081;
            var fringe = new Point[63];
            for (var i = 0; i <= 60; i++)
            {
                var t = i / 60.0;
                var current = Math.Sin(t * Tau * (1.11 + row * .13) + row * .69 - phase * (.40 + row * .07));
                current += Math.Sin(t * Tau * 2.7 - phase * .98 + row) * .29;
                fringe[i] = new Point(t * width, height * (.26 + depth + current * amplitude));
            }
            fringe[61] = new Point(width, height);
            fringe[62] = new Point(0, height);
            var color = Mix(primary, accent, row / 7.0);
            var wash = Vertical((Alpha(color, .04 + level * .08), 0), (Alpha(color, .013), .68), (Alpha(Midnight, 0), 1));
            var contour = new Pen(ColorBrush(color, .14 + level * .20), Math.Max(.8, scale * (.0015 + pulse * .0009)));
            drawing.DrawGeometry(wash, null, Shape(fringe, true));
            drawing.DrawGeometry(null, contour, Shape(fringe.AsSpan(0, 61), false));
        }
    }

    private void DrawSkyline(DrawingContext drawing, double width, double height)
    {
        var baseline = height * .88;
        var bounds = new Rect(0, 0, width, height);
        var night = Mix(DarkSlate, secondary, .19 + level * .13);
        drawing.DrawRectangle(Vertical((Midnight, 0), (night, .68), (Midnight, 1)), null, bounds);
        DrawStars(drawing, width, height * .58, 67, accent);
        drawing.DrawEllipse(Soft(primary, .12 + pulse * .11), null, new Point(width * .65, baseline * .79), width * .51, height * .40);

        var buildings = Math.Clamp((int)(width / 38), 10, 44);
        var step = width / buildings;
        for (var layer = 0; layer < 2; layer++)
        {
            var ground = baseline + layer * height * .06;
            var fill = ColorBrush(Mix(Midnight, secondary, layer == 0 ? .115 : .035), 1);
            var shade = layer == 0 ? accent : primary;
            for (var building = 0; building < buildings; building++)
            {
                var seed = building + layer * 83;
                var scale = .45 + Unit(seed, 17) * .52;
                var tall = Unit(seed, 23);
                var x = (building + .5 - scale * .5 + (layer == 0 ? 0 : .40)) * step;
                var towerWidth = step * scale;
                var towerHeight = height * ((layer == 0 ? .13 : .095) + tall * tall * (layer == 0 ? .32 : .22));
                var rooftop = ground - towerHeight;
                drawing.DrawRectangle(fill, null, new Rect(x, rooftop, towerWidth, towerHeight));

                var band = Spectrum((building + .5) / buildings);
                var floors = Math.Clamp((int)(towerHeight / 9), 3, 13);
                var columns = Math.Clamp((int)(towerWidth / 6.0), 2, 5);
                var roomWidth = towerWidth / (columns + .3);
                var roomHeight = towerHeight / (floors + .6);
                var light = ColorBrush(shade, Math.Clamp(.19 + band * .54, 0, 1));
                var bright = ColorBrush(shade, Math.Clamp(.32 + band * .63, 0, 1));
                for (var floor = 0; floor < floors; floor++)
                for (var column = 0; column < columns; column++)
                {
                    var activity = Unit(seed * 53 + 7 * floor + column, 63);
                    if (activity < .64 - band * .39) continue;
                    drawing.DrawRectangle(activity >= .91 ? bright : light, null, new Rect(x + (.35 + column) * roomWidth,
                        rooftop + (.67 + floor) * roomHeight, roomWidth * .33, roomHeight * .28));
                }
                if (tall > .84 && layer == 0)
                {
                    var tip = new Point(x + towerWidth * .6, Math.Max(0, rooftop - height * .027));
                    drawing.DrawLine(new Pen(ColorBrush(shade, .20), 1), tip, new Point(tip.X, rooftop));
                    drawing.DrawEllipse(ColorBrush(shade, .28 + pulse * .49), null, tip, 1.55, 1.55);
                }
            }
        }
        drawing.DrawRectangle(Vertical((Alpha(Midnight, .22), 0), (Midnight, 1)), null, new Rect(0, baseline, width, height - baseline));
        drawing.DrawLine(new Pen(ColorBrush(primary, .18 + pulse * .19), 1), new Point(0, baseline * 1.068), new Point(width, baseline * 1.068));
    }

    private void DrawStars(DrawingContext drawing, double width, double height, int count, Color color)
    {
        var scale = Math.Min(width, height);
        var dim = ColorBrush(color, .13);
        var bright = ColorBrush(color, .33 + level * .17);
        for (var i = 1; i <= count; i++)
        {
            var twinkle = Math.Sin(phase * (.39 + Unit(i, 29)) + i * .54);
            var radius = Math.Clamp(scale * (.0012 + Unit(i, 57) * .0016), .47, 1.85);
            var point = new Point(Unit(i, 13) * width, (.035 + Unit(i, 51) * .90) * height);
            drawing.DrawEllipse(twinkle > .65 ? bright : dim, null, point, radius, radius);
        }
    }

    private double Spectrum(double fraction)
    {
        var spectrum = Frame?.Spectrum;
        if (spectrum is null || spectrum.Length == 0) return 0;
        var index = (int)(Math.Clamp(fraction, 0, 1) * (spectrum.Length - 1));
        return Math.Clamp(spectrum[index], 0, 1);
    }

    private static StreamGeometry Shape(ReadOnlySpan<Point> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(points[0], closed, closed);
            foreach (var point in points[1..]) stream.LineTo(point, true, false);
        }
        return geometry;
    }

    private static LinearGradientBrush Vertical(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(.5, 0), EndPoint = new Point(.5, 1) };
        foreach (var stop in stops) brush.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        return brush;
    }

    private static RadialGradientBrush Soft(Color color, double strength) => new(Alpha(color, strength), Alpha(color, 0));

    private static SolidColorBrush ColorBrush(Color color, double opacity) => new(Alpha(color, opacity));

    private static Color Alpha(Color color, double opacity) => Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);

    private static Color Mix(Color first, Color second, double fraction) => Color.FromRgb(
        (byte)(first.R + (second.R - first.R) * fraction),
        (byte)(first.G + (second.G - first.G) * fraction),
        (byte)(first.B + (second.B - first.B) * fraction));

    private static double Unit(int seed, int salt)
    {
        var value = Math.Sin(seed * 12.9898 + salt * 78.233) * 43758.5453;
        return value - Math.Floor(value);
    }
}
