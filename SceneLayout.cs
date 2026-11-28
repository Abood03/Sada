using System.Windows;

namespace Sada;

internal static class SceneLayout
{
    const double MinimumWidth = 140;
    const double MinimumHeight = 90;
    const double SnapDistance = 9;

    internal static Rect Resolve(WidgetKind kind, WidgetOptions options, Size area, Point legacyOrigin, double scaleX = 1, double scaleY = 1)
    {
        var (x, y) = DefaultAnchor(kind);
        double width = ConstrainSize(options.Width, MinimumWidth, 2000, area.Width);
        double height = ConstrainSize(options.Height, MinimumHeight, 1260, area.Height);
        return new Rect(
            ResolveAxis(options.RelativeLeft, options.Left, legacyOrigin.X, scaleX, area.Width - width, x),
            ResolveAxis(options.RelativeTop, options.Top, legacyOrigin.Y, scaleY, area.Height - height, y),
            width, height);
    }

    internal static void Store(WidgetOptions options, Rect bounds, Size area)
    {
        options.Width = bounds.Width;
        options.Height = bounds.Height;
        options.RelativeLeft = Fraction(bounds.Left, area.Width - bounds.Width);
        options.RelativeTop = Fraction(bounds.Top, area.Height - bounds.Height);
        options.Left = null;
        options.Top = null;
    }

    internal static Rect ResizeTo(Rect original, Size requested, Size area)
    {
        double width = ConstrainSize(requested.Width, MinimumWidth, 2000, area.Width);
        double height = ConstrainSize(requested.Height, MinimumHeight, 1260, area.Height);
        return Fit(new Rect(original.Location, new Size(width, height)), area);
    }

    internal static (Rect Bounds, double? X, double? Y) Move(Rect desired, Size area, IEnumerable<Rect> neighbors, bool snap)
    {
        var result = Fit(desired, area);
        if (!snap) return (result, null, null);
        var (horizontal, vertical) = Targets(area, neighbors);
        var x = Snap([result.Left, result.Left + result.Width / 2, result.Right], horizontal, -result.Left, area.Width - result.Right);
        var y = Snap([result.Top, result.Top + result.Height / 2, result.Bottom], vertical, -result.Top, area.Height - result.Bottom);
        result.Offset(x.Delta, y.Delta);
        return (result, x.Guide, y.Guide);
    }

    internal static (Rect Bounds, double? X, double? Y) Resize(Rect original, Vector delta, Size area, IEnumerable<Rect> neighbors, bool snap)
    {
        double maxWidth = Math.Min(2000, area.Width - original.Left);
        double maxHeight = Math.Min(1260, area.Height - original.Top);
        double minWidth = Math.Min(MinimumWidth, maxWidth);
        double minHeight = Math.Min(MinimumHeight, maxHeight);
        double width = Math.Clamp(original.Width + delta.X, minWidth, maxWidth);
        double height = Math.Clamp(original.Height + delta.Y, minHeight, maxHeight);
        if (!snap) return (new Rect(original.Location, new Size(width, height)), null, null);
        var (horizontal, vertical) = Targets(area, neighbors);
        var x = Snap([original.Left + width], horizontal, minWidth - width, maxWidth - width);
        var y = Snap([original.Top + height], vertical, minHeight - height, maxHeight - height);
        return (new Rect(original.Location, new Size(width + x.Delta, height + y.Delta)), x.Guide, y.Guide);
    }

    static Rect Fit(Rect bounds, Size area) =>
        new(Math.Clamp(bounds.Left, 0, Math.Max(0, area.Width - bounds.Width)),
            Math.Clamp(bounds.Top, 0, Math.Max(0, area.Height - bounds.Height)), bounds.Width, bounds.Height);

    static (List<double> Horizontal, List<double> Vertical) Targets(Size area, IEnumerable<Rect> neighbors)
    {
        var x = new List<double> { 0, area.Width / 2, area.Width };
        var y = new List<double> { 0, area.Height / 2, area.Height };
        foreach (var bounds in neighbors)
        {
            x.Add(bounds.Left);
            x.Add(bounds.Left + bounds.Width / 2);
            x.Add(bounds.Right);
            y.Add(bounds.Top);
            y.Add(bounds.Top + bounds.Height / 2);
            y.Add(bounds.Bottom);
        }
        return (x, y);
    }

