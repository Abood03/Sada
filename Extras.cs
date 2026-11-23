using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Sada;

public sealed class Extras : FrameworkElement
{
    private const double Tau = Math.PI * 2;
    private const double TrailLife = .64;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly EffectInk ink = new();
    private readonly Snowflake[] snowflakes = new Snowflake[200];
    private readonly Bird[] flock = new Bird[66];
    private readonly Bird[] nextFlock = new Bird[66];
    private readonly TrailPoint[] trail = new TrailPoint[58];
    private readonly Point[] ribbon = new Point[62];
    private double previousTime;
    private double phase;
    private double energy;
    private double beat;
    private int trailHead;
    private int trailCount;
    private bool pointerPresent;

    public Extras()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        var random = new Random(4264);
        for (var i = 0; i < snowflakes.Length; i++)
        {
            snowflakes[i] = new Snowflake(random.NextDouble(), random.NextDouble(),
                random.NextDouble(), random.NextDouble() * Tau, random.NextDouble());
        }

        for (var i = 0; i < flock.Length; i++)
        {
            var angle = random.NextDouble() * Tau;
            flock[i] = new Bird(new Point(.2 + random.NextDouble() * .6, .22 + random.NextDouble() * .56),
                new Vector(Math.Cos(angle), Math.Sin(angle)) * .12);
        }
    }

    public WidgetKind Kind { get; set; }
    public AudioFrame? Frame { get; set; }
    public VisualSettings Settings { get; set; } = new();
    public Point Pointer { get; set; } = new(double.NaN, double.NaN);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 2 || ActualHeight < 2)
            return;

        ink.Update(Settings);
        var now = clock.Elapsed.TotalSeconds;
        var elapsed = Math.Clamp(now - previousTime, 0, .08);
        previousTime = now;
        var step = elapsed * EffectInk.Finite(Settings.Speed, .15, 4, 1);
        phase += step;
        var smoothing = EffectInk.Finite(Settings.Smoothing, 0, 1, .58);
        var gain = EffectInk.Finite(Settings.Sensitivity, .1, 4, 1);
        var target = Math.Clamp((Frame?.Level ?? 0) * gain, 0, 1);
        energy += (target - energy) * (1 - Math.Exp(-elapsed / (.045 + smoothing * .25)));
        beat = Math.Clamp((Frame?.Beat ?? 0) * gain, 0, 1);

        drawing.PushOpacity(EffectInk.Finite(Settings.Opacity, 0, 1, 1));
        switch (Kind)
        {
            case WidgetKind.Snow: DrawSnow(drawing, step); break;
            case WidgetKind.CursorTrail: DrawTrail(drawing, now); break;
            case WidgetKind.Flock: DrawFlock(drawing, step); break;
            case WidgetKind.Waves: DrawWaves(drawing); break;
            case WidgetKind.Fluid: DrawFluid(drawing); break;
        }
        drawing.Pop();
    }

    private void DrawSnow(DrawingContext drawing, double step)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var density = Math.Clamp(Settings.Count, 8, 120);
        var count = Math.Min(snowflakes.Length, density * (width * height > 520000 ? 2 : 1));
        var scale = Math.Clamp(Math.Sqrt(width * height) / 420, .65, 2.6);
        var glow = EffectInk.Finite(Settings.Glow, 0, 1, .45);
        var drift = Math.Sin(phase * .42) * .008 + beat * .013;
        for (var i = 0; i < count; i++)
        {
            ref var flake = ref snowflakes[i];
            var sway = Math.Sin(phase * (0.6 + flake.Size * .45) + flake.Seed);
            flake.X = Wrap(flake.X + (drift + .017 * sway * flake.Size) * step);
            flake.Y = Wrap(flake.Y + (.025 + flake.Size * .12 + energy * .05) * step);
            var point = new Point(flake.X * width, flake.Y * height);
            var radius = (.58 + 1.72 * flake.Size * flake.Size) * scale;
            var alpha = (.17 + flake.Size * .57) * (.86 + .14 * sway);
            if (glow > .05 && flake.Size > .63)
                drawing.DrawEllipse(ink.Brush(flake.Tint, alpha * glow * .28), null, point, radius * 2.5, radius * 2.5);

            drawing.DrawEllipse(ink.Brush(flake.Tint, alpha), null, point, radius, radius);
            if (flake.Size > .94)
                DrawFlakeCrystal(drawing, point, radius * (1.65 + beat * .26), alpha * .58, flake.Tint);
        }
    }

    private void DrawFlakeCrystal(DrawingContext drawing, Point center, double radius, double alpha, double tint)
    {
        var pen = new Pen(ink.Brush(tint, alpha), .75);
        for (var spoke = 0; spoke < 3; spoke++)
        {
            var angle = spoke * Math.PI / 3 + phase * .2;
            var arm = new Vector(Math.Cos(angle), Math.Sin(angle)) * radius;
            drawing.DrawLine(pen, center - arm, center + arm);
        }
    }

    private void DrawTrail(DrawingContext drawing, double now)
    {
        RecordPointer(now);
        var thickness = EffectInk.Finite(Settings.Thickness, .05, 1, .66);
        var glow = EffectInk.Finite(Settings.Glow, 0, 1, .45);
        var visibleCount = Math.Min(trailCount, Math.Clamp(Settings.Count, 10, trail.Length));
        var previous = default(TrailPoint);
        for (var index = visibleCount - 1; index >= 0; index--)
        {
            var current = trail[(trailHead + trail.Length - 1 - index) % trail.Length];
            var age = now - current.Time;
            if (age > TrailLife)
                continue;

            var freshness = 1 - age / TrailLife;
            var alpha = Math.Pow(freshness, 1.45) * (.66 + energy * .34);
            var width = Math.Max(.45, freshness * (1.5 + thickness * 4 + beat * 1.8));
            var hue = Wrap(current.Time * .53 + index / 37d);
            if (!current.IsStart && previous.Time != 0)
            {
                if (glow > .01)
                    drawing.DrawLine(TrailPen(hue, alpha * glow * .38, width * 3), previous.Position, current.Position);

                drawing.DrawLine(TrailPen(hue, alpha, width), previous.Position, current.Position);
            }
            else if (index == 0)
            {
                drawing.DrawEllipse(ink.Brush(hue, alpha), null, current.Position, width * .6, width * .6);
            }

            previous = current;
        }
    }

    private void RecordPointer(double now)
    {
        if (!HasPointer())
        {
            pointerPresent = false;
            return;
        }

        var last = trail[(trailHead + trail.Length - 1) % trail.Length];
        var distance = (Pointer - last.Position).LengthSquared;
        if (pointerPresent && distance < 1.8)
            return;

        var start = !pointerPresent || now - last.Time > TrailLife * .9 || distance > 180 * 180;
        trail[trailHead] = new TrailPoint(Pointer, now, start);
        trailHead = (trailHead + 1) % trail.Length;
        trailCount = Math.Min(trailCount + 1, trail.Length);
        pointerPresent = true;
    }

    private Pen TrailPen(double tint, double alpha, double width)
    {
        return new Pen(ink.Brush(tint, alpha), width)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
        };
    }

    private void DrawFlock(DrawingContext drawing, double step)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var count = Math.Clamp(Settings.Count, 12, flock.Length);
        var attractor = new Point(.5 + .22 * Math.Sin(phase * .44), .49 + .2 * Math.Cos(phase * .57));
        var pointer = HasPointer() ? new Point(Pointer.X / width, Pointer.Y / height) : new Point(-2, -2);
        for (var i = 0; i < count; i++)
            nextFlock[i] = MoveBird(i, count, attractor, pointer, step);

        Array.Copy(nextFlock, flock, count);
        var scale = Math.Clamp(Math.Sqrt(width * height) / 430, .8, 2.7);
        for (var i = 0; i < count; i++)
            DrawBird(drawing, flock[i], i, count, scale);
    }

    private Bird MoveBird(int index, int count, Point attractor, Point pointer, double step)
    {
        var bird = flock[index];
        var alignment = new Vector();
        var cohesion = new Vector();
        var separation = new Vector();
        var neighbors = 0;
        for (var j = 0; j < count; j++)
        {
            var difference = flock[j].Position - bird.Position;
            var distanceSquared = difference.LengthSquared;
            if (distanceSquared is <= .00000001 or > .043)
                continue;

            alignment += flock[j].Direction;
            cohesion += difference;
            if (distanceSquared < .008)
                separation -= difference * (.008 - distanceSquared) / (distanceSquared + .005);

            neighbors++;
        }

        var toAttractor = attractor - bird.Position;
        var steering = toAttractor * (.42 + beat * .36) + separation * .66
            + new Vector(-toAttractor.Y, toAttractor.X) * .57;
        if (neighbors > 0)
            steering += (alignment / neighbors - bird.Direction) * 1.32 + cohesion / neighbors * .45;

        var away = bird.Position - pointer;
        if (away.LengthSquared < .027)
            steering += away * 6 * (1 - away.LengthSquared / .027);

        var direction = bird.Direction + steering * step;
        var speed = direction.Length;
        var maxSpeed = .24 + energy * .22;
        if (speed > maxSpeed)
            direction *= maxSpeed / speed;

        var position = bird.Position + direction * step;
        position.X = Math.Clamp(position.X, -.03, 1.03);
        position.Y = Math.Clamp(position.Y, -.03, 1.03);
        return new Bird(position, direction);
    }

    private void DrawBird(DrawingContext drawing, Bird bird, int index, int count, double scale)
    {
        var center = new Point(bird.Position.X * ActualWidth, bird.Position.Y * ActualHeight);
        var forward = new Vector(bird.Direction.X * ActualWidth, bird.Direction.Y * ActualHeight);
        if (forward.LengthSquared < .000001)
            forward = new Vector(1, 0);

        forward.Normalize();
        var side = new Vector(-forward.Y, forward.X);
        var size = scale * (2.3 + index % 9 * .23);
        var wings = (.7 + Math.Sin(phase * (7 + index % 4) + index) * .22) * size;
        var tail = center - forward * size * .85;
        var path = new StreamGeometry();
        using (var geometry = path.Open())
        {
            geometry.BeginFigure(center + forward * size, true, true);
            geometry.LineTo(tail + side * wings, false, false);
            geometry.LineTo(center - forward * size * .28, false, false);
            geometry.LineTo(tail - side * wings, false, false);
        }
        path.Freeze();

        var tint = (index + 1d) / count;
        var glow = EffectInk.Finite(Settings.Glow, 0, 1, .45);
        if (glow > .04)
            drawing.DrawEllipse(ink.Brush(tint, glow * (.12 + beat * .19)), null, center, size * 1.6, size * 1.6);

        drawing.DrawGeometry(ink.Brush(tint, .64 + energy * .3), null, path);
    }

    private void DrawWaves(DrawingContext drawing)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var thickness = EffectInk.Finite(Settings.Thickness, .05, 1, .66);
        var glow = EffectInk.Finite(Settings.Glow, 0, 1, .45);
        var layers = Math.Clamp(Settings.Count / 11, 3, 8);
        var useLeft = Frame?.Left;
        var useRight = Frame?.Right;
        for (var layer = 0; layer < layers; layer++)
        {
            var position = layer / (double)(layers - 1);
            var baseline = height * (.29 + .46 * position);
            var amplitude = height * (.063 + .079 * energy) * (1 - .32 * position);
            for (var i = 0; i < ribbon.Length; i++)
            {
                var x = i / (double)(ribbon.Length - 1);
                var sample = Sample(layer % 2 == 0 ? useLeft : useRight, x);
                var wiggle = Math.Sin(x * Tau * (1.18 + layer * .13) + phase * (.67 + position * .3) + layer * .52)
                    + .4 * Math.Sin(x * Tau * 3.1 - phase * .85 + layer * .82);
                var feather = .2 + .8 * Math.Sin(Math.PI * x);
                ribbon[i] = new Point(x * width, baseline + (wiggle * amplitude + sample * height * .135) * feather);
            }

            var ribbonWidth = height * (.008 + thickness * .04 + beat * .024) * (1 - .48 * position);
            var shape = WaveShape(ribbonWidth);
            drawing.PushOpacity(.47 + .31 * energy);
            drawing.DrawGeometry(ink.Ribbon(position), null, shape);
            drawing.Pop();
            if (glow > .02)
                drawing.DrawGeometry(null, new Pen(ink.Brush(position, glow * .35), .8 + 1.15 * beat), shape);
        }
    }

    private StreamGeometry WaveShape(double width)
    {
        var shape = new StreamGeometry();
        using (var geometry = shape.Open())
        {
            geometry.BeginFigure(ribbon[0], true, true);
            for (var i = 1; i < ribbon.Length; i++)
                geometry.LineTo(ribbon[i], true, false);

            for (var i = ribbon.Length - 1; i >= 0; i--)
            {
                var lower = ribbon[i];
                lower.Y += Math.Sin(i / (double)(ribbon.Length - 1) * Math.PI) * width;
                geometry.LineTo(lower, true, false);
            }
        }
        shape.Freeze();
        return shape;
    }

    private void DrawFluid(DrawingContext drawing)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var scale = Math.Min(width, height);
        var glow = EffectInk.Finite(Settings.Glow, 0, 1, .45);
        var count = Math.Clamp(Settings.Count / 8, 4, 11);
        var hasPointer = HasPointer();
        for (var i = 0; i < count; i++)
        {
            var angle = i / (double)count * Tau;
            var tint = i / (double)(count - 1);
            var band = Sample(Frame?.Spectrum, tint * .8);
            var horizontal = .5 + Math.Sin(phase * (.3 + .12 * tint) + angle) * (.27 + energy * .09);
            var vertical = .5 + Math.Cos(phase * (.41 + .08 * tint) + angle * 1.28) * (.26 + energy * .06);
            var center = new Point(horizontal * width, vertical * height);
            if (hasPointer)
            {
                var towardPointer = Pointer - center;
                var influence = .31 * Math.Exp(-towardPointer.Length / (scale * .37));
                center += towardPointer * influence;
            }

            var radius = scale * (.153 + (i % 4) * .018 + band * .18 + beat * .058);
            var stretch = 1.05 + .48 * (1 + Math.Sin(phase + angle)) / 2;
            drawing.PushOpacity(.5 + energy * .29 + glow * .19);
            drawing.DrawEllipse(ink.Radial(tint), null, center, radius * stretch, radius);
            drawing.Pop();
            var bead = center + new Vector(Math.Cos(phase * .88 + angle), Math.Sin(phase * .71 - angle)) * radius * .63;
            var beadRadius = 1 + radius * (.045 + band * .045);
            drawing.DrawEllipse(ink.Brush(tint, .27 + band * .44), null, bead, beadRadius, beadRadius);
        }
    }

    private bool HasPointer() => Pointer.X >= 0 && Pointer.X < ActualWidth && Pointer.Y >= 0 && Pointer.Y < ActualHeight;

    private static float Sample(float[]? samples, double position)
    {
        if (samples is not { Length: > 1 })
            return 0;

        var index = position * (samples.Length - 1);
        int low = (int)index;
        return samples[low] + (samples[Math.Min(samples.Length - 1, low + 1)] - samples[low]) * (float)(index - low);
    }

    private static double Wrap(double value) => value - Math.Floor(value);

    private struct Snowflake(double x, double y, double size, double seed, double tint)
    {
        public double X = x;
        public double Y = y;
        public readonly double Size = size;
        public readonly double Seed = seed;
        public readonly double Tint = tint;
    }

    private readonly record struct Bird(Point Position, Vector Direction);
    private readonly record struct TrailPoint(Point Position, double Time, bool IsStart);
}

