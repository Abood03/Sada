using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Sada;

public enum NeonLettering { Script, Block, TallCapitals }
public enum NeonReaction { Chase, Equalizer, Surge, Flare, Steady }
public enum NeonFlicker { Steady, Occasional, BrokenLetter }

internal sealed class NeonSign : FrameworkElement
{
    const double PowerOffMilliseconds = 275;
    const double TrackChangeMilliseconds = 335;
    const double IdleGraceMilliseconds = 6000;
    const double IdleFadeMilliseconds = 280;
    const double MaxIgnitionMilliseconds = 1050;
    const int SpectrumZones = 10;

    static readonly Typeface Script = new("Segoe Script");
    static readonly Typeface Block = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Typeface Tall = new(new FontFamily("Bahnschrift Condensed, Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.ExtraCondensed);
    static readonly Typeface ArtistTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface NoteTypeface = new("Segoe UI Symbol");
    static readonly Color DefaultTitle = Color.FromRgb(112, 248, 204);
    static readonly Color DefaultArtist = Color.FromRgb(254, 186, 111);
    static readonly TubeStyle DefaultTubes = new(DefaultTitle, DefaultArtist);

    readonly record struct TrackIdentity(string Source, string Title, string Artist, string Album, string? VideoId);
    readonly record struct SignContent(string Title, string Artist, TrackIdentity Track, NeonLettering Lettering, bool ShowNote);
    sealed record WordArt(Geometry Text, Geometry[] Prefixes, Geometry[] Clusters, Geometry[] Zones, TranslateTransform Translate);
    sealed class TubeStyle(Color title, Color artist)
    {
        internal Color Title { get; } = title;
        internal Color Artist { get; } = artist;
        internal TubeInk TitleInk { get; } = new(title, 1);
        internal TubeInk ArtistInk { get; } = new(artist, .66);
        internal Pen Lead { get; } = Tube(title, .55, 1.4);
    }

    sealed class TubeInk(Color hue, double scale)
    {
        internal Pen[] Halo { get; } =
        [
            Tube(hue, .028, 39 * scale), Tube(hue, .048, 31 * scale), Tube(hue, .077, 24 * scale),
            Tube(hue, .12, 17 * scale), Tube(hue, .18, 11 * scale), Tube(hue, .30, 5.6 * scale)
        ];
        internal SolidColorBrush Glass { get; } = Brush(NeonSign.Glass(hue));
        internal LinearGradientBrush Filament { get; } = GlowFilament(hue);
        internal Pen Shell { get; } = Tube(Mix(hue, Colors.Black, .28), .90, 2.5 * scale);
        internal Pen GlassEdge { get; } = Tube(Mix(hue, Colors.White, .54), .63, .85 * scale);
        internal Pen Flare { get; } = Tube(Mix(hue, Colors.White, .36), .50, 5.4 * scale);
    }

    static readonly Pen GlassRim = Tube(Color.FromRgb(18, 22, 28), .84, 2.3);
    static readonly Pen GlassEdge = Tube(Color.FromRgb(158, 174, 186), .35, .7);
    static readonly Pen OffLead = Tube(Color.FromRgb(135, 150, 159), .39, 1.2);

    SignContent? displayed;
    SignContent? incoming;
    WordArt? title;
    WordArt? artist;
    WordArt? note;
    TubeStyle tubes = DefaultTubes;
    readonly double[] spectrum = new double[SpectrumZones];
    NeonReaction reaction;
    NeonFlicker flicker;
    double strength;
    double glowIntensity = 1;
    bool glassTrim = true;
    double clock = double.NaN;
    double switchedAt;
    double fadingAt = double.NaN;
    double pauseAt = double.NaN;
    double powerAtPause;
    double chase = 1;
    double previousBeat;
    double oldReveal;
    double reveal = 1;
    double fade = 1;
    double charge;
    double flare;
    double level;
    double surgeDip;
    double flickerOpacity = 1;
    int flickerIndex = -1;
    int cutoutIndex = -1;
    Geometry? cutout;
    Point leadTop, leadBottom;

    internal NeonSign()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    internal double IdleOpacity { get; private set; } = 1;

    internal void Render(MediaSnapshot song, AudioFrame frame, NeonOptions options, Color? titleColor = null, Color? artistColor = null) =>
        RenderAt(song, frame, options, Environment.TickCount64, titleColor, artistColor);

    internal void RenderAt(MediaSnapshot song, AudioFrame frame, NeonOptions options, long milliseconds,
        Color? titleColor = null, Color? artistColor = null)
    {
        double now = double.IsNaN(clock) ? milliseconds : Math.Max(clock, milliseconds);
        double elapsed = double.IsNaN(clock) ? 16 : Math.Min(1000, now - clock);
        clock = now;
        var track = song.YouTubeVideoId is { } id ? new TrackIdentity("", "", "", "", id)
            : new TrackIdentity(song.Source, song.Title, song.Artist, song.Album, null);
        var updated = new SignContent(song.HasTrack ? song.Title : "Waiting for music",
            song.HasTrack && options.ShowArtist ? song.Artist : "", track, options.Lettering, options.ShowNote);
        bool settingsChanged = UpdateReactionOptions(options);
        bool animate = SystemParameters.ClientAreaAnimation;
        double pausedFor = double.IsNaN(pauseAt) ? 0 : now - pauseAt;
        bool resume = song.IsPlaying && pausedFor >= PowerOffMilliseconds;
        if (song.IsPlaying) pauseAt = double.NaN;
        else if (double.IsNaN(pauseAt))
        {
            pauseAt = now;
            powerAtPause = charge;
        }

        bool changed = updated != incoming;
        if (changed)
        {
            incoming = updated;
            if (!animate || displayed is null || updated == displayed || displayed?.Track == updated.Track)
            {
                if (displayed != updated) ShowIncoming(displayed?.Track == updated.Track && !resume ? now - MaxIgnitionMilliseconds : now);
                fadingAt = double.NaN;
            }
            else if (double.IsNaN(fadingAt))
            {
                oldReveal = reveal;
                fadingAt = now;
            }
        }

        double nextFade = 1;
        double nextReveal;
        if (animate && !double.IsNaN(fadingAt) && now - fadingAt < TrackChangeMilliseconds)
        {
            nextFade = PowerDown(now - fadingAt);
            nextReveal = oldReveal;
        }
        else
        {
            if (!double.IsNaN(fadingAt))
            {
                ShowIncoming(fadingAt + TrackChangeMilliseconds);
                fadingAt = double.NaN;
            }
            if (animate && resume) switchedAt = now;
            double revealTime = Math.Clamp((title?.Prefixes.Length ?? displayed?.Title.Length ?? 0) * 36, 280, MaxIgnitionMilliseconds);
            nextReveal = animate ? Math.Clamp((now - switchedAt) / revealTime, 0, 1) : 1;
        }

        // Keep the outgoing track's ink while waiting for its replacement.
        if (displayed?.Track == updated.Track)
            settingsChanged |= SetInk(titleColor ?? DefaultTitle, artistColor ?? DefaultArtist);

        var idleTarget = options.Idle switch
        {
            NeonIdle.Fade => .30,
            NeonIdle.Hide => 0d,
            _ => 1d
        };
        double idleProgress = song.IsPlaying ? 0 : animate
            ? Ease((now - pauseAt - IdleGraceMilliseconds) / IdleFadeMilliseconds)
            : now - pauseAt >= IdleGraceMilliseconds ? 1 : 0;
        IdleOpacity = 1 - (1 - idleTarget) * idleProgress;
        double requestedFlare = song.IsPlaying && animate && reaction != NeonReaction.Steady ? Signal(frame.Beat) : 0;
        double nextCharge = song.IsPlaying
            ? animate ? Approach(resume ? 0 : charge, 1, elapsed, 95) : 1
            : animate ? powerAtPause * PowerDown(now - pauseAt) : 0;
        double nextFlare = Approach(flare, requestedFlare, elapsed, requestedFlare > flare ? 56 : 175);
        bool moving = Math.Abs(nextFade - fade) > .0001 || Math.Abs(nextReveal - reveal) > .0001 ||
            Math.Abs(nextCharge - charge) > .0001 || Math.Abs(nextFlare - flare) > .0001;
        moving |= UpdateReaction(frame, song.IsPlaying && animate, requestedFlare, elapsed, now);
        (fade, reveal, charge, flare) = (nextFade, nextReveal, nextCharge, nextFlare);
        if (changed || settingsChanged || moving) InvalidateVisual();
    }

    bool UpdateReactionOptions(NeonOptions options)
    {
        var nextStrength = double.IsFinite(options.ReactionStrength) ? Math.Clamp(options.ReactionStrength, 0, 2) : 1;
        var nextGlow = double.IsFinite(options.GlowIntensity) ? Math.Clamp(options.GlowIntensity, 0, 2) : 1;
        bool changed = options.Reaction != reaction || options.Flicker != flicker || nextStrength != strength ||
            nextGlow != glowIntensity || glassTrim != options.GlassTrim;
        (reaction, flicker, strength) = (options.Reaction, options.Flicker, nextStrength);
        (glowIntensity, glassTrim) = (nextGlow, options.GlassTrim);
        if (reaction != NeonReaction.Surge) surgeDip = 0;
        return changed;
    }

    bool SetInk(Color titleColor, Color artistColor)
    {
        if (tubes.Title == titleColor && tubes.Artist == artistColor) return false;
        tubes = titleColor == DefaultTitle && artistColor == DefaultArtist ? DefaultTubes : new TubeStyle(titleColor, artistColor);
        return true;
    }

    bool UpdateReaction(AudioFrame frame, bool playing, double requestedFlare, double elapsed, double now)
    {
        bool changed = false;
        if (reaction == NeonReaction.Surge)
        {
            double nextLevel = Approach(level, playing ? Signal(frame.Level) : 0, elapsed, 125);
            double nextDip = playing && requestedFlare >= .76 && previousBeat < .76
                ? 1 : Approach(surgeDip, 0, elapsed, 115);
            changed = Math.Abs(nextLevel - level) > .0001 || Math.Abs(nextDip - surgeDip) > .0001;
            level = nextLevel;
            surgeDip = nextDip;
        }
        if (playing && reaction == NeonReaction.Equalizer && strength > 0)
        {
            for (int zone = 0; zone < spectrum.Length; zone++)
            {
                var band = frame.Spectrum.Length == 0 ? 0 : Signal(frame.Spectrum[zone * (frame.Spectrum.Length - 1) / (spectrum.Length - 1)]);
                double nextBand = Approach(spectrum[zone], band, elapsed, 105);
                changed |= Math.Abs(nextBand - spectrum[zone]) > .0001;
                spectrum[zone] = nextBand;
            }
        }
        double nextChase = playing && reaction == NeonReaction.Chase && strength > 0 && requestedFlare >= .34 && previousBeat < .34
            ? 0 : Math.Min(1, chase + elapsed / 440);
        changed |= nextChase != chase;
        chase = nextChase;
        previousBeat = requestedFlare;

        double nextOpacity = 1;
        int nextIndex = -1;
        if (playing && title is { Clusters.Length: > 0 } text && displayed is { } sign && reveal >= 1)
        {
            long elapsedSong = (long)Math.Max(0, now - switchedAt);
            long period = flicker == NeonFlicker.BrokenLetter ? 3100 : 8800;
            long through = elapsedSong % period;
            if (flicker != NeonFlicker.Steady && through is >= 1100 and < 1290)
            {
                var seed = StableSeed(sign.Title);
                if (flicker == NeonFlicker.Occasional) seed += (uint)(elapsedSong / period);
                nextIndex = (int)(seed % text.Clusters.Length);
                nextOpacity -= (flicker == NeonFlicker.BrokenLetter ? .96 : .56) * Math.Sin(Math.PI * (through - 1100) / 190);
            }
        }
        changed |= nextIndex != flickerIndex || Math.Abs(nextOpacity - flickerOpacity) > .0001;
        (flickerIndex, flickerOpacity) = (nextIndex, nextOpacity);
        return changed;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateShape();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateShape();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (displayed is not { } sign || ActualWidth < 24 || ActualHeight < 20) return;
        if (title is null) ArrangeSign(sign);
        if (title is null) return;

        var response = reaction == NeonReaction.Surge ? SurgePower() : 1;
        var intensity = fade * charge * response;
        DrawGlass(drawing, 1 - intensity * Ease(reveal));
        if (intensity <= .001) return;

        var audioFlare = Math.Clamp(flare * strength, 0, 1);
        drawing.PushOpacity(intensity * Ease(reveal * 3));
        drawing.DrawLine(tubes.Lead, leadTop, leadBottom);
        if (note is { } symbol)
        {
            drawing.PushTransform(symbol.Translate);
            var noteFlare = Math.Max(audioFlare, .82 * Arc(reveal * 3.5));
            DrawTube(drawing, symbol.Text, tubes.TitleInk, noteFlare);
            drawing.Pop();
        }
        drawing.Pop();

        drawing.PushOpacity(intensity);
        drawing.PushTransform(title.Translate);
        DrawTitleProgress(drawing, title);
        drawing.Pop();
        drawing.Pop();

        double artistReveal = Math.Clamp((reveal - .35) / .65, 0, 1);
        if (artist is not { } credit || artistReveal <= 0) return;
        drawing.PushOpacity(intensity * (.82 + .18 * Ease(artistReveal)));
        drawing.PushTransform(credit.Translate);
        DrawProgress(drawing, credit, tubes.ArtistInk, artistReveal, audioFlare * .35);
        drawing.Pop();
        drawing.Pop();
    }

    void DrawGlass(DrawingContext drawing, double opacity)
    {
        if (opacity <= .001 || title is null) return;
        drawing.PushOpacity(opacity);
        drawing.DrawLine(OffLead, leadTop, leadBottom);
        DrawUnlitTube(drawing, title, tubes.TitleInk);
        if (artist is { } credit) DrawUnlitTube(drawing, credit, tubes.ArtistInk);
        if (note is { } symbol) DrawUnlitTube(drawing, symbol, tubes.TitleInk);
        drawing.Pop();
    }

    void DrawUnlitTube(DrawingContext drawing, WordArt art, TubeInk ink)
    {
        drawing.PushTransform(art.Translate);
        drawing.DrawGeometry(ink.Glass, GlassRim, art.Text);
        if (glassTrim) drawing.DrawGeometry(null, GlassEdge, art.Text);
        drawing.Pop();
    }

    void DrawTitleProgress(DrawingContext drawing, WordArt text)
    {
        double wholeSignFlare = reaction switch
        {
            NeonReaction.Flare => Math.Clamp(flare * strength, 0, 1),
            NeonReaction.Surge => .45 * Math.Clamp(level * strength, 0, 1) * (1 - surgeDip),
            _ => 0
        };
        if (reveal >= 1 || text.Prefixes.Length == 0)
        {
            DrawLitTitle(drawing, text, wholeSignFlare);
            return;
        }
        DrawProgress(drawing, text, tubes.TitleInk, reveal, wholeSignFlare);
    }

    void DrawProgress(DrawingContext drawing, WordArt text, TubeInk ink, double progress, double pulse)
    {
        if (progress >= 1 || text.Prefixes.Length == 0)
        {
            DrawTube(drawing, text.Text, ink, pulse);
            return;
        }
        double position = progress * text.Prefixes.Length;
        int complete = (int)position;
        if (complete > 0)
        {
            drawing.PushClip(text.Prefixes[complete - 1]);
            DrawTube(drawing, text.Text, ink, pulse);
            drawing.Pop();
        }
        double through = position - complete;
        if (complete >= text.Clusters.Length || through <= .001) return;

        double arc = Arc(through);
        drawing.PushOpacity(Math.Clamp(Ease(through) * (1 - .18 * arc) + .31 * arc, 0, 1));
        drawing.PushClip(text.Clusters[complete]);
        DrawTube(drawing, text.Text, ink, Math.Max(pulse, arc));
        if (arc > .02)
        {
            drawing.PushOpacity(.42 * arc);
            drawing.DrawGeometry(null, ink.Flare, text.Text);
            drawing.Pop();
        }
        drawing.Pop();
        drawing.Pop();
    }

    void DrawLitTitle(DrawingContext drawing, WordArt text, double wholeSignFlare)
    {
        if (flickerIndex >= 0 && flickerIndex < text.Clusters.Length)
        {
            if (cutoutIndex != flickerIndex)
            {
                var bounds = text.Text.Bounds;
                bounds.Inflate(21, 21);
                cutout = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), text.Clusters[flickerIndex]);
                cutout.Freeze();
                cutoutIndex = flickerIndex;
            }
            drawing.PushClip(cutout!);
            DrawMusicResponse(drawing, text, wholeSignFlare);
            drawing.Pop();
            drawing.PushOpacity(flickerOpacity);
            drawing.PushClip(text.Clusters[flickerIndex]);
            DrawMusicResponse(drawing, text, wholeSignFlare);
            drawing.Pop();
            drawing.Pop();
            return;
        }
        DrawMusicResponse(drawing, text, wholeSignFlare);
    }

