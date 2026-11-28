using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio = Sada.StudioUi;

namespace Sada;

internal sealed class GlitchBox : FrameworkElement
{
    private const int TransitionTime = 300;
    private const int BurstTime = 260;
    private const int BurstGap = 340;
    private const double BurstThreshold = .025;
    private const int ChannelSize = 480;

    private static readonly Brush Recess = Studio.Brush("#DA122032");
    private static readonly Pen Rim = CreatePen("#906789A4", 1);
    private static readonly Pen EmptyMark = CreatePen("#8898B8D2", 1.35);
    private static readonly Brush RedInk = Studio.Brush("#FC638D");
    private static readonly Brush CyanInk = Studio.Brush("#64E8F2");
    private static readonly Typeface TitleTypeface = new(new FontFamily("Bahnschrift"), FontStyles.Normal,
        FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush ScanPattern = CreateScanPattern();
    private static readonly ConditionalWeakTable<BitmapSource, Channels> ChannelCache = new();

    private sealed record Channels(BitmapSource Red, BitmapSource Cyan);
    private readonly record struct Slice(double Top, double Height, double Offset);
    private readonly record struct TrackIdentity(string Source, string Title, string Artist, string Album, string? VideoId);
    private readonly record struct Content(BitmapSource? Artwork, string Title, Color Tint);
    private sealed class Card(Content content)
    {
        internal Content Value { get; } = content;
        internal Brush TitleInk { get; } = MakeTitleInk(content.Tint);
        internal Pen SignalEdge { get; } = CreatePen(Color.FromArgb(167, content.Tint.R, content.Tint.G, content.Tint.B), 1);
        internal Geometry? Lettering { get; set; }
        internal Rect Bounds { get; set; }
        internal double Scale { get; set; }
    }

    private Card? current;
    private Card? outgoing;
    private TrackIdentity? track;
    private long? swappedAt;
    private long? burstAt;
    private long lastTime;
    private double transition = 1;
    private double amplitude;
    private double burstGain;
    private float previousBeat;
    private float previousLevel;
    private bool scanlines;
    private int burstNumber;
    private readonly Slice[] burstSlices = new Slice[3];
    private readonly Rect[] burstBlocks = new Rect[4];
    private Rect face;
    private Geometry? faceClip;

    internal GlitchBox()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    internal void Render(BitmapSource? cover, MediaSnapshot song, AudioFrame frame, GlitchOptions options, Color? tint = null) =>
        RenderAt(cover, song, frame, options, Environment.TickCount64, !SystemParameters.ClientAreaAnimation, tint);

    internal void RenderAt(BitmapSource? cover, MediaSnapshot song, AudioFrame frame, GlitchOptions options,
        long milliseconds, bool reducedMotion, Color? tint = null)
    {
        long now = Math.Max(lastTime, milliseconds);
        lastTime = now;
        double intensity = double.IsFinite(options.Intensity) ? Math.Clamp(options.Intensity, 0, 1) : .65;
        double response = double.IsFinite(options.MusicResponse) ? Math.Clamp(options.MusicResponse, 0, 3) : 1;
        bool animate = !reducedMotion && intensity > 0;
        var identity = song.YouTubeVideoId is { } video ? new TrackIdentity("", "", "", "", video)
            : new TrackIdentity(song.Source, song.Title, song.Artist, song.Album, null);
        bool changedTrack = track is not null && track != identity;
        var artwork = options.Content == GlitchContent.Artwork && song.HasTrack ? cover : null;
        var title = artwork is null && song.HasTrack ? song.Title : "";
        var content = new Content(artwork, title, tint ?? Studio.Mint.Color);
        bool changedContent = current?.Value != content;
        bool changedArtwork = !ReferenceEquals(current?.Value.Artwork, artwork);
        bool changedFace = changedArtwork || !string.Equals(current?.Value.Title ?? "", title, StringComparison.Ordinal);
        bool refresh = changedContent || scanlines != options.Scanlines;
        track = identity;
        scanlines = options.Scanlines;
        if (changedContent)
        {
            if (changedFace && animate && current is not null)
            {
                // During rapid skips retain the layer the viewer could actually see.
                outgoing = swappedAt.HasValue && transition < .5 ? outgoing : current;
                swappedAt = now;
            }
            current = new Card(content);
        }

        float beat = float.IsFinite(frame.Beat) ? Math.Clamp(frame.Beat, 0, 1) : 0;
        float level = float.IsFinite(frame.Level) ? Math.Clamp(frame.Level, 0, 1) : 0;
        bool onset = beat >= .66f && beat - previousBeat >= .20f;
        bool swell = level >= .54f && level - previousLevel >= .18f;
        bool canBurst = burstAt is not long lastBurst || now - lastBurst >= BurstGap;
        if (animate && song.HasTrack && ((changedArtwork && outgoing is not null) || changedTrack))
            BeginBurst(now, .88);
        else if (animate && song.HasTrack && song.IsPlaying && response > 0 && (onset || swell) && canBurst)
        {
            var lowBand = frame.Spectrum.Length > 3 && float.IsFinite(frame.Spectrum[3])
                ? Math.Clamp(frame.Spectrum[3], 0, 1) : 0;
            double signal = onset ? .43 + .3 * beat + .15 * lowBand : .27 + .23 * level;
            double multiplier = response <= 1 ? response : 1 + (response - 1) * .33;
            double gain = Math.Min(1.24, signal * multiplier);
            BeginBurst(now, gain);
        }
        previousBeat = beat;
        previousLevel = level;

        double blend = animate && swappedAt is long start
            ? Ease((now - start) / (double)TransitionTime)
            : 1;
        if (blend >= 1)
        {
            swappedAt = null;
            outgoing = null;
        }

        double nextAmplitude = animate && burstAt is long active
            ? intensity * burstGain * Envelope((now - active) / (double)BurstTime)
            : 0;
        if (!animate) burstAt = null;
        if (refresh || Math.Abs(transition - blend) > .001 || Math.Abs(amplitude - nextAmplitude) > .008 ||
            (amplitude >= BurstThreshold) != (nextAmplitude >= BurstThreshold))
        {
            transition = blend;
            amplitude = nextAmplitude;
            InvalidateVisual();
        }
    }

    private void BeginBurst(long now, double gain)
    {
        burstAt = now;
        burstGain = gain;
        uint phase = unchecked((uint)(++burstNumber * 73));
        for (int i = 0; i < burstSlices.Length; i++)
        {
            phase = unchecked(phase * 1664525u + 1013904223u);
            double height = .036 + ((phase >> 16) & 31) / 420d;
            double top = .05 + i * .31 + ((phase >> 8) & 31) / 225d;
            double offset = (.42 + (phase & 31) / 58d) * ((phase & 128) == 0 ? -1 : 1);
            burstSlices[i] = new Slice(top, height, offset);
        }
        for (int i = 0; i < burstBlocks.Length; i++)
        {
            phase = unchecked(phase * 1664525u + 1013904223u);
            var lane = burstSlices[i % burstSlices.Length];
            double left = .08 + ((phase >> 16) & 63) / 94d;
            double width = .025 + ((phase >> 8) & 31) / 310d;
            burstBlocks[i] = new Rect(left, lane.Top + lane.Height * .66, width, lane.Height * .34);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        faceClip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 12 || ActualHeight < 12) return;
        if (faceClip is null)
        {
            face = new Rect(1, 1, ActualWidth - 2, ActualHeight - 2);
            double corner = Math.Clamp(Math.Min(face.Width, face.Height) * .045, 2, 12);
            faceClip = new RectangleGeometry(face, corner, corner);
            faceClip.Freeze();
        }

        drawing.PushClip(faceClip);
        drawing.DrawRectangle(Recess, null, face);
        if (transition < 1)
        {
            DrawCard(drawing, outgoing);
            drawing.PushOpacity(transition);
        }

        DrawCard(drawing, current);
        if (current is not null && amplitude >= BurstThreshold)
            DrawBurst(drawing, current);
        if (transition < 1) drawing.Pop();
        if (scanlines && (HasContent(current) || HasContent(outgoing)))
            drawing.DrawRectangle(ScanPattern, null, face);
        drawing.Pop();
        drawing.DrawGeometry(null, Rim, faceClip);
    }

    private static bool HasContent(Card? card) =>
        card is not null && (card.Value.Artwork is not null || card.Value.Title.Length > 0);

    private void DrawCard(DrawingContext drawing, Card? card)
    {
        if (card?.Value.Artwork is { } cover)
        {
            DrawCroppedImage(drawing, cover, 0);
            return;
        }

        drawing.DrawRectangle(Recess, null, face);
        if (card is not null && card.Value.Title.Length > 0)
        {
            DrawTitle(drawing, card, card.TitleInk, 0);
            return;
        }

        double diameter = Math.Min(face.Width, face.Height) * .22;
        var center = new Point(face.Left + face.Width * .5, face.Top + face.Height * .5);
        drawing.DrawEllipse(null, EmptyMark, center, diameter / 2, diameter / 2);
        drawing.DrawEllipse(null, EmptyMark, center, diameter * .30, diameter * .30);
        drawing.DrawEllipse(Studio.Mint, null, center, diameter * .065, diameter * .065);
    }

    private void DrawBurst(DrawingContext drawing, Card card)
    {
        BitmapSource? cover = card.Value.Artwork;
        var channels = cover is not null ? ChannelCache.GetValue(cover, CreateChannels) : null;
        if (channels is null && card.Value.Title.Length == 0) return;

        double travel = amplitude * Math.Clamp(face.Width * .13, 5, 28);
        foreach (var slice in burstSlices)
        {
            var band = new Rect(face.Left, face.Top + face.Height * slice.Top, face.Width, face.Height * slice.Height);
            drawing.PushClip(new RectangleGeometry(band));
            double displacement = travel * slice.Offset;
            if (cover is not null)
                DrawCroppedImage(drawing, cover, displacement);
            else
            {
                drawing.DrawRectangle(Recess, null, band);
                DrawTitle(drawing, card, card.TitleInk, displacement);
            }
            drawing.PushOpacity(Math.Min(.56, .64 * amplitude));
            if (channels is not null)
                DrawCroppedImage(drawing, channels.Red, displacement + travel * .52);
            else
                DrawTitle(drawing, card, RedInk, displacement + travel * .52);
            drawing.Pop();
            drawing.PushOpacity(Math.Min(.49, .55 * amplitude));
            if (channels is not null)
                DrawCroppedImage(drawing, channels.Cyan, displacement - travel * .49);
            else
                DrawTitle(drawing, card, CyanInk, displacement - travel * .49);
            drawing.Pop();
            drawing.PushOpacity(amplitude * .65);
            drawing.DrawLine(card.SignalEdge, band.TopLeft, new Point(band.Left + band.Width * .55, band.Top));
            drawing.Pop();
            drawing.Pop();
        }
        if (amplitude < .43) return;
        drawing.PushOpacity(Math.Min(.25, (amplitude - .43) * .65));
        for (int i = 0; i < burstBlocks.Length; i++)
        {
            var block = burstBlocks[i];
            var region = new Rect(face.Left + face.Width * block.X, face.Top + face.Height * block.Y,
                face.Width * block.Width, Math.Max(1, face.Height * block.Height));
            drawing.DrawRectangle(i % 2 == 0 ? RedInk : CyanInk, null, region);
        }
        drawing.Pop();
    }

    private void DrawTitle(DrawingContext drawing, Card card, Brush ink, double offset)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (card.Lettering is null || card.Bounds != face || card.Scale != pixelsPerDip)
        {
            card.Bounds = face;
            card.Scale = pixelsPerDip;
            card.Lettering = BuildTitle(card.Value.Title, pixelsPerDip);
        }

        if (offset != 0) drawing.PushTransform(new TranslateTransform(offset, 0));
        drawing.DrawGeometry(ink, null, card.Lettering);
        if (offset != 0) drawing.Pop();
    }