public enum GlowEdge { Bottom, Top, Left, Right }

public sealed class GlowEffect : FrameworkElement
{
    private readonly EffectInk ink = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double energy;
    private double previousTime;
    private double drift;

    public GlowEffect()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public AudioFrame? Frame { get; set; }
    public VisualSettings Settings { get; set; } = new();
    public new int Style { get; set; }
    public double Strength { get; set; } = .58;
    public GlowEdge TaskbarEdge { get; set; } = GlowEdge.Bottom;

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 2 || ActualHeight < 2)
            return;

        ink.Update(Settings);
        var now = clock.Elapsed.TotalSeconds;
        var elapsed = Math.Clamp(now - previousTime, 0, .08);
        previousTime = now;
        var rawLevel = Math.Clamp(Frame?.Level ?? 0, 0, 1);
        var beat = Math.Clamp(Frame?.Beat ?? 0, 0, 1);
        var strength = EffectInk.Finite(Strength, 0, 1, .58);
        var opacity = EffectInk.Finite(Settings.Opacity, 0, 1, 1);
        energy += (rawLevel - energy) * (1 - Math.Exp(-elapsed / (.04 + EffectInk.Finite(Settings.Smoothing, 0, 1, .58) * .17)));
        drift += elapsed * EffectInk.Finite(Settings.Speed, .15, 4, 1);
        var bloom = (energy * .73 + beat * .27) * strength;
        if (bloom < .003)
            return;

        drawing.PushOpacity(opacity);
        switch (Style)
        {
            case 0: DrawWash(drawing, GlowEdge.Bottom, bloom, .22); break;
            case 1: DrawWash(drawing, GlowEdge.Top, bloom, .68); break;
            case 2:
                DrawWash(drawing, GlowEdge.Left, bloom, .11);
                DrawWash(drawing, GlowEdge.Right, bloom, .89);
                break;
            case 3: DrawOutline(drawing, bloom); break;
            case 4: DrawSpectrum(drawing, TaskbarEdge, bloom, 1.4); break;
            case 5: DrawCorners(drawing, bloom); break;
            case 6:
                DrawWash(drawing, GlowEdge.Top, bloom, .84);
                DrawWash(drawing, GlowEdge.Bottom, bloom, .17);
                break;
            case 7: DrawOutline(drawing, bloom * (.62 + .3 * Math.Sin(drift * 2.4))); break;
            case 8: DrawScanningLights(drawing, bloom); break;
            default:
                DrawSpectrum(drawing, GlowEdge.Bottom, bloom, .8);
                DrawSpectrum(drawing, GlowEdge.Top, bloom, .8);
                DrawSpectrum(drawing, GlowEdge.Left, bloom, .55);
                DrawSpectrum(drawing, GlowEdge.Right, bloom, .55);
                break;
        }
        drawing.Pop();
    }

    private void DrawOutline(DrawingContext drawing, double bloom)
    {
        DrawWash(drawing, GlowEdge.Bottom, bloom, .12);
        DrawWash(drawing, GlowEdge.Top, bloom, .84);
        DrawWash(drawing, GlowEdge.Left, bloom, .32);
        DrawWash(drawing, GlowEdge.Right, bloom, .63);
    }

    private void DrawWash(DrawingContext drawing, GlowEdge edge, double bloom, double tint)
    {
        var depth = Math.Min(ActualWidth, ActualHeight) * (.028 + bloom * .11);
        var (length, transform) = EdgeTransform(edge);
        drawing.PushTransform(transform);
        drawing.PushOpacity(Math.Clamp(bloom * 1.42, 0, 1));
        drawing.DrawRectangle(ink.Wash(tint), null, new Rect(0, 0, length, depth));
        drawing.Pop();
        drawing.Pop();
    }

    private void DrawCorners(DrawingContext drawing, double bloom)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) * (.12 + .25 * bloom);
        drawing.PushOpacity(Math.Clamp(bloom * 1.75, 0, 1));
        drawing.DrawEllipse(ink.Radial(.12), null, new Point(0, 0), radius, radius);
        drawing.DrawEllipse(ink.Radial(.5), null, new Point(ActualWidth, 0), radius, radius);
        drawing.DrawEllipse(ink.Radial(.72), null, new Point(0, ActualHeight), radius, radius);
        drawing.DrawEllipse(ink.Radial(.95), null, new Point(ActualWidth, ActualHeight), radius, radius);
        drawing.Pop();
    }

    private void DrawScanningLights(DrawingContext drawing, double bloom)
    {
        var fraction = .5 + .48 * Math.Sin(drift * .79);
        var secondary = .5 + .48 * Math.Cos(drift * .88);
        var radius = Math.Min(ActualWidth, ActualHeight) * (.09 + .17 * bloom);
        drawing.PushOpacity(Math.Clamp(bloom * 1.68, 0, 1));
        drawing.DrawEllipse(ink.Radial(fraction), null, new Point(ActualWidth * fraction, 0), radius * 1.7, radius);
        drawing.DrawEllipse(ink.Radial(secondary), null, new Point(ActualWidth * secondary, ActualHeight), radius * 1.7, radius);
        drawing.DrawEllipse(ink.Radial(fraction * .7), null, new Point(0, ActualHeight * fraction), radius, radius * 1.7);
        drawing.DrawEllipse(ink.Radial(secondary * .8), null, new Point(ActualWidth, ActualHeight * secondary), radius, radius * 1.7);
        drawing.Pop();
    }

    private void DrawSpectrum(DrawingContext drawing, GlowEdge edge, double bloom, double widthScale)
    {
        var spectrum = Frame?.Spectrum;
        if (spectrum is not { Length: > 2 })
            return;

        var (length, transform) = EdgeTransform(edge);
        var count = Math.Clamp(Settings.Count, 12, 100);
        var slot = length / count;
        var maximum = Math.Min(ActualWidth, ActualHeight) * (.05 + .14 * bloom) * widthScale;
        var thickness = EffectInk.Finite(Settings.Thickness, .05, 1, .66);
        drawing.PushTransform(transform);
        for (var i = 0; i < count; i++)
        {
            var tint = i / (double)(count - 1);
            var volume = spectrum[(int)(tint * (spectrum.Length - 1))];
            var depth = volume * maximum;
            if (depth < .5)
                continue;

            var barWidth = Math.Max(1, slot * (.26 + thickness * .56));
            drawing.PushOpacity(Math.Clamp(.28 + bloom * 1.3, 0, 1));
            drawing.DrawRectangle(ink.Wash(tint), null, new Rect(i * slot + (slot - barWidth) * .5, 0, barWidth, depth));
            drawing.Pop();
        }
        drawing.Pop();
    }

    private (double Length, Transform Transform) EdgeTransform(GlowEdge edge)
    {
        return edge switch
        {
            GlowEdge.Left => (ActualHeight, new MatrixTransform(0, 1, 1, 0, 0, 0)),
            GlowEdge.Right => (ActualHeight, new MatrixTransform(0, 1, -1, 0, ActualWidth, 0)),
            GlowEdge.Top => (ActualWidth, Transform.Identity),
            _ => (ActualWidth, new MatrixTransform(1, 0, 0, -1, 0, ActualHeight))
        };
    }
}