    void DrawMusicResponse(DrawingContext drawing, WordArt text, double wholeSignFlare)
    {
        bool reactive = charge >= .21 && strength > 0;
        if (reactive && reaction == NeonReaction.Equalizer && text.Zones.Length > 0)
        {
            var influence = Math.Min(1, strength);
            drawing.PushOpacity(1 - .69 * influence);
            DrawTube(drawing, text.Text, tubes.TitleInk, wholeSignFlare);
            drawing.Pop();
            for (int i = 0; i < text.Zones.Length; i++)
            {
                var band = spectrum[i * (SpectrumZones - 1) / Math.Max(1, text.Zones.Length - 1)];
                DrawPulse(drawing, text.Text, text.Zones[i], 1.25 * influence * Math.Clamp(band * strength, 0, 1));
            }
            return;
        }
        DrawTube(drawing, text.Text, tubes.TitleInk, wholeSignFlare);
        if (reactive && reaction == NeonReaction.Chase && chase < 1 && text.Clusters.Length > 0)
        {
            double position = Ease(chase) * Math.Max(0, text.Clusters.Length - 1);
            int cluster = (int)position;
            DrawPulse(drawing, text.Text, text.Clusters[cluster], (1 - (position - cluster)) * strength);
            if (cluster + 1 < text.Clusters.Length)
                DrawPulse(drawing, text.Text, text.Clusters[cluster + 1], (position - cluster) * strength);
        }
    }