    private Geometry BuildTitle(string title, double pixelsPerDip)
    {
        var starts = StringInfo.ParseCombiningCharacters(title);
        if (starts.Length > 160) title = title[..starts[160]] + "…";
        double size = Math.Clamp(Math.Min(face.Width * .15, face.Height * .22), 8, 43);
        double inset = Math.Clamp(face.Width * .08, 3, 22);
        FlowDirection direction = LyricFlow.DirectionFor(title);
        var text = new FormattedText(title, CultureInfo.CurrentUICulture, direction, TitleTypeface, size, Brushes.White, pixelsPerDip)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(1, face.Width - inset * 2),
            MaxTextHeight = Math.Max(1, face.Height - 4),
            MaxLineCount = 3,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var geometry = text.BuildGeometry(default);
        var bounds = geometry.Bounds;
        if (!bounds.IsEmpty)
        {
            geometry.Transform = new TranslateTransform(
                face.Left + (face.Width - bounds.Width) * .5 - bounds.Left,
                face.Top + (face.Height - bounds.Height) * .5 - bounds.Top);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawCroppedImage(DrawingContext drawing, BitmapSource cover, double offset)
    {
        double scale = Math.Max((face.Width + Math.Abs(offset) * 2) / cover.PixelWidth, face.Height / cover.PixelHeight);
        double width = cover.PixelWidth * scale;
        double height = cover.PixelHeight * scale;
        var crop = new Rect(face.Left + (face.Width - width) * .5 + offset,
            face.Top + (face.Height - height) * .5, width, height);
        drawing.DrawImage(cover, crop);
    }

    private static Channels CreateChannels(BitmapSource source)
    {
        double scale = Math.Min(1, ChannelSize / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        if (scale < 1)
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var samples = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = samples.PixelWidth * 4;
        var pixels = new byte[stride * samples.PixelHeight];
        var reds = new byte[pixels.Length];
        var cyans = new byte[pixels.Length];
        samples.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // Use red and cyan channel images only in the short displaced bands.
            // The regular frame always keeps its original cover colors.
            int brightness = (pixels[i + 2] * 3 + pixels[i + 1] * 6 + pixels[i]) / 10;
            reds[i] = 38;
            reds[i + 1] = 19;
            reds[i + 2] = (byte)Math.Max(pixels[i + 2], brightness);
            reds[i + 3] = (byte)(pixels[i + 3] * (80 + brightness * 175 / 255) / 255);
            cyans[i] = (byte)Math.Max(pixels[i], brightness);
            cyans[i + 1] = (byte)Math.Max(pixels[i + 1], brightness);
            cyans[i + 2] = 12;
            cyans[i + 3] = reds[i + 3];
        }

        BitmapSource Plate(byte[] plate)
        {
            var bitmap = BitmapSource.Create(samples.PixelWidth, samples.PixelHeight, 96, 96, PixelFormats.Bgra32, null, plate, stride);
            bitmap.Freeze();
            return bitmap;
        }

        return new Channels(Plate(reds), Plate(cyans));
    }

    private static double Ease(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static double Envelope(double progress) =>
        Ease(progress / .13) * (1 - Ease((progress - .12) / .88));

    private static Brush MakeTitleInk(Color color)
    {
        static byte Channel(byte value) => (byte)(value + (255 - value) * .31);
        var brush = new SolidColorBrush(Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B)));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(string color, double thickness) =>
        CreatePen((Color)ColorConverter.ConvertFromString(color), thickness);

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateScanPattern()
    {
        var stripe = new GeometryDrawing(Studio.Brush("#1A08101D"), null, new RectangleGeometry(new Rect(0, 0, 8, 1)));
        var brush = new DrawingBrush(stripe)
        {
            Viewbox = new Rect(0, 0, 8, 4),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 8, 4),
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.Tile,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
