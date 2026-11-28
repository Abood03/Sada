using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Sada;

public enum VisualMode
{
    Bars, Mirror, Blocks, Ring, Halo, Waveform, Aurora, Orb, Vinyl, Waterfall, Scope, Particles, Terrain, Meters
}

public enum VisualDirection { Up, Down, Left, Right, Center }
public enum VisualPalette { Aurora, Ember, Orchid, Frost, Citrus, Monochrome, Rainbow, Custom }
public enum VisualAtmosphere { None, Mist, Dust, Ripples }

public sealed class VisualSettings
{
    public VisualMode Mode { get; set; } = VisualMode.Bars;
    public VisualDirection Direction { get; set; } = VisualDirection.Up;
    public VisualPalette Palette { get; set; } = VisualPalette.Aurora;
    public string CustomColor { get; set; } = "#33DFC7";
    public string CustomAccent { get; set; } = "#A48CFF";
    public int Count { get; set; } = 48;
    public double Thickness { get; set; } = .66;
    public double Sensitivity { get; set; } = 1;
    public double Smoothing { get; set; } = .58;
    public double Opacity { get; set; } = 1;
    public double Speed { get; set; } = 1;
    public double Glow { get; set; } = .45;
    public bool Mirror { get; set; }
    public VisualAtmosphere Atmosphere { get; set; }
    public double AtmosphereIntensity { get; set; } = .6;
}

public sealed class Visualizer : FrameworkElement
{
    private const int BandCount = 80;
    private const int HistoryLength = 44;
    private const int ColorSteps = 32;
    private const int AlphaSteps = 16;
    private const int MaximumCount = 120;
    private const int HaloRippleSegments = 100;
    private const int HaloPenCacheCapacity = 4096;
    private const double HaloPenThicknessSteps = 16;
    private const int MaximumFadingLayers = 2;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(220);
    private const double Tau = Math.PI * 2;
    private static readonly Vector[] HaloRippleDirections = GetHaloRippleDirections();
    private readonly record struct Appearance(VisualMode Mode, VisualDirection Direction, VisualPalette Palette,
        VisualAtmosphere Atmosphere, string? CustomColor, string? CustomAccent, AlbumPalette? Artwork);
    private readonly float[] bands = new float[BandCount];
    private readonly float[][] history = new float[HistoryLength][];
    private readonly SolidColorBrush[,] colors = new SolidColorBrush[ColorSteps, AlphaSteps];
    private readonly Color[] gradient = new Color[ColorSteps];
    private readonly Dictionary<(int Color, int Alpha, int Thickness), Pen> haloPens = new();
    private readonly RadialGradientBrush?[,] mistBrushes = new RadialGradientBrush?[4, 256];
    private readonly Vector[] haloSpokeDirections = new Vector[MaximumCount];
    private readonly int[] waterfallPixels = new int[BandCount * HistoryLength];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<DrawingGroup> fadingLayers = new(MaximumFadingLayers);
    private DrawingGroup liveLayer = new();
    private Appearance? renderedAppearance;
    private Size renderedSize;
    private TimeSpan fadeUntil;
    private WriteableBitmap? waterfall;
    private VisualPalette? previousPalette;
    private string? previousCustomColor;
    private string? previousCustomAccent;
    private AudioFrame? previousFrame;
    private int historyHead = HistoryLength - 1;
    private double previousTime;
    private double phase;
    private double energy;
    private double beat;
    private double leftLevel;
    private double rightLevel;
    private double barWidth;
    private double glow;
    private int count;
    private int haloSpokeCount;