    double SurgePower() =>
        (1 - .34 * Math.Min(strength, 1) * (1 - Math.Clamp(level * strength, 0, 1))) *
        (1 - Math.Min(.48, .39 * strength) * surgeDip);

    void DrawPulse(DrawingContext drawing, Geometry path, Geometry clip, double intensity)
    {
        if (intensity < .025) return;
        drawing.PushOpacity(Math.Clamp(intensity * .65, 0, .84));
        drawing.PushClip(clip);
        drawing.DrawGeometry(tubes.TitleInk.Filament, tubes.TitleInk.Flare, path);
        drawing.Pop();
        drawing.Pop();
    }

    void DrawTube(DrawingContext drawing, Geometry path, TubeInk ink, double pulse)
    {
        double light = Math.Min(1, glowIntensity * (.53 + .30 * pulse));
        if (light > .001)
        {
            drawing.PushOpacity(light);
            foreach (var pen in ink.Halo) drawing.DrawGeometry(null, pen, path);
            drawing.Pop();
        }
        drawing.DrawGeometry(ink.Filament, ink.Shell, path);
        if (glassTrim) drawing.DrawGeometry(null, ink.GlassEdge, path);
    }

    void ShowIncoming(double now)
    {
        displayed = incoming;
        switchedAt = now;
        chase = 1;
        InvalidateShape();
    }