    static (double Delta, double? Guide) Snap(double[] positions, List<double> targets, double minDelta, double maxDelta)
    {
        double distance = SnapDistance;
        (double Delta, double? Guide) best = (0, null);
        foreach (double position in positions)
        foreach (double target in targets)
        {
            double delta = target - position;
            if (delta < minDelta || delta > maxDelta || Math.Abs(delta) > distance) continue;
            distance = Math.Abs(delta);
            best = (delta, target);
        }
        return best;
    }

    static double ConstrainSize(double desired, double min, double max, double available) =>
        Math.Min(available, Math.Clamp(double.IsFinite(desired) ? desired : min, min, max));

    static double ResolveAxis(double? relative, double? legacy, double origin, double scale, double room, double anchor)
    {
        if (relative is { } fraction && double.IsFinite(fraction)) return room * Math.Clamp(fraction, 0, 1);
        if (legacy is { } absolute && double.IsFinite(absolute)) return Math.Clamp((absolute - origin) / scale, 0, room);
        return room * anchor;
    }

    static double Fraction(double origin, double room) => room > 0 ? Math.Clamp(origin / room, 0, 1) : 0;

    static (double X, double Y) DefaultAnchor(WidgetKind kind) => kind switch
    {
        WidgetKind.Visualizer => (.76, .39),
        WidgetKind.Clock => (.74, .10),
        WidgetKind.Lyrics => (.64, .88),
        WidgetKind.Media or WidgetKind.Cassette or WidgetKind.Disc or WidgetKind.Neon => (.88, .72),
        WidgetKind.GlitchBox => (.09, .57),
        WidgetKind.Calendar or WidgetKind.Weather => (.31, .20),
        WidgetKind.System or WidgetKind.Disks => (.28, .70),
        _ => (.50, .50)
    };
}

internal static class ScenePresets
{
    internal static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[] { "Album", "Karaoke", "Focus" });

    internal static SceneOptions Apply(string name, SceneOptions source, Size? area = null)
    {
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentOutOfRangeException(nameof(name));
        var result = Preferences.Clone(source);
        result.Name = Names.First(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var widget in result.Widgets.Values) widget.Enabled = false;

        Size surface = area is { Width: >= 400, Height: >= 300 } canvas ? canvas : new Size(1920, 1032);
        bool karaoke = name.Equals("Karaoke", StringComparison.OrdinalIgnoreCase);
        bool focus = name.Equals("Focus", StringComparison.OrdinalIgnoreCase);
        result.Visual = new VisualSettings
        {
            Mode = focus ? VisualMode.Bars : VisualMode.Halo,
            Palette = focus ? VisualPalette.Monochrome : VisualPalette.Aurora,
            Opacity = focus ? .47 : 1,
            Count = focus ? 42 : 62
        };
        result.RingCover = !focus;
        result.MoodColor = !focus;
        result.TrackOnVisualizer = false;
        result.AlbumGlow = false;
        result.LinkCoverAndLyrics = !focus;
        result.LyricsStyle = LyricsStyle.Flow;
        result.LyricsTextSize = karaoke ? 29 : 21;

        if (focus)
        {
            result.Clock = ClockFace.Minimal;
            Add(WidgetKind.Clock, 260, 115, surface.Width * .82, surface.Height * .13, .88);
            Add(WidgetKind.Visualizer, 660, 94, surface.Width * .5, surface.Height * .94, .74);
        }
        else
        {
            double cover = karaoke ? 249 : 375;
            double caption = karaoke ? 218 : 170;
            double top = Math.Max(58, (surface.Height - cover - caption) * .47);
            double center = surface.Width * (karaoke ? .57 : .66);
            Add(WidgetKind.Visualizer, cover, cover, center, top + cover / 2, 1);
            Add(WidgetKind.Lyrics, karaoke ? 890 : 665, caption, center, top + cover + caption / 2, 1);
        }
        return result;

        void Add(WidgetKind kind, double width, double height, double x, double y, double opacity)
        {
            width = Math.Min(width, surface.Width);
            height = Math.Min(height, surface.Height);
            var options = result.Widget(kind);
            options.Enabled = true;
            options.Placement = Placement.Desktop;
            options.Surface = 0;
            options.Locked = true;
            options.Opacity = opacity;
            SceneLayout.Store(options, new Rect(x - width / 2, y - height / 2, width, height), surface);
        }
    }
}