    public Visualizer()
    {
        for (var row = 0; row < HistoryLength; row++) history[row] = new float[BandCount];
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public VisualSettings Settings { get; set; } = new();
    public AudioFrame? Frame { get; set; }
    internal AlbumPalette? ArtworkColors { get; set; }
    private AlbumPalette? previousArtworkColors;
    private ImageBrush? ringArtworkBrush;

    internal ImageSource? RingArtwork
    {
        set
        {
            if (ReferenceEquals(ringArtworkBrush?.ImageSource, value)) return;
            ringArtworkBrush = value is null ? null : new ImageBrush(value) { Stretch = Stretch.UniformToFill };
            if (ringArtworkBrush?.CanFreeze == true) ringArtworkBrush.Freeze();
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 2 || ActualHeight < 2) return;

        var appearance = new Appearance(Settings.Mode, Settings.Direction, Settings.Palette, Settings.Atmosphere,
            Settings.Palette == VisualPalette.Custom ? Settings.CustomColor : null,
            Settings.Palette == VisualPalette.Custom ? Settings.CustomAccent : null, ArtworkColors);
        var size = RenderSize;
        var now = clock.Elapsed;
        bool animate = SystemParameters.ClientAreaAnimation;
        if (fadingLayers.Count != 0 && (!animate || now >= fadeUntil || renderedSize != size))
            FinishTransition();
        if (animate && renderedAppearance is { } previous && previous != appearance && renderedSize == size)
            BeginTransition(now);
        renderedAppearance = appearance;
        renderedSize = size;

        using (var frameDrawing = liveLayer.Open())
            DrawFrame(frameDrawing);
        foreach (DrawingGroup layer in fadingLayers)
            drawing.DrawDrawing(layer);
        drawing.DrawDrawing(liveLayer);
    }

    private void BeginTransition(TimeSpan now)
    {
        double liveOpacity = liveLayer.Opacity;
        if (liveOpacity >= .02)
        {
            if (fadingLayers.Count == MaximumFadingLayers)
            {
                int faintest = fadingLayers[0].Opacity <= fadingLayers[1].Opacity ? 0 : 1;
                fadingLayers.RemoveAt(faintest);
            }
            fadingLayers.Add(liveLayer);
        }

        foreach (DrawingGroup layer in fadingLayers)
            AnimateOpacity(layer, layer.Opacity, 0);
        liveLayer = new DrawingGroup();
        AnimateOpacity(liveLayer, 0, 1);
        fadeUntil = now + TransitionDuration;
        // A retained waterfall drawing still references its original bitmap.
        // Recreate only that mode's buffer so its outgoing image stays intact.
        waterfall = null;
    }

    private void FinishTransition()
    {
        fadingLayers.Clear();
        liveLayer.BeginAnimation(DrawingGroup.OpacityProperty, null);
        liveLayer.Opacity = 1;
    }

    private static void AnimateOpacity(DrawingGroup layer, double from, double to)
    {
        layer.BeginAnimation(DrawingGroup.OpacityProperty, null);
        layer.Opacity = to;
        layer.BeginAnimation(DrawingGroup.OpacityProperty, new DoubleAnimation(from, to, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private void DrawFrame(DrawingContext drawing)
    {
        var paletteChanged = UpdatePalette();
        var historyChanged = UpdateMotion();
        var opacity = Limit(Settings.Opacity, 0, 1, 1);
        count = Math.Clamp(Settings.Count, 8, MaximumCount);
        barWidth = Limit(Settings.Thickness, .05, 1, .66);
        glow = Limit(Settings.Glow, 0, 1, .45);

        drawing.PushOpacity(opacity);
        DrawAtmosphere(drawing, ActualWidth, ActualHeight);
        var (width, height, transform) = GetOrientation();
        if (transform is not null) drawing.PushTransform(transform);
        switch (Settings.Mode)
        {
            case VisualMode.Mirror: DrawBars(drawing, width, height, true, false); break;
            case VisualMode.Blocks: DrawBars(drawing, width, height, Settings.Direction == VisualDirection.Center, true); break;
            case VisualMode.Ring: DrawRing(drawing, width, height); break;
            case VisualMode.Halo: DrawHalo(drawing, width, height); break;
            case VisualMode.Waveform: DrawWaveform(drawing, width, height); break;
            case VisualMode.Aurora: DrawAurora(drawing, width, height); break;
            case VisualMode.Orb: DrawOrb(drawing, width, height); break;
            case VisualMode.Vinyl: DrawVinyl(drawing, width, height); break;
            case VisualMode.Waterfall: DrawWaterfall(drawing, width, height, paletteChanged || historyChanged); break;
            case VisualMode.Scope: DrawScope(drawing, width, height); break;
            case VisualMode.Particles: DrawParticles(drawing, width, height, 1, true); break;
            case VisualMode.Terrain: DrawTerrain(drawing, width, height); break;
            case VisualMode.Meters: DrawMeters(drawing, width, height); break;
            default: DrawBars(drawing, width, height, Settings.Direction == VisualDirection.Center, false); break;
        }
        if (transform is not null) drawing.Pop();
        if (ringArtworkBrush is not null && Settings.Mode is VisualMode.Ring or VisualMode.Halo)
        {
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var radius = Math.Min(ActualWidth, ActualHeight) * .205;
            var edge = Settings.Mode == VisualMode.Halo ? HaloPenFor(.54, .56, 1.2) : PenFor(.54, .56, 1.2);
            drawing.DrawEllipse(ringArtworkBrush, edge, center, radius, radius);
        }
        drawing.Pop();
    }

    private (double Width, double Height, Transform? Transform) GetOrientation()
    {
        return Settings.Direction switch
        {
            VisualDirection.Down => (ActualWidth, ActualHeight, new MatrixTransform(1, 0, 0, -1, 0, ActualHeight)),
            VisualDirection.Left => (ActualHeight, ActualWidth, new MatrixTransform(0, -1, 1, 0, 0, ActualHeight)),
            VisualDirection.Right => (ActualHeight, ActualWidth, new MatrixTransform(0, 1, -1, 0, ActualWidth, 0)),
            _ => (ActualWidth, ActualHeight, null)
        };
    }

    private bool UpdateMotion()
    {
        var frame = Frame;
        var time = clock.Elapsed.TotalSeconds;
        var delta = Math.Clamp(time - previousTime, 0, .1);
        previousTime = time;
        phase += delta * Limit(Settings.Speed, .15, 4, 1);
        var gain = Limit(Settings.Sensitivity, .1, 4, 1);
        var smoothing = Limit(Settings.Smoothing, 0, 1, .58);
        var attack = 1 - Math.Exp(-delta / (.014 + smoothing * .068));
        var release = 1 - Math.Exp(-delta / (.035 + smoothing * .27));
        for (var i = 0; i < bands.Length; i++)
        {
            var level = Math.Clamp(Sample(frame?.Spectrum, i / (double)(BandCount - 1)) * gain, 0, 1);
            bands[i] = (float)Follow(bands[i], level, attack, release);
        }
        energy = Follow(energy, Math.Clamp((frame?.Level ?? 0) * gain, 0, 1), attack, release);
        beat = Math.Clamp((frame?.Beat ?? 0) * gain, 0, 1);

        if (ReferenceEquals(previousFrame, frame)) return false;
        previousFrame = frame;
        historyHead = (historyHead + 1) % HistoryLength;
        Array.Copy(bands, history[historyHead], bands.Length);
        leftLevel = Follow(leftLevel, ChannelLevel(frame?.Left, gain), attack, release);
        rightLevel = Follow(rightLevel, ChannelLevel(frame?.Right, gain), attack, release);
        return true;
    }

    private bool UpdatePalette()
    {
        if (previousPalette == Settings.Palette && previousCustomColor == Settings.CustomColor &&
            previousCustomAccent == Settings.CustomAccent && previousArtworkColors == ArtworkColors) return false;

        previousPalette = Settings.Palette;
        previousCustomColor = Settings.CustomColor;
        previousCustomAccent = Settings.CustomAccent;
        previousArtworkColors = ArtworkColors;
        haloPens.Clear();
        Array.Clear(mistBrushes);
        var stops = ArtworkColors is { } art ? (art.First, art.Middle, art.Last) : ResolvePalette(Settings);

        for (var i = 0; i < ColorSteps; i++)
        {
            var fraction = i / (double)(ColorSteps - 1);
            var color = fraction < .5
                ? LerpColor(stops.First, stops.Middle, fraction * 2)
                : LerpColor(stops.Middle, stops.Last, (fraction - .5) * 2);
            gradient[i] = color;
            for (var alpha = 0; alpha < AlphaSteps; alpha++)
            {
                var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255 / (AlphaSteps - 1)), color.R, color.G, color.B));
                brush.Freeze();
                colors[i, alpha] = brush;
            }
        }
        return true;
    }

    internal static (Color First, Color Middle, Color Last) ResolvePalette(VisualSettings settings) => settings.Palette switch
    {
        VisualPalette.Ember => (Rgb(241, 62, 78), Rgb(255, 137, 86), Rgb(255, 223, 119)),
        VisualPalette.Orchid => (Rgb(103, 93, 254), Rgb(190, 102, 249), Rgb(255, 157, 192)),
        VisualPalette.Frost => (Rgb(69, 131, 254), Rgb(105, 216, 252), Rgb(222, 250, 255)),
        VisualPalette.Citrus => (Rgb(67, 210, 123), Rgb(178, 227, 84), Rgb(255, 237, 135)),
        VisualPalette.Monochrome => (Rgb(129, 142, 158), Rgb(210, 218, 225), Colors.White),
        VisualPalette.Rainbow => (Rgb(253, 97, 147), Rgb(92, 220, 245), Rgb(168, 105, 255)),
        VisualPalette.Custom => CustomStops(settings.CustomColor, settings.CustomAccent),
        _ => (Rgb(39, 218, 179), Rgb(68, 169, 246), Rgb(177, 138, 255))
    };

    private void DrawBars(DrawingContext drawing, double width, double height, bool center, bool blocks)
    {
        var step = (width * .96) / count;
        var widthOfBar = Math.Min(step * barWidth, Math.Max(.6, step - .25));
        var baseline = height * (center ? .5 : .97);
        var range = height * (center ? .45 : .91);
        var xOrigin = width * .02 + (step - widthOfBar) / 2;
        var segmentCount = Math.Clamp((int)(range / 6), 4, 22);
        var segmentHeight = range / segmentCount;
        var radius = Math.Min(widthOfBar / 2, 4);

        for (var i = 0; i < count; i++)
        {
            var fraction = FrequencyPosition(i / (double)(count - 1));
            var level = Spectrum(fraction);
            var x = xOrigin + i * step;
            var amplitude = Math.Max(1.2, range * level);

            if (blocks)
            {
                var lit = Math.Clamp((int)Math.Ceiling(level * segmentCount), 1, segmentCount);
                var heightOfBlock = segmentHeight * .66;
                for (var j = 0; j < lit; j++)
                {
                    var y = baseline - j * segmentHeight - heightOfBlock;
                    var shade = Brush(fraction * .72 + .28 * j / segmentCount, .26 + .7 * level);
                    drawing.DrawRoundedRectangle(shade, null, new Rect(x, y, widthOfBar, heightOfBlock), 1, 1);
                    if (center) drawing.DrawRoundedRectangle(shade, null, new Rect(x, 2 * baseline - y - heightOfBlock, widthOfBar, heightOfBlock), 1, 1);
                }
                continue;
            }

            var fill = Brush(fraction, .34 + .65 * Math.Sqrt(level));
            if (glow > .12 && level > .1)
            {
                var spread = glow * Math.Min(widthOfBar * .46, 8);
                var back = new Rect(x - spread, baseline - amplitude - spread,
                    widthOfBar + 2 * spread, amplitude * (center ? 2 : 1) + 2 * spread);
                drawing.DrawRoundedRectangle(Brush(fraction, glow * level * .22), null, back, radius + spread, radius + spread);
            }
            var bar = new Rect(x, baseline - amplitude, widthOfBar, amplitude * (center ? 2 : 1));
            drawing.DrawRoundedRectangle(fill, null, bar, radius, radius);
        }
    }

    private void DrawRing(DrawingContext drawing, double width, double height)
    {
        var center = new Point(width / 2, height / 2);
        var scale = Math.Min(width, height);
        var radius = scale * (.28 + beat * .028);
        var resolution = Math.Clamp(count * 2, 48, 220);
        var thickness = StrokeSize(width, height);
        var outer = Path(resolution, i =>
        {
            var t = i / (double)(resolution - 1);
            var angle = t * Tau - Math.PI / 2 + phase * .17;
            var band = Settings.Mirror ? Math.Abs(2 * t - 1) : .5 - Math.Cos(t * Tau) * .5;
            var amplitude = Spectrum(band) * scale * .145;
            return Polar(center, angle, radius + amplitude);
        }, true);

        drawing.DrawEllipse(null, PenFor(.60, .23, 1), center, radius * .82, radius * .82);
        drawing.DrawGeometry(null, PenFor(.60, .12 + glow * .38, thickness * (1.8 + glow * 2.3)), outer);
        drawing.DrawGeometry(Brush(.28, .08 + energy * .12), PenFor(.22, .76 + energy * .24, thickness), outer);
        var inner = new Point(center.X, center.Y);
        var core = scale * (.024 + energy * .035);
        drawing.DrawEllipse(Brush(.76, .22 + beat * .64), null, inner, core, core);
    }

    private void DrawHalo(DrawingContext drawing, double width, double height)
    {
        var origin = new Point(width / 2, height / 2);
        var scale = Math.Min(width, height);
        var radius = scale * (.267 + beat * .016);
        var ringThickness = Math.Max(.8, scale * (.002 + barWidth * .006));
        drawing.DrawEllipse(null, HaloPenFor(.27, .27, ringThickness), origin, radius * .93, radius * .93);
        drawing.DrawEllipse(null, HaloPenFor(.78, .17, 1), origin, scale * .446, scale * .446);

        if (haloSpokeCount != count)
        {
            for (var i = 0; i < count; i++)
                haloSpokeDirections[i] = UnitDirection(i / (double)count * Tau - Math.PI / 2);
            haloSpokeCount = count;
        }

        var (sin, cos) = Math.SinCos(phase * .07);
        var spokeThickness = Math.Max(.55, Tau * radius / count * barWidth * .7);
        for (var i = 0; i < count; i++)
        {
            var around = i / (double)count;
            var band = FrequencyPosition(around);
            var level = Spectrum(band);
            var spoke = haloSpokeDirections[i];
            var direction = new Vector(spoke.X * cos - spoke.Y * sin, spoke.X * sin + spoke.Y * cos);
            var start = origin + direction * radius;
            var end = origin + direction * (radius + scale * (.016 + level * .19));
            drawing.DrawLine(HaloPenFor(band, .24 + .74 * level, spokeThickness), start, end);
        }

        var ripple = new StreamGeometry();
        var channel = Frame?.Left;
        var gain = Limit(Settings.Sensitivity, .1, 4, 1);
        using (var stream = ripple.Open())
        {
            for (var i = 0; i < HaloRippleSegments; i++)
            {
                var signal = Math.Clamp(Sample(channel, i / (double)(HaloRippleSegments - 1)) * gain, -1, 1);
                var point = origin + HaloRippleDirections[i] * (radius * .82 + signal * scale * .066);
                if (i == 0) stream.BeginFigure(point, true, true);
                else stream.LineTo(point, true, false);
            }
        }
        ripple.Freeze();
        drawing.DrawGeometry(null, HaloPenFor(.48, .44 + energy * .48, ringThickness * .82), ripple);
    }

    private static Vector[] GetHaloRippleDirections()
    {
        var directions = new Vector[HaloRippleSegments];
        for (var i = 0; i < directions.Length; i++)
            directions[i] = UnitDirection(i * Tau / (directions.Length - 1));
        return directions;
    }

    private static Vector UnitDirection(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return new Vector(cos, sin);
    }

    private void DrawWaveform(DrawingContext drawing, double width, double height)
    {
        var origin = height * .5;
        var resolution = Math.Clamp(count * 4, 64, 260);
        var thickness = StrokeSize(width, height);
        var xPadding = width * .02;
        DrawStroke(drawing, new Point(xPadding, origin), new Point(width - xPadding, origin), .14, .15, 1);
        var wave = Path(resolution, i =>
        {
            var t = i / (double)(resolution - 1);
            var x = xPadding + width * .96 * t;
            var sample = Wave(FrequencyPosition(t), false);
            return new Point(x, origin - sample * height * .44);
        });
        drawing.DrawGeometry(null, PenFor(.72, .20 + glow * .30, thickness * (2 + glow * 3)), wave);
        drawing.DrawGeometry(null, PenFor(.30, .76 + energy * .24, thickness), wave);
    }

    private void DrawAurora(DrawingContext drawing, double width, double height)
    {
        var resolution = Math.Clamp(count * 2, 40, 160);
        for (var ribbon = 0; ribbon < 4; ribbon++)
        {
            var origin = height * (.40 + ribbon * .065);
            var geometry = Path(resolution * 2, i =>
            {
                var upper = i < resolution;
                var t = (upper ? i : 2 * resolution - i - 1) / (double)(resolution - 1);
                var envelope = Math.Sin(t * Math.PI);
                var mod = Math.Sin(t * Tau * (1 + ribbon * .15) - phase * (.9 + ribbon * .24) + ribbon * .8);
                var samples = Wave(FrequencyPosition(t), ribbon % 2 == 1);
                var wave = mod * (.10 + energy * .35) + samples * .32;
                var band = Spectrum(t * .8);
                var thickness = (.014 + .045 * barWidth + band * .11) * (upper ? -1 : 1);
                return new Point(width * (.015 + .97 * t), origin + (wave * .60 + thickness) * height * envelope);
            }, true);
            var shade = ribbon / 3.0;
            drawing.DrawGeometry(Brush(shade, .12 + energy * .28), PenFor(shade, .27 + energy * .52, 1 + glow), geometry);
        }
    }

    private void DrawOrb(DrawingContext drawing, double width, double height)
    {
        var center = new Point(width / 2, height / 2);
        var radius = Math.Min(width, height) * (.31 + energy * .04 + beat * .025);
        drawing.DrawEllipse(Brush(.42, .08 + glow * .15), PenFor(.70, .4, 1.4), center, radius, radius);
        var lines = Math.Clamp(count / 5, 5, 22);
        var tilt = phase * .31;
        for (var line = 0; line < lines; line++)
        {
            var latitude = ((line + 1) / (double)(lines + 1) - .5) * Math.PI;
            var thickness = barWidth + .6;
            var mesh = Path(65, i =>
            {
                var longitude = i * Tau / 64;
                var roughness = Wave(i / 64.0, false) * (.035 + Spectrum(line / (double)lines) * .15);
                var latRadius = radius * Math.Cos(latitude) * (1 + roughness);
                var x = Math.Cos(longitude) * latRadius;
                var y = Math.Sin(latitude) * radius;
                var z = Math.Sin(longitude) * latRadius;
                return new Point(center.X + x, center.Y + y * Math.Cos(tilt) - z * Math.Sin(tilt));
            }, true);
            drawing.DrawGeometry(null, PenFor(line / (double)(lines - 1), .29 + energy * .48, thickness), mesh);
        }
        var crest = new Point(center.X - radius * .37, center.Y - radius * .52);
        drawing.DrawEllipse(Brush(.85, .22 + beat * .3), null, crest, radius * .055, radius * .055);
    }

    private void DrawVinyl(DrawingContext drawing, double width, double height)
    {
        var center = new Point(width / 2, height / 2);
        var radius = Math.Min(width, height) * .415;
        var grooves = Math.Clamp(count / 3, 8, 28);
        var thickness = Math.Max(.8, StrokeSize(width, height) * .6);
        for (var groove = 0; groove < grooves; groove++)
        {
            var fraction = groove / (double)(grooves - 1);
            var level = Spectrum(fraction);
            var grooveRadius = radius * (.35 + fraction * .65);
            var ring = Path(76, i =>
            {
                var angle = i * Tau / 75;
                var shine = Math.Sin(angle * 3 + phase * 1.7 + fraction * 5);
                return Polar(center, angle, grooveRadius + shine * level * radius * .105);
            }, true);
            drawing.DrawGeometry(null, PenFor(fraction, .17 + level * .62, thickness), ring);
        }
        drawing.DrawEllipse(Brush(.8, .18 + beat * .48), PenFor(.59, .68, thickness), center, radius * .29, radius * .29);
        drawing.DrawEllipse(Brush(.2, .70), null, center, radius * .028, radius * .028);
        var stylus = Polar(center, phase * 1.5, radius * .22);
        drawing.DrawEllipse(Brush(1, .9), null, stylus, radius * .03, radius * .03);
    }

    private void DrawWaterfall(DrawingContext drawing, double width, double height, bool changed)
    {
        if (waterfall is null || changed)
        {
            waterfall ??= new WriteableBitmap(BandCount, HistoryLength, 96, 96, PixelFormats.Pbgra32, null);
            for (var y = 0; y < HistoryLength; y++)
            {
                var row = History(y);
                var age = 1 - y / (double)HistoryLength;
                for (var x = 0; x < BandCount; x++)
                {
                    var position = FrequencyPosition(x / (double)(BandCount - 1));
                    var level = Sample(row, position);
                    var color = gradient[ColorIndex(Math.Clamp(level * .78 + position * .22, 0, 1))];
                    var alpha = (int)(Math.Clamp(level * 1.55, 0, 1) * (.29 + .71 * age) * 255);
                    var red = color.R * alpha / 255;
                    var green = color.G * alpha / 255;
                    var blue = color.B * alpha / 255;
                    waterfallPixels[(HistoryLength - 1 - y) * BandCount + x] = (alpha << 24) | (red << 16) | (green << 8) | blue;
                }
            }
            waterfall.WritePixels(new Int32Rect(0, 0, BandCount, HistoryLength), waterfallPixels, BandCount * 4, 0);
        }
        var bounds = new Rect(width * .025, height * .04, width * .95, height * .91);
        drawing.DrawImage(waterfall, bounds);
        DrawStroke(drawing, bounds.BottomLeft, bounds.BottomRight, .39, .47, StrokeSize(width, height));
    }

    private void DrawScope(DrawingContext drawing, double width, double height)
    {
        var origin = new Point(width * .5, height * .5);
        var clip = Math.Min(width, height) * .44;
        var resolution = Math.Clamp(count * 5, 85, 450);
        drawing.DrawEllipse(null, PenFor(.6, .20, 1), origin, clip, clip);
        drawing.DrawEllipse(null, PenFor(.6, .15, 1), origin, clip * .48, clip * .48);
        DrawStroke(drawing, new Point(origin.X - clip, origin.Y), new Point(origin.X + clip, origin.Y), .26, .20, 1);
        DrawStroke(drawing, new Point(origin.X, origin.Y - clip), new Point(origin.X, origin.Y + clip), .26, .20, 1);
        if (energy < .001) return;
        var trace = Path(resolution, i =>
        {
            var t = i / (double)(resolution - 1);
            var left = Wave(t, false);
            var right = Wave(t, true);
            return new Point(origin.X + (left - right) * clip * .7, origin.Y - (left + right) * clip * .7);
        });
        var thickness = StrokeSize(width, height);
        drawing.DrawGeometry(null, PenFor(.49, .14 + glow * .36, thickness * (2 + glow * 3)), trace);
        drawing.DrawGeometry(null, PenFor(.15, .82, thickness), trace);
    }

    private void DrawParticles(DrawingContext drawing, double width, double height, double intensity, bool clustered)
    {
        var amount = Math.Clamp(count * (clustered ? 3 : 2), 16, 240);
        var scale = Math.Min(width, height);
        var session = phase * .10;
        for (var i = 1; i <= amount; i++)
        {
            var fraction = Unit(i, 37);
            var rate = .19 + Unit(i, 53) * .81;
            var band = Spectrum(fraction);
            var angle = Unit(i, 23) * Tau + session * rate * (i % 2 == 0 ? 1 : -1);
            var distance = .055 + (Unit(i, 71) + beat * .11) * .36 + band * .055;
            var position = clustered
                ? new Point(width * (.5 + Math.Cos(angle) * distance), height * (.5 + Math.Sin(angle) * distance))
                : new Point(width * Wrap(Unit(i, 91) + Math.Sin(angle) * .08), height * Wrap(Unit(i, 43) - session * rate));
            var size = Math.Max(.65, scale * (.0016 + Unit(i, 59) * .004 * barWidth + band * .012));
            if (glow > .16 && band > .11)
                drawing.DrawEllipse(Brush(fraction, .22 * glow * intensity), null, position, size * 2.7, size * 2.7);
            drawing.DrawEllipse(Brush(fraction, Math.Clamp((.19 + band * .72 + beat * .15) * intensity, 0, 1)), null, position, size, size);
        }
    }

    private void DrawTerrain(DrawingContext drawing, double width, double height)
    {
        var layers = Math.Clamp(count / 5, 5, 15);
        var resolution = Math.Clamp(count * 2, 32, 148);
        for (var layer = layers - 1; layer >= 0; layer--)
        {
            var depth = layer / (double)(layers - 1);
            var row = History(Math.Min(HistoryLength - 1, layer * 3));
            var ridgeWidth = width * (.93 - depth * .34);
            var ridgeBottom = height * (.83 - depth * .51);
            var ridge = Path(resolution + 2, i =>
            {
                var t = Math.Clamp(i / (double)(resolution - 1), 0, 1);
                if (i >= resolution) t = i == resolution ? 1 : 0;
                var amplitude = i < resolution
                    ? Math.Sin(t * Math.PI) * (.008 + Sample(row, FrequencyPosition(t)) * .37)
                    : 0;
                return new Point((width - ridgeWidth) / 2 + ridgeWidth * t, ridgeBottom - amplitude * height);
            }, true);
            drawing.DrawGeometry(Brush(depth, .14 - depth * .10), PenFor(depth, .66 - depth * .37, 1 + barWidth * 1.2), ridge);
        }
    }

    private void DrawMeters(DrawingContext drawing, double width, double height)
    {
        var segments = Math.Clamp(count, 8, 65);
        var gap = Math.Min(width * .045, 20);
        var step = (width - 2 * gap) / segments;
        var body = Math.Min(height * .22, (4 + barWidth * 15) * Math.Max(1, height / 155));
        var blockWidth = step * (.52 + .42 * barWidth);
        var radius = Math.Min(2.8, blockWidth / 2);
        for (var channel = 0; channel < 2; channel++)
        {
            var level = channel == 0 ? leftLevel : rightLevel;
            var y = height * (channel == 0 ? .37 : .63) - body / 2;
            for (var i = 0; i < segments; i++)
            {
                var fraction = i / (double)(segments - 1);
                var fill = fraction < level ? .68 + .32 * level : .12;
                var x = gap + i * step;
                drawing.DrawRoundedRectangle(Brush(fraction, fill), null, new Rect(x, y, blockWidth, body), radius, radius);
            }
        }
    }

    private void DrawAtmosphere(DrawingContext drawing, double width, double height)
    {
        var intensity = Limit(Settings.AtmosphereIntensity, 0, 1, .6);
        if (intensity <= 0 || Settings.Atmosphere == VisualAtmosphere.None) return;
        var origin = new Point(width / 2, height / 2);
        switch (Settings.Atmosphere)
        {
            case VisualAtmosphere.Mist:
                for (var i = 0; i < 4; i++)
                {
                    var angle = i * 1.9 + phase * (.21 + i * .044);
                    var position = new Point(origin.X + Math.Cos(angle) * width * .26, origin.Y + Math.Sin(angle) * height * .29);
                    var brush = MistBrush(i, (.11 + energy * .4) * intensity);
                    drawing.DrawEllipse(brush, null, position, width * (.21 + energy * .10), height * (.27 + energy * .13));
                }
                break;
            case VisualAtmosphere.Dust:
                DrawParticles(drawing, width, height, intensity, false);
                break;
            case VisualAtmosphere.Ripples:
                for (var i = 0; i < 5; i++)
                {
                    var spread = Wrap(phase * .24 + i / 5.0);
                    var alpha = (1 - spread) * intensity * (.13 + energy * .7);
                    drawing.DrawEllipse(null, PenFor(i / 4.0, alpha, .75 + beat * 2.5), origin,
                        width * (.10 + spread * .47), height * (.10 + spread * .47));
                }
                break;
        }
    }

    private float[] History(int age) => history[(historyHead + HistoryLength - age) % HistoryLength];

    private double FrequencyPosition(double t) => Settings.Mirror ? Math.Abs(2 * t - 1) : t;

    private double Spectrum(double t) => Sample(bands, t);

    private double Wave(double t, bool right)
    {
        var channel = right ? Frame?.Right : Frame?.Left;
        return Math.Clamp(Sample(channel, t) * Limit(Settings.Sensitivity, .1, 4, 1), -1, 1);
    }

    private double StrokeSize(double width, double height) => Math.Max(.75, Math.Min(width, height) * (.002 + barWidth * .012));

    private void DrawStroke(DrawingContext drawing, Point from, Point to, double shade, double opacity, double thickness) =>
        drawing.DrawLine(PenFor(shade, opacity, thickness), from, to);

    private Pen PenFor(double shade, double opacity, double thickness) =>
        RoundedPen(Brush(shade, opacity), thickness);

    private Pen HaloPenFor(double shade, double opacity, double thickness)
    {
        var color = ColorIndex(shade);
        var alpha = AlphaIndex(opacity);
        var width = (int)Math.Round(thickness * HaloPenThicknessSteps);
        var key = (color, alpha, width);
        if (haloPens.TryGetValue(key, out var pen)) return pen;

        // Subpixel width buckets allow the pulsing spokes to reuse immutable pens.
        pen = RoundedPen(colors[color, alpha], width / HaloPenThicknessSteps);
        pen.Freeze();
        if (haloPens.Count == HaloPenCacheCapacity) haloPens.Clear();
        haloPens.Add(key, pen);
        return pen;
    }

    private static Pen RoundedPen(SolidColorBrush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round
    };

    private SolidColorBrush Brush(double shade, double opacity) =>
        colors[ColorIndex(shade), AlphaIndex(opacity)];

    private static int ColorIndex(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * (ColorSteps - 1));

    private static int AlphaIndex(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * (AlphaSteps - 1));

    private RadialGradientBrush MistBrush(int spot, double opacity)
    {
        var alpha = (byte)(Math.Clamp(opacity, 0, 1) * 255);
        if (mistBrushes[spot, alpha] is { } brush) return brush;

        var color = gradient[ColorIndex(spot / 3.0)];
        brush = new RadialGradientBrush(
            Color.FromArgb(alpha, color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B));
        brush.Freeze();
        mistBrushes[spot, alpha] = brush;
        return brush;
    }

    private static StreamGeometry Path(int segments, Func<int, Point> pointAt, bool closed = false)
    {
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(pointAt(0), closed, closed);
            for (var i = 1; i < segments; i++) stream.LineTo(pointAt(i), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static double Sample(float[]? samples, double fraction)
    {
        if (samples is null || samples.Length == 0) return 0;
        var index = Math.Clamp(fraction, 0, 1) * (samples.Length - 1);
        var first = (int)index;
        var second = Math.Min(first + 1, samples.Length - 1);
        return samples[first] + (samples[second] - samples[first]) * (index - first);
    }

    private static double ChannelLevel(float[]? samples, double gain)
    {
        if (samples is null || samples.Length == 0) return 0;
        var energy = 0.0;
        foreach (var sample in samples) energy += sample * sample;
        var rms = Math.Sqrt(energy / samples.Length) * gain;
        return Math.Clamp(Math.Sqrt(rms) * 1.55, 0, 1);
    }

    private static Point Polar(Point center, double angle, double radius) =>
        new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);

    private static double Follow(double position, double target, double attack, double release) =>
        position + (target - position) * (target > position ? attack : release);

    private static double Limit(double value, double lower, double upper, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, lower, upper) : fallback;

    private static double Wrap(double value) => value - Math.Floor(value);

    private static double Unit(int seed, int salt) => Wrap(Math.Sin(seed * 12.9898 + salt * 78.233) * 43758.5453);

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromRgb(red, green, blue);

    private static Color LerpColor(Color first, Color second, double position) => Rgb(
        (byte)Math.Round(first.R + (second.R - first.R) * position),
        (byte)Math.Round(first.G + (second.G - first.G) * position),
        (byte)Math.Round(first.B + (second.B - first.B) * position));

    private static (Color, Color, Color) CustomStops(string? main, string? accent)
    {
        var first = ParseColor(main, Rgb(51, 223, 199));
        var last = ParseColor(accent, Rgb(164, 140, 255));
        return (first, LerpColor(first, last, .48), last);
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        var digits = value?.Trim().TrimStart('#');
        if (digits?.Length != 6 || !uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var color)) return fallback;
        return Rgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
    }
}