    void InvalidateShape()
    {
        title = artist = note = null;
        cutout = null;
        cutoutIndex = -1;
        flickerIndex = -1;
        InvalidateVisual();
    }

    void ArrangeSign(SignContent sign)
    {
        string label = sign.Lettering == NeonLettering.TallCapitals ? sign.Title.ToUpperInvariant() : sign.Title;
        var type = sign.Lettering switch { NeonLettering.Block => Block, NeonLettering.TallCapitals => Tall, _ => Script };
        double margin = Math.Clamp(ActualWidth * .055, 9, 30);
        double noteWidth = sign.ShowNote ? Math.Clamp(ActualWidth * .065, 12, 48) : 0;
        double left = margin + 9 + noteWidth;
        double width = Math.Max(4, ActualWidth - left - margin);
        double titleRatio = sign.Lettering == NeonLettering.TallCapitals ? .37 : .32;
        double titleSize = Math.Clamp(ActualHeight * titleRatio, 10, 84);
        double artistSize = Math.Clamp(ActualHeight * .13, 8, 25);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool hasArtist = !string.IsNullOrWhiteSpace(sign.Artist);
        double titleHeight = Math.Max(12, ActualHeight * (hasArtist ? .64 : .84));
        var titleText = Fit(label, type, titleSize, width, titleHeight,
            allowTwoLines: ActualHeight >= 66, pixelsPerDip);
        var titlePath = titleText.BuildGeometry(default);
        titlePath.Freeze();
        if (titlePath.Bounds.IsEmpty) return;

        FittedText? credit = hasArtist ? Fit(sign.Artist, ArtistTypeface, artistSize, width,
            Math.Max(12, ActualHeight * .22), allowTwoLines: false, pixelsPerDip) : null;
        var creditPath = credit?.BuildGeometry(default);
        if (creditPath?.Bounds.IsEmpty == true) creditPath = null;
        double gap = creditPath is null ? 0 : Math.Clamp(ActualHeight * .10, 5, 21);
        double combined = titlePath.Bounds.Height + gap + (creditPath?.Bounds.Height ?? 0);
        double top = Math.Max(4, (ActualHeight - combined) / 2);

        title = ShapeWords(titleText, titlePath, left, top, withSpectrum: true);
        if (credit is { } artistText && creditPath is not null)
            artist = ShapeWords(artistText, creditPath, left, top + titlePath.Bounds.Height + gap, withSpectrum: false);
        if (sign.ShowNote)
        {
            var symbol = Fit("♪", NoteTypeface, Math.Clamp(titleSize * .75, 8, 38), noteWidth,
                Math.Max(16, titleHeight), allowTwoLines: false, pixelsPerDip).BuildGeometry(default);
            if (!symbol.Bounds.IsEmpty)
                note = Place(symbol, [], [], [], margin + 6, top + Math.Max(0, (titlePath.Bounds.Height - symbol.Bounds.Height) / 2));
        }
        leadTop = new Point(margin, top + 3);
        leadBottom = new Point(margin, top + combined - 3);
    }

