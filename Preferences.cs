using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sada;

public enum WidgetKind { Visualizer, Clock, Lyrics, Media, Environment, Wallpaper, AlbumBackdrop, ScreenGlow, Span, Fluid, Snow, CursorTrail, Waves, Listening, Calendar, Weather, System, Disks, Mood, Flock, Cassette, Disc, Neon }
public enum Placement { Desktop, Floating, OnTop }
public enum ClockFace { Digital, Stacked, Minimal, Analog, Words }
public enum CardLayout { Compact, Centered, Cover, TextOnly }
public enum LyricsStyle { Flow, Single, Karaoke, Subtitles, Panel, Marquee }
public enum Backdrop { Studio, Album, Image, Transparent }
public enum NeonColors { Sada, WarmWhite, Visualizer, Album, Custom }
public enum NeonIdle { ShowTube, Fade, Hide }

public sealed class NeonOptions
{
    public NeonLettering Lettering { get; set; }
    public NeonColors Colors { get; set; } = NeonColors.Album;
    public NeonReaction Reaction { get; set; }
    public NeonFlicker Flicker { get; set; }
    public NeonIdle Idle { get; set; }
    public bool Acrylic { get; set; }
    public bool ShowArtist { get; set; } = true;
    public bool ShowNote { get; set; } = true;
    public bool ClickToPlay { get; set; }
    public double ReactionStrength { get; set; } = 1;
    public string TitleColor { get; set; } = "#70F2D1";
    public string ArtistColor { get; set; } = "#B6A4FF";
}

public sealed class WidgetOptions
{
    public bool Enabled { get; set; }
    public Placement Placement { get; set; } = Placement.Desktop;
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 1;
    public double Surface { get; set; }
    public double Width { get; set; } = 390;
    public double Height { get; set; } = 230;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? RelativeLeft { get; set; }
    public double? RelativeTop { get; set; }
}

public sealed class SceneOptions
{
    public string Name { get; set; } = "My desktop";
    public bool LinkCoverAndLyrics { get; set; }
    public VisualSettings Visual { get; set; } = new() { Mode = VisualMode.Halo };
    public Dictionary<WidgetKind, WidgetOptions> Widgets { get; set; } = new();
    public ClockFace Clock { get; set; }
    public string ClockFont { get; set; } = "Segoe UI Variable Display";
    public bool Time24Hours { get; set; } = true;
    public bool Seconds { get; set; }
    public bool Date { get; set; } = true;
    public bool ClockGlow { get; set; }
    public bool BoldClock { get; set; }
    public int ClockTracking { get; set; }
    public string ClockColor { get; set; } = "#EDFFFA";
    public bool ClockGradient { get; set; }
    public CardLayout MediaLayout { get; set; }
    public bool TrackOnVisualizer { get; set; } = true;
    public bool RingCover { get; set; } = true;
    public bool AlbumGlow { get; set; }
    public bool MoodColor { get; set; }
    public LyricsStyle LyricsStyle { get; set; }
    public string LyricsPath { get; set; } = "";
    public string LyricsFileTrack { get; set; } = "";
    public bool AutoLyrics { get; set; } = true;
    public double LyricsTextSize { get; set; } = 21;
    public Backdrop FullBackdrop { get; set; } = Backdrop.Album;
    public string ImagePath { get; set; } = "";
    public bool FullMedia { get; set; } = true;
    public bool FullClock { get; set; }
    public bool FullLyrics { get; set; }
    public bool FullNeon { get; set; }
    public double? FullNeonX { get; set; }
    public double? FullNeonY { get; set; }
    public NeonOptions Neon { get; set; } = new();
    public int GlowStyle { get; set; }
    public double GlowStrength { get; set; } = .58;
    public bool AllMonitors { get; set; }
    public int EnvironmentStyle { get; set; }
    public string WeatherCity { get; set; } = "";
    public string MonitorName { get; set; } = "";

    public WidgetOptions Widget(WidgetKind kind)
    {
        if (Widgets.TryGetValue(kind, out var existing)) return existing;
        var options = kind switch
        {
            WidgetKind.Visualizer => new WidgetOptions { Width = 382, Height = 390 },
            WidgetKind.Lyrics => new WidgetOptions { Width = 500, Height = 175 },
            WidgetKind.Clock => new WidgetOptions { Width = 360, Height = 158 },
            WidgetKind.Media => new WidgetOptions { Width = 380, Height = 192 },
            WidgetKind.Neon => new WidgetOptions { Width = 570, Height = 168 },
            WidgetKind.CursorTrail or WidgetKind.ScreenGlow => new WidgetOptions { Locked = true, Placement = Placement.OnTop },
            WidgetKind.Snow or WidgetKind.Environment or WidgetKind.Wallpaper or WidgetKind.AlbumBackdrop or WidgetKind.Span
                => new WidgetOptions { Locked = true },
            _ => new WidgetOptions()
        };
        Widgets.Add(kind, options);
        return options;
    }
}

public sealed record SceneProfile(string Name, SceneOptions Scene, string? Process = null, string? StartTime = null);

public sealed class Preferences
{
    public SceneOptions Scene { get; set; } = new();
    public List<SceneProfile> Profiles { get; set; } = [];
    public string AudioId { get; set; } = "";
    public bool Microphone { get; set; }
    public int Fps { get; set; } = 60;
    public bool ReduceOnSilence { get; set; } = true;
    public bool PauseWhenCovered { get; set; } = true;
    public bool StartAtLogin { get; set; }
    public bool Hotkeys { get; set; } = true;
    public bool HiddenWidgets { get; set; }
    public int LyricsSetupVersion { get; set; }
    public bool AdaptiveLyricsTiming { get; set; } = true;
    public MediaSourcePreference PlaybackSource { get; set; }
    public Dictionary<string, YouTubeSongMatch> YouTubeMatches { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> SongLyricOffsets { get; set; } = new(StringComparer.Ordinal);

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sada");
    static string FilePath => Path.Combine(Folder, "settings.json");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, FilePath, true);
    }

    public static SceneOptions Clone(SceneOptions scene) =>
        JsonSerializer.Deserialize<SceneOptions>(JsonSerializer.Serialize(scene, Json), Json)!;
}

public sealed record YouTubeSongMatch(string Title, string Artist);