internal sealed class EffectInk
{
    private const int Tints = 24;
    private const int Alphas = 24;
    private readonly SolidColorBrush[,] solids = new SolidColorBrush[Tints, Alphas];
    private readonly RadialGradientBrush[] radials = new RadialGradientBrush[Tints];
    private readonly LinearGradientBrush[] ribbons = new LinearGradientBrush[Tints];
    private readonly LinearGradientBrush[] washes = new LinearGradientBrush[Tints];
    private (VisualPalette Palette, string? First, string? Last)? previous;

    public void Update(VisualSettings settings)
    {
        var key = (settings.Palette, settings.CustomColor, settings.CustomAccent);
        if (previous == key)
            return;

        previous = key;
        var (first, middle, last) = Visualizer.ResolvePalette(settings);
        for (var index = 0; index < Tints; index++)
        {
            var position = index / (double)(Tints - 1);
            var color = position < .5 ? Blend(first, middle, position * 2) : Blend(middle, last, position * 2 - 1);
            var highlight = Blend(color, Colors.White, .43);
            for (var alpha = 0; alpha < Alphas; alpha++)
                solids[index, alpha] = Freeze(new SolidColorBrush(Alpha(color, alpha / (double)(Alphas - 1))));

            radials[index] = Freeze(new RadialGradientBrush(new GradientStopCollection
            {
                new(Alpha(highlight, .75), 0), new(Alpha(color, .47), .29),
                new(Alpha(color, .21), .64), new(Alpha(color, 0), 1)
            }));
            ribbons[index] = Freeze(new LinearGradientBrush(new GradientStopCollection
            {
                new(Alpha(color, 0), 0), new(Alpha(color, .75), .14),
                new(Alpha(highlight, .85), .53), new(Alpha(color, .7), .81),
                new(Alpha(color, 0), 1)
            }, new Point(0, 0), new Point(1, 0)));
            washes[index] = Freeze(new LinearGradientBrush(new GradientStopCollection
            {
                new(Alpha(highlight, .94), 0), new(Alpha(color, .48), .23),
                new(Alpha(color, .16), .57), new(Alpha(color, 0), 1)
            }, new Point(0, 0), new Point(0, 1)));
        }
    }

    public SolidColorBrush Brush(double tint, double alpha) => solids[Step(tint, Tints), Step(alpha, Alphas)];
    public Brush Radial(double tint) => radials[Step(tint, Tints)];
    public Brush Ribbon(double tint) => ribbons[Step(tint, Tints)];
    public Brush Wash(double tint) => washes[Step(tint, Tints)];

    internal static double Finite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static int Step(double value, int count) => (int)Math.Round(Math.Clamp(value, 0, 1) * (count - 1));
    private static Color Alpha(Color color, double opacity) => Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B);
    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