    static WordArt ShapeWords(FittedText text, Geometry path, double x, double y, bool withSpectrum)
    {
        // Read the shaped run's regions to keep joined letters and diacritics intact.
        var starts = StringInfo.ParseCombiningCharacters(text.Text);
        var groups = new List<(int Start, int End, Geometry Clip)>(starts.Length);
        for (int i = 0; i < starts.Length; i++)
        {
            int start = starts[i];
            if (char.IsWhiteSpace(text.Text[start])) continue;
            int end = i + 1 < starts.Length ? starts[i + 1] : text.Text.Length;
            var clip = Highlight(text, start, end - start);
            if (!clip.Bounds.IsEmpty) groups.Add((start, end, clip));
        }
        var prefixes = new Geometry[groups.Count];
        var clusters = new Geometry[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            prefixes[i] = Highlight(text, 0, groups[i].End);
            clusters[i] = groups[i].Clip;
        }
        var zones = new Geometry[withSpectrum ? Math.Min(SpectrumZones, groups.Count) : 0];
        for (int i = 0; i < zones.Length; i++)
        {
            int start = groups[i * groups.Count / zones.Length].Start;
            int end = groups[(i + 1) * groups.Count / zones.Length - 1].End;
            zones[i] = Highlight(text, start, end - start);
        }
        return Place(path, prefixes, clusters, zones, x, y);
    }

    readonly record struct FittedText(string Text, FormattedText Format)
    {
        internal Geometry BuildGeometry(Point origin) => Format.BuildGeometry(origin);
    }

    static FittedText Fit(string text, Typeface face, double size, double width, double height,
        bool allowTwoLines, double pixelsPerDip)
    {
        var label = string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        var starts = StringInfo.ParseCombiningCharacters(label);
        if (starts.Length > 128) label = label[..starts[128]] + "…";
        var flow = TextDirection(label);
        FormattedText Shape(string value, double points) => new(value, CultureInfo.CurrentUICulture, flow,
            face, points, Brushes.White, pixelsPerDip);
        var shape = Shape(label, size);
        double measuredWidth = shape.WidthIncludingTrailingWhitespace;
        int lines = allowTwoLines && measuredWidth > width * 1.25 ? 2 : 1;
        double room = width * (lines == 2 ? 1.76 : 1);
        double scale = Math.Min(1, Math.Min(room / Math.Max(1, measuredWidth), height / Math.Max(1, lines * shape.Height)));
        if (scale < 1) shape = Shape(label, Math.Max(7, size * scale));
        shape.MaxTextWidth = width;
        shape.MaxLineCount = lines;
        shape.MaxTextHeight = height;
        shape.Trimming = TextTrimming.CharacterEllipsis;
        return new FittedText(label, shape);
    }

    static Geometry Highlight(FittedText text, int start, int length)
    {
        var highlight = text.Format.BuildHighlightGeometry(default, start, length) ?? Geometry.Empty;
        if (highlight.IsFrozen) return highlight;
        highlight.Freeze();
        return highlight;
    }

    static WordArt Place(Geometry path, Geometry[] prefixes, Geometry[] clusters, Geometry[] zones, double x, double y)
    {
        if (!path.IsFrozen) path.Freeze();
        var translate = new TranslateTransform(x - path.Bounds.Left, y - path.Bounds.Top);
        translate.Freeze();
        return new WordArt(path, prefixes, clusters, zones, translate);
    }

    static FlowDirection TextDirection(string text)
    {
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            return ch is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFC'
                ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }

    static SolidColorBrush Brush(Color value)
    {
        var brush = new SolidColorBrush(value);
        brush.Freeze();
        return brush;
    }

    static LinearGradientBrush GlowFilament(Color hue)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(.15, 0), EndPoint = new Point(.78, 1) };
        brush.GradientStops.Add(new GradientStop(Mix(hue, Colors.White, .51), 0));
        brush.GradientStops.Add(new GradientStop(Mix(hue, Colors.White, .89), .25));
        brush.GradientStops.Add(new GradientStop(Mix(hue, Colors.White, .75), .49));
        brush.GradientStops.Add(new GradientStop(Mix(hue, Colors.White, .42), .70));
        brush.GradientStops.Add(new GradientStop(Mix(hue, Colors.White, .65), 1));
        brush.Freeze();
        return brush;
    }

    static Color Mix(Color first, Color second, double amount)
    {
        byte Channel(byte from, byte to) => (byte)(from + (to - from) * amount);
        return Color.FromRgb(Channel(first.R, second.R), Channel(first.G, second.G), Channel(first.B, second.B));
    }

    static Color Glass(Color value)
    {
        static byte Channel(byte current) => (byte)(42 + current * .15);
        return Color.FromArgb(161, Channel(value.R), Channel(value.G), Channel(value.B));
    }

    static Pen Tube(Color color, double alpha, double thickness)
    {
        var pen = new Pen(Brush(Color.FromArgb((byte)(255 * alpha), color.R, color.G, color.B)), thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    static uint StableSeed(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value) hash = unchecked((hash ^ character) * 16777619);
        return hash;
    }

    static double Signal(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    static double Arc(double through) => Math.Sin(Math.PI * Math.Clamp((through - .06) / .83, 0, 1));

    static double Ease(double value)
    {
        double x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }

    static double PowerDown(double elapsed)
    {
        double time = Math.Clamp(elapsed / PowerOffMilliseconds, 0, 1);
        double sputter = 1 - .68 * Math.Pow(Math.Sin(time * Math.PI * 3), 2);
        return (1 - Ease(time)) * sputter;
    }

    static double Approach(double value, double target, double elapsed, double time)
    {
        double difference = target - value;
        return Math.Abs(difference) < .002 ? target : value + difference * (1 - Math.Exp(-elapsed / time));
    }
}
