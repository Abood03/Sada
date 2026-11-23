using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using Studio = Sada.StudioUi;

namespace Sada;

internal sealed class WidgetView : Grid
{
    private static readonly ConditionalWeakTable<byte[], BitmapSource> ArtworkCache = new();
    private static readonly Brush Glass = Studio.Brush("#DB132239");
    private static readonly Brush Deeper = Studio.Brush("#D60D172A");
    private static readonly Brush Line = Studio.Brush("#663D6280");
    private static readonly Brush Faded = Studio.Brush("#C6B7C8E2");
    private static readonly Brush Darken = Studio.Brush("#C50B1325");
    private static readonly string[] Weekdays = ["M", "T", "W", "T", "F", "S", "S"];

    private readonly App app;
    private readonly WidgetKind kind;
    private readonly bool fullscreen;
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private readonly ScaleTransform wallpaperScale = new(1, 1);
    private readonly ScaleTransform coverScale = new(1, 1);
    private Visualizer? visual;
    private Extras? extras;
    private GlowEffect? glow;
    private EnvironmentView? environment;
    private Grid? backdrop;
    private Image? atmosphereArt;
    private Border? fullCover;
    private Image? wallpaperImage;
    private MediaElement? wallpaperVideo;
    private MediaCard? media;
    private ClockPanel? clock;
    private LyricsPanel? lyrics;
    private StackPanel? dataPanel;
    private TextBlock? fileMessage;
    private string layoutId = "";
    private string wallpaperPath = "";
    private string dataVersion = "";
    private long lastText;
    private int? visualOverride;
    private int? paletteOverride;
    private AlbumPalette? coverPalette;
    private byte[]? sampledArtwork;

    public WidgetView(App app, WidgetKind kind, bool fullscreen)
    {
        this.app = app;
        this.kind = kind;
        this.fullscreen = fullscreen;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Apply();
    }

    public void Apply()
    {
        var scene = app.Scene;
        string key = $"{fullscreen}/{kind}/{scene.FullBackdrop}/{scene.FullMedia}/{scene.FullClock}/{scene.FullLyrics}/" +
            $"{scene.TrackOnVisualizer}/{scene.AlbumGlow}/{scene.MediaLayout}/{scene.Clock}/{scene.LyricsStyle}";
        if (key != layoutId)
        {
            layoutId = key;
            Build();
        }

        if (kind == WidgetKind.Wallpaper || fullscreen && scene.FullBackdrop == Backdrop.Image)
            ApplyWallpaper(scene.ImagePath);

        var currentArtwork = app.Media.Current.Artwork;
        UpdatePalette(currentArtwork);
        var artColors = SelectedCoverPalette(scene);
        var style = ResolveStyle(scene, artColors);
        if (visual is not null)
        {
            visual.Settings = style;
            visual.ArtworkColors = artColors;
            if (!fullscreen && kind == WidgetKind.Visualizer)
                visual.RingArtwork = scene.RingCover ? GetArtwork(currentArtwork) : null;
        }
        if (extras is not null) extras.Settings = style;
        if (environment is not null)
        {
            environment.Settings = style;
            environment.Style = scene.EnvironmentStyle;
        }
        if (glow is not null)
        {
            glow.Settings = style;
            glow.Strength = scene.GlowStrength;
            glow.Style = scene.GlowStyle;
        }
        clock?.Apply(scene, style);
        lastText = long.MinValue;
    }

    public void Release() => ReleaseWallpaper();

    public void ToggleVisual()
    {
        visualOverride = ((visualOverride ?? (int)app.Scene.Visual.Mode) + 1) % Enum.GetValues<VisualMode>().Length;
        Apply();
    }

    public void TogglePalette()
    {
        paletteOverride = ((paletteOverride ?? (int)app.Scene.Visual.Palette) + 1) % (Enum.GetValues<VisualPalette>().Length + 1);
        Apply();
    }

    public void Render(AudioFrame frame, Point pointer)
    {
        if (visual is not null) { visual.Frame = frame; visual.InvalidateVisual(); }
        if (extras is not null) { extras.Frame = frame; extras.Pointer = pointer; extras.InvalidateVisual(); }
        if (glow is not null) { glow.Frame = frame; glow.InvalidateVisual(); }
        if (environment is not null) { environment.Frame = frame; environment.InvalidateVisual(); }
        if (wallpaperImage is not null || wallpaperVideo is not null)
        {
            double scale = 1 + Math.Clamp(frame.Beat, 0, 1) * .028 + Math.Clamp(frame.Level, 0, 1) * .012;
            wallpaperScale.ScaleX = wallpaperScale.ScaleY = scale;
        }
        if (fullCover is not null)
            coverScale.ScaleX = coverScale.ScaleY = 1 + Math.Clamp(frame.Beat, 0, 1) * .055;
        long now = timer.ElapsedMilliseconds;
        if (lastText != long.MinValue && now - lastText < 135)
            return;
        lastText = now;

        MediaSnapshot song = app.Media.Current;
        BitmapSource? artwork = GetArtwork(song.Artwork);
        if (NeedsCoverPalette(app.Scene) && !ReferenceEquals(sampledArtwork, song.Artwork))
        {
            Apply();
            lastText = now;
        }
        if (atmosphereArt is not null && !ReferenceEquals(atmosphereArt.Source, artwork))
            atmosphereArt.Source = artwork;
        if (!fullscreen && kind == WidgetKind.Visualizer && visual is not null)
            visual.RingArtwork = app.Scene.RingCover ? artwork : null;
        if (fullCover is not null && !ReferenceEquals((fullCover.Background as ImageBrush)?.ImageSource, artwork))
        {
            fullCover.Background = artwork is null ? Brushes.Transparent : new ImageBrush(artwork) { Stretch = Stretch.UniformToFill };
            fullCover.Visibility = artwork is null ? Visibility.Hidden : Visibility.Visible;
        }
        media?.Update(song, artwork, now);
        clock?.Update(DateTime.Now);
        if (dataPanel is not null)
            UpdateData(song);
    }

    public void RenderLyrics()
    {
        if (lyrics is null) return;
        var position = app.LyricsPosition;
        var now = timer.ElapsedMilliseconds;
        lyrics.Update(app.Lyrics, position, now, app.LyricsMessage, app.Scene.LyricsTextSize);
    }

    private void Build()
    {
        ReleaseWallpaper();
        Children.Clear();
        visual = null;
        extras = null;
        glow = null;
        environment = null;
        atmosphereArt = null;
        fullCover = null;
        backdrop = null;
        media = null;
        clock = null;
        lyrics = null;
        dataPanel = null;
        dataVersion = "";
        fileMessage = null;

        if (fullscreen)
        {
            BuildFullscreen();
            return;
        }

        switch (kind)
        {
            case WidgetKind.Visualizer:
                BuildVisualizer();
                break;
            case WidgetKind.Clock:
                clock = new ClockPanel(app.Scene.Clock);
                Children.Add(clock);
                break;
            case WidgetKind.Lyrics:
                lyrics = new LyricsPanel(app.Scene.LyricsStyle);
                Children.Add(lyrics);
                break;
            case WidgetKind.Media:
            case WidgetKind.Cassette:
            case WidgetKind.Disc:
            case WidgetKind.Neon:
                media = new MediaCard(app, kind, app.Scene.MediaLayout, false);
                Children.Add(media);
                break;
            case WidgetKind.Span:
                visual = new Visualizer();
                Children.Add(visual);
                break;
            case WidgetKind.Environment:
                environment = new EnvironmentView();
                Children.Add(environment);
                break;
            case WidgetKind.Wallpaper:
                BuildWallpaperLayer();
                break;
            case WidgetKind.AlbumBackdrop:
                atmosphereArt = NewAmbientArt(.80);
                Children.Add(atmosphereArt);
                Children.Add(new Border { Background = Studio.Brush("#610B1325"), IsHitTestVisible = false });
                break;
            case WidgetKind.ScreenGlow:
                glow = new GlowEffect();
                if (IsLoaded) LoadTaskbarEdge();
                else glow.Loaded += (_, _) => LoadTaskbarEdge();
                Children.Add(glow);
                break;
            case WidgetKind.CursorTrail:
            case WidgetKind.Snow:
            case WidgetKind.Fluid:
            case WidgetKind.Waves:
            case WidgetKind.Flock:
                extras = new Extras { Kind = kind };
                Children.Add(extras);
                break;
            default:
                dataPanel = new StackPanel();
                var fit = new Viewbox { Child = dataPanel, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(13) };
                Children.Add(fit);
                break;
        }
    }

    private void BuildVisualizer()
    {
        var scene = app.Scene;
        if (scene.AlbumGlow)
        {
            atmosphereArt = NewAmbientArt(.65);
            Children.Add(atmosphereArt);
            Children.Add(new Border { Background = Studio.Brush("#550B1325"), IsHitTestVisible = false });
        }
        if (scene.TrackOnVisualizer)
        {
            var stage = new Grid();
            stage.RowDefinitions.Add(new RowDefinition());
            stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            visual = new Visualizer();
            stage.Children.Add(visual);
            media = new MediaCard(app, WidgetKind.Media, scene.MediaLayout, true);
            SetRow(media, 1);
            stage.Children.Add(media);
            Children.Add(stage);
        }
        else
        {
            visual = new Visualizer();
            Children.Add(visual);
        }
    }

    private void BuildFullscreen()
    {
        var scene = app.Scene;
        switch (scene.FullBackdrop)
        {
            case Backdrop.Album:
                Children.Add(new Border { Background = Studio.Night });
                atmosphereArt = NewAmbientArt(.76);
                Children.Add(atmosphereArt);
                Children.Add(new Border { Background = Darken, IsHitTestVisible = false });
                break;
            case Backdrop.Image:
                Children.Add(new Border { Background = Studio.Night });
                BuildWallpaperLayer();
                Children.Add(new Border { Background = Studio.Brush("#620B1325"), IsHitTestVisible = false });
                break;
            case Backdrop.Studio:
                Children.Add(new Border { Background = new RadialGradientBrush(Color.FromRgb(39, 55, 96), Color.FromRgb(11, 19, 37)) });
                break;
        }

        var stage = new Grid { Margin = new Thickness(30, 24, 30, 26) };
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stage.RowDefinitions.Add(new RowDefinition());
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new DockPanel();
        var exit = Studio.Button("Exit full screen  ⎋", app.ToggleFull);
        SetZIndex(exit, 3);
        DockPanel.SetDock(exit, Dock.Right);
        top.Children.Add(exit);
        if (scene.FullClock)
        {
            clock = new ClockPanel(scene.Clock) { Width = 245, Height = 85, HorizontalAlignment = HorizontalAlignment.Left };
            top.Children.Add(clock);
        }
        stage.Children.Add(top);

        var canvas = new Grid { ClipToBounds = true };
        visual = new Visualizer { MaxWidth = 1580 };
        canvas.Children.Add(visual);
        if (scene.FullBackdrop == Backdrop.Album)
        {
            var tile = new Viewbox { MaxWidth = 215, MaxHeight = 215, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false };
            fullCover = new Border { Width = 210, Height = 210, CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1), BorderBrush = Line, Visibility = Visibility.Hidden,
                RenderTransform = coverScale, RenderTransformOrigin = new Point(.5, .5),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 32, ShadowDepth = 6, Opacity = .55 } };
            tile.Child = fullCover;
            canvas.Children.Add(tile);
        }
        SetRow(canvas, 1);
        stage.Children.Add(canvas);
        if (scene.FullLyrics)
        {
            lyrics = new LyricsPanel(scene.LyricsStyle, useBackdrop: true) { MaxWidth = 850, Height = 110 };
            SetRow(lyrics, 2);
            stage.Children.Add(lyrics);
        }
        if (scene.FullMedia)
        {
            media = new MediaCard(app, WidgetKind.Media, CardLayout.Compact, false) { MaxWidth = 570, MaxHeight = 125 };
            SetRow(media, 3);
            stage.Children.Add(media);
        }
        var hint = Studio.Text("V  Visualizer    C  Colors    ← / →  Track    Space  Play", 11, Studio.Soft);
        hint.VerticalAlignment = VerticalAlignment.Bottom;
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.Margin = new Thickness(15);
        hint.IsHitTestVisible = false;
        Children.Add(stage);
        Children.Add(hint);
    }

    private void BuildWallpaperLayer()
    {
        backdrop = new Grid { IsHitTestVisible = false, ClipToBounds = true };
        fileMessage = Studio.Text("Choose a wallpaper in Studio.", 15, Studio.Soft);
        fileMessage.HorizontalAlignment = HorizontalAlignment.Center;
        fileMessage.VerticalAlignment = VerticalAlignment.Center;
        backdrop.Children.Add(fileMessage);
        Children.Add(backdrop);
    }

    private void ApplyWallpaper(string path)
    {
        if (backdrop is null || path == wallpaperPath) return;
        ReleaseWallpaper();
        wallpaperPath = path;
        ShowWallpaperMessage("Choose a wallpaper in Studio.");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException();
            var uri = new Uri(Path.GetFullPath(path));
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".mp4" or ".wmv" or ".m4v" or ".mov" or ".avi" or ".webm" or ".mkv")
            {
                wallpaperVideo = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
                    Volume = 0, IsMuted = true, Stretch = Stretch.UniformToFill, RenderTransform = wallpaperScale,
                    RenderTransformOrigin = new Point(.5, .5), Source = uri };
                MediaElement mediaFile = wallpaperVideo;
                wallpaperVideo.MediaEnded += (_, _) => { mediaFile.Position = TimeSpan.FromMilliseconds(1); mediaFile.Play(); };
                wallpaperVideo.MediaOpened += (_, _) => { if (fileMessage is not null) fileMessage.Visibility = Visibility.Collapsed; };
                wallpaperVideo.MediaFailed += (_, _) => ShowWallpaperMessage("Windows could not play this wallpaper video.");
                backdrop.Children.Insert(0, mediaFile);
                mediaFile.Play();
            }
            else
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 2560;
                bitmap.EndInit();
                bitmap.Freeze();
                wallpaperImage = new Image { Source = bitmap, Stretch = Stretch.UniformToFill,
                    RenderTransform = wallpaperScale, RenderTransformOrigin = new Point(.5, .5) };
                backdrop.Children.Insert(0, wallpaperImage);
                if (fileMessage is not null) fileMessage.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            ShowWallpaperMessage("This wallpaper file could not be opened.");
        }
    }

    private void ShowWallpaperMessage(string message)
    {
        if (fileMessage is null) return;
        fileMessage.Text = message;
        fileMessage.Visibility = Visibility.Visible;
    }

    private void ReleaseWallpaper()
    {
        if (wallpaperVideo is { } movie)
        {
            movie.Close();
            movie.Source = null;
            backdrop?.Children.Remove(movie);
            wallpaperVideo = null;
        }
        if (wallpaperImage is { } image)
        {
            backdrop?.Children.Remove(image);
            wallpaperImage = null;
        }
        wallpaperPath = "";
    }

    private void LoadTaskbarEdge()
    {
        var origin = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)origin.X, (int)origin.Y));
        var full = screen.Bounds;
        var work = screen.WorkingArea;
        if (glow is not null)
            glow.TaskbarEdge = work.Top > full.Top ? GlowEdge.Top : work.Left > full.Left ? GlowEdge.Left :
                work.Right < full.Right ? GlowEdge.Right : GlowEdge.Bottom;
    }

    private static Image NewAmbientArt(double opacity) => new()
    {
        Stretch = Stretch.UniformToFill,
        Opacity = opacity,
        IsHitTestVisible = false,
        Effect = new BlurEffect { Radius = 37, RenderingBias = RenderingBias.Performance }
    };

    private static BitmapSource? GetArtwork(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            return ArtworkCache.GetValue(bytes, data =>
            {
                using var stream = new MemoryStream(data);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 512;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            });
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private bool NeedsCoverPalette(SceneOptions scene) =>
        scene.MoodColor || kind == WidgetKind.Mood || paletteOverride == Enum.GetValues<VisualPalette>().Length;

    private void UpdatePalette(byte[]? artwork)
    {
        if (!NeedsCoverPalette(app.Scene) || ReferenceEquals(sampledArtwork, artwork)) return;
        sampledArtwork = artwork;
        coverPalette = AlbumPalette.From(GetArtwork(artwork));
    }

    private AlbumPalette? SelectedCoverPalette(SceneOptions scene) =>
        (scene.MoodColor && paletteOverride is null || paletteOverride == Enum.GetValues<VisualPalette>().Length) ? coverPalette : null;

    private VisualSettings ResolveStyle(SceneOptions scene, AlbumPalette? artwork)
    {
        if (visualOverride is null && paletteOverride is null && artwork is null) return scene.Visual;
        var original = scene.Visual;
        return new VisualSettings
        {
            Mode = (VisualMode)(visualOverride ?? (int)original.Mode), Direction = original.Direction,
            Palette = artwork is not null ? VisualPalette.Custom : paletteOverride is { } value && Enum.IsDefined((VisualPalette)value) ? (VisualPalette)value : original.Palette,
            CustomColor = artwork is not null ? AlbumPalette.RgbHex(artwork.First) : original.CustomColor,
            CustomAccent = artwork is not null ? AlbumPalette.RgbHex(artwork.Last) : original.CustomAccent,
            Count = original.Count, Thickness = original.Thickness, Sensitivity = original.Sensitivity,
            Smoothing = original.Smoothing, Opacity = original.Opacity, Speed = original.Speed,
            Glow = original.Glow, Mirror = original.Mirror, Atmosphere = original.Atmosphere,
            AtmosphereIntensity = original.AtmosphereIntensity
        };
    }

    private void UpdateData(MediaSnapshot song)
    {
        if (dataPanel is null) return;
        switch (kind)
        {
            case WidgetKind.System:
                var system = app.Data.System;
                SetData("System", string.Join("|", system.CpuPercent, system.GpuPercent, system.MemoryPercent, system.DownloadBytesPerSecond, system.UploadBytesPerSecond), () =>
                {
                    AddGauge("Processor", system.CpuPercent, Studio.Mint);
                    AddGauge("Graphics", system.GpuPercent, Studio.Violet);
                    AddGauge("Memory", system.MemoryPercent, Studio.Mint);
                    AddCaption($"↓ {Bytes(system.DownloadBytesPerSecond)}/s     ↑ {Bytes(system.UploadBytesPerSecond)}/s");
                });
                break;
            case WidgetKind.Disks:
                var disk = app.Data.System;
                SetData("Storage", $"{disk.DiskName}/{disk.DiskTotal}/{disk.DiskFree}", () =>
                {
                    AddGauge(disk.DiskName.Length > 0 ? disk.DiskName : "System drive", disk.DiskTotal > 0 ? 100d * (disk.DiskTotal - disk.DiskFree) / disk.DiskTotal : null, Studio.Mint);
                    AddCaption($"{Bytes(disk.DiskFree)} free of {Bytes(disk.DiskTotal)}");
                });
                break;
            case WidgetKind.Listening:
                var stats = app.Data.Listening;
                SetData("Listening", $"{stats.Today.TotalSeconds:F0}/{stats.Total.TotalSeconds:F0}/{stats.TracksToday}/{stats.TopArtist}", () =>
                {
                    AddValue($"{Duration(stats.Today)} today");
                    AddCaption($"{stats.TracksToday} tracks today · {stats.UniqueTracks} unique tracks");
                    AddCaption($"{Duration(stats.Total)} total{(stats.TopArtist.Length > 0 ? " · " + stats.TopArtist : "")}");
                });
                break;
            case WidgetKind.Calendar:
                var date = app.Data.Calendar;
                SetData("Calendar", $"{date.Year}/{date.Month}/{date.Today}", () => AddMonth(date));
                break;
            case WidgetKind.Weather:
                var weather = app.Data.Weather;
                SetData("Weather", weather?.ToString() ?? "nil", () =>
                {
                    if (weather is null) { AddCaption("Look up a city in Studio."); return; }
                    AddCaption(weather.Place);
                    AddValue($"{weather.Temperature:F0}°C  ·  {weather.Summary}");
                    AddCaption($"Feels {weather.FeelsLike:F0}°C  ·  Wind {weather.WindSpeed:F0} km/h");
                    AddCaption("Weather by Open-Meteo");
                });
                break;
            case WidgetKind.Mood:
                SetData("Album palette", $"{song.Title}/{coverPalette}", () =>
                {
                    AddCaption(song.HasTrack ? song.Title : "Play music with cover art.");
                    var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                    foreach (Color color in new[] { coverPalette?.First ?? Studio.Mint.Color, coverPalette?.Middle ?? Studio.Violet.Color, coverPalette?.Last ?? Studio.Night.Color })
                        swatches.Children.Add(new Border { Width = 58, Height = 31, CornerRadius = new CornerRadius(5),
                            Margin = new Thickness(0, 0, 5, 0), Background = new SolidColorBrush(color), ToolTip = color.ToString() });
                    dataPanel.Children.Add(swatches);
                });
                break;
        }
    }

    private void SetData(string title, string version, Action fill)
    {
        if (dataPanel is null || version == dataVersion) return;
        dataVersion = version;
        dataPanel.Children.Clear();
        dataPanel.Children.Add(Studio.Label(title));
        fill();
    }

    private void AddValue(string value) => dataPanel?.Children.Add(Studio.Text(value, 22, Studio.Paper, true));
    private void AddCaption(string value) => dataPanel?.Children.Add(Studio.Text(value, 13, Studio.Soft));

    private void AddGauge(string label, double? value, Brush brush)
    {
        if (dataPanel is null) return;
        var row = new DockPanel { Width = 180, Margin = new Thickness(0, 4, 0, 0) };
        var measurement = Studio.Text(value is { } percent ? $"{percent:F0}%" : "—", 12, Studio.Paper, true);
        DockPanel.SetDock(measurement, Dock.Right);
        row.Children.Add(measurement);
        row.Children.Add(Studio.Text(label, 12, Studio.Soft));
        dataPanel.Children.Add(row);
        var track = new Grid { Height = 4, Width = 180, Background = Studio.Field, HorizontalAlignment = HorizontalAlignment.Left };
        track.Children.Add(new Border { Background = brush, Width = 1.8 * Math.Clamp(value ?? 0, 0, 100), HorizontalAlignment = HorizontalAlignment.Left });
        dataPanel.Children.Add(track);
    }

    private void AddMonth(CalendarReading date)
    {
        if (dataPanel is null) return;
        AddValue(date.MonthName);
        var days = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Width = 220, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var title in Weekdays)
            days.Children.Add(new Border { Child = Cell(title, Studio.Soft, 11) });
        for (int index = 0; index < date.StartingWeekday; index++) days.Children.Add(new Border());
        for (int day = 1; day <= date.DaysInMonth; day++)
        {
            bool today = day == date.Today;
            days.Children.Add(new Border { Background = today ? Studio.Mint : Brushes.Transparent, CornerRadius = new CornerRadius(4),
                Child = Cell(day.ToString(CultureInfo.CurrentCulture), today ? Studio.Ink : Studio.Paper, 12) });
        }
        dataPanel.Children.Add(days);
    }

    private static TextBlock Cell(string text, Brush ink, double size)
    {
        var result = Studio.Text(text, size, ink, true);
        result.TextAlignment = TextAlignment.Center;
        result.Padding = new Thickness(3);
        return result;
    }

    private static string Duration(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes:00}m" : $"{time.Minutes}m {time.Seconds:00}s";

    private static string Bytes(double bytes)
    {
        if (bytes >= 1_099_511_627_776d) return $"{bytes / 1_099_511_627_776d:F1} TiB";
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824:F1} GiB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576:F1} MiB";
        if (bytes >= 1024) return $"{bytes / 1024:F1} KiB";
        return $"{bytes:F0} B";
    }

    private sealed class MediaCard : Grid
    {
        private readonly TextBlock title;
        private readonly TextBlock artist;
        private readonly TextBlock? clock;
        private readonly Button? play;
        private readonly Slider? seek;
        private readonly Border? cover;
        private readonly Ellipse? disc;
        private readonly RotateTransform discAngle = new();
        private BitmapSource? previousArt;
        private bool dragging;
        private long lastMotion;

        public MediaCard(App app, WidgetKind kind, CardLayout layout, bool compact)
        {
            Margin = new Thickness(8);
            bool centered = kind == WidgetKind.Media && layout is CardLayout.Centered or CardLayout.Cover;
            bool useArt = kind != WidgetKind.Media || layout != CardLayout.TextOnly;
            bool decorative = kind is WidgetKind.Cassette or WidgetKind.Disc or WidgetKind.Neon;
            var deck = new StackPanel { Width = centered && !compact ? 235 : 264 };
            var shell = new Border
            {
                CornerRadius = new CornerRadius(kind == WidgetKind.Cassette ? 7 : 13),
                Padding = new Thickness(compact ? 7 : 10),
                BorderThickness = new Thickness(1),
                Background = kind == WidgetKind.Cassette ? Deeper : Glass,
                BorderBrush = kind == WidgetKind.Neon ? Studio.Violet : Line,
                Child = deck
            };
            if (kind == WidgetKind.Neon)
                shell.Effect = new DropShadowEffect { Color = Studio.Violet.Color, BlurRadius = 20, ShadowDepth = 0, Opacity = .65 };

            var contents = new Grid();
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            string header = kind switch { WidgetKind.Disc => "DISC PLAYER", WidgetKind.Cassette => "SIDE A", WidgetKind.Neon => "ON AIR", _ => "NOW PLAYING" };
            var tag = Studio.Text(header, 9, kind == WidgetKind.Neon ? Studio.Violet : Studio.Mint, true);
            title = OneLine("Waiting for playback", compact ? 14 : 17, Studio.Paper, true);
            artist = OneLine("", 12, Studio.Soft);
            info.Children.Add(tag);
            info.Children.Add(title);
            info.Children.Add(artist);
            if (centered)
            {
                tag.TextAlignment = title.TextAlignment = artist.TextAlignment = TextAlignment.Center;
                contents.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                contents.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                SetRow(info, 1);
            }
            else if (useArt)
            {
                contents.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                contents.ColumnDefinitions.Add(new ColumnDefinition());
                SetColumn(info, 1);
            }
            contents.Children.Add(info);
            if (useArt)
            {
                double size = compact ? 36 : centered && layout == CardLayout.Cover ? 86 : decorative ? 59 : 48;
                cover = new Border
                {
                    Width = size, Height = size, Background = Studio.Field,
                    CornerRadius = new CornerRadius(kind == WidgetKind.Cassette ? 5 : 8),
                    Margin = centered ? new Thickness(0, 0, 0, 7) : new Thickness(0, 0, 10, 0),
                    HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (kind == WidgetKind.Disc)
                {
                    var rings = new Grid { RenderTransform = discAngle, RenderTransformOrigin = new Point(.5, .5) };
                    disc = new Ellipse { Fill = Studio.Night, Stroke = Studio.Violet, StrokeThickness = 1 };
                    rings.Children.Add(disc);
                    rings.Children.Add(new Ellipse { Stroke = Studio.Soft, Opacity = .6, Margin = new Thickness(8) });
                    rings.Children.Add(new Ellipse { Stroke = Studio.Soft, Opacity = .55, Margin = new Thickness(15) });
                    rings.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Studio.Night });
                    cover.Background = Brushes.Transparent;
                    cover.Child = rings;
                }
                contents.Children.Add(cover);
            }
            deck.Children.Add(contents);

            if (!compact)
            {
                if (kind == WidgetKind.Cassette)
                {
                    var tape = new Grid { Height = 9, Margin = new Thickness(5, 6, 5, 1) };
                    tape.Children.Add(new Border { Background = Studio.Line, Height = 2, VerticalAlignment = VerticalAlignment.Center });
                    tape.Children.Add(new Ellipse { Fill = Studio.Mint, Width = 8, HorizontalAlignment = HorizontalAlignment.Left });
                    tape.Children.Add(new Ellipse { Fill = Studio.Violet, Width = 8, HorizontalAlignment = HorizontalAlignment.Right });
                    deck.Children.Add(tape);
                }
                seek = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, Margin = new Thickness(0, 7, 0, 1),
                    Height = 17, ToolTip = "Seek" };
                seek.PreviewMouseLeftButtonDown += (_, _) => dragging = true;
                seek.PreviewMouseLeftButtonUp += async (_, _) => { dragging = false; await app.Media.SeekAsync(TimeSpan.FromSeconds(seek.Value)); };
                seek.LostMouseCapture += (_, _) => dragging = false;
                seek.KeyUp += async (_, args) =>
                {
                    if (args.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
                        await app.Media.SeekAsync(TimeSpan.FromSeconds(seek.Value));
                };
                deck.Children.Add(seek);
                var footer = new DockPanel();
                clock = OneLine("", 11, Studio.Soft);
                clock.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(clock, Dock.Right);
                footer.Children.Add(clock);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(Control("⏮", "Previous track", app.Media.PreviousAsync));
                play = Control("▶", "Play or pause", app.Media.ToggleAsync);
                buttons.Children.Add(play);
                buttons.Children.Add(Control("⏭", "Next track", app.Media.NextAsync));
                footer.Children.Add(buttons);
                deck.Children.Add(footer);
            }
            Children.Add(new Viewbox { Child = shell, StretchDirection = StretchDirection.DownOnly, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }

        public void Update(MediaSnapshot song, BitmapSource? artwork, long now)
        {
            Put(title, song.HasTrack ? song.Title : "Waiting for playback");
            Put(artist, song.Artist.Length > 0 ? song.Artist : song.HasTrack ? song.Album : "Play music in a Windows media app.");
            if (!ReferenceEquals(previousArt, artwork))
            {
                previousArt = artwork;
                Brush fill = artwork is null ? Studio.Field : new ImageBrush(artwork) { Stretch = Stretch.UniformToFill };
                if (disc is not null) disc.Fill = fill;
                else if (cover is not null) cover.Background = fill;
            }
            if (disc is not null && song.IsPlaying && lastMotion > 0)
                discAngle.Angle = (discAngle.Angle + Math.Min(now - lastMotion, 150) * .019) % 360;
            lastMotion = now;
            if (play is not null)
            {
                play.Content = song.IsPlaying ? "Ⅱ" : "▶";
                play.IsEnabled = song.HasTrack;
            }
            if (seek is not null && !dragging)
            {
                seek.IsEnabled = song.Duration.TotalSeconds > 0;
                seek.Maximum = Math.Max(1, song.Duration.TotalSeconds);
                seek.Value = Math.Clamp(song.Position.TotalSeconds, 0, seek.Maximum);
            }
            if (clock is not null)
                Put(clock, song.Duration > TimeSpan.Zero ? $"{MediaTime(song.Position)}  /  {MediaTime(song.Duration)}" : "");
        }

        private static Button Control(string symbol, string description, Func<Task<bool>> command)
        {
            var button = Studio.AsyncButton(symbol, async () => { await command(); });
            button.ToolTip = description;
            System.Windows.Automation.AutomationProperties.SetName(button, description);
            button.Margin = new Thickness(0, 0, 4, 0);
            button.Padding = new Thickness(6, 0, 6, 0);
            button.MinHeight = 23;
            button.MinWidth = 35;
            return button;
        }

        private static string MediaTime(TimeSpan span) => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private sealed class ClockPanel : Grid
    {
        private static readonly string[] SmallNumbers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty"];
        private readonly ClockFace face;
        private readonly TextBlock primary;
        private readonly TextBlock secondary;
        private readonly TextBlock detail;
        private readonly AnalogClock? analog;
        private SceneOptions? scene;
        private string? appearance;

        public ClockPanel(ClockFace face)
        {
            this.face = face;
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            primary = ClockText(face == ClockFace.Words ? 31 : face == ClockFace.Minimal ? 48 : 67);
            secondary = ClockText(54);
            detail = ClockText(15);
            if (face == ClockFace.Analog)
            {
                analog = new AnalogClock { Height = 140, Width = 140 };
                panel.Children.Add(analog);
            }
            else panel.Children.Add(primary);
            if (face == ClockFace.Stacked) panel.Children.Add(secondary);
            panel.Children.Add(detail);
            var scaled = new Viewbox { Child = panel, Stretch = Stretch.Uniform, MaxWidth = 520, MaxHeight = 400,
                VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(10) };
            Children.Add(scaled);
        }

        public void Apply(SceneOptions options, VisualSettings style)
        {
            scene = options;
            var tone = Studio.ValidColor(options.ClockColor, Studio.Paper);
            var (_, _, to) = Visualizer.ResolvePalette(style);
            string newAppearance = $"{tone.Color}/{to}/{options.ClockGradient}/{options.ClockFont}/{options.BoldClock}/{options.ClockGlow}";
            if (appearance == newAppearance) return;
            appearance = newAppearance;
            Brush ink = options.ClockGradient ? new LinearGradientBrush(tone.Color, to, 35d) : tone;
            var font = new FontFamily(string.IsNullOrWhiteSpace(options.ClockFont) ? "Segoe UI Variable Display" : options.ClockFont);
            foreach (var line in new[] { primary, secondary, detail })
            {
                line.Foreground = ink;
                line.FontFamily = font;
                line.FontWeight = options.BoldClock ? FontWeights.Bold : FontWeights.Medium;
            }
            if (analog is not null) { analog.Ink = ink; analog.InvalidateVisual(); }
            Effect = options.ClockGlow ? new DropShadowEffect { Color = tone.Color, ShadowDepth = 0, BlurRadius = 19, Opacity = .64 } : null;
        }

        public void Update(DateTime now)
        {
            if (scene is not { } options) return;
            string hour = now.ToString(options.Time24Hours ? "HH" : "h", CultureInfo.CurrentCulture);
            string minute = now.ToString("mm", CultureInfo.CurrentCulture);
            string seconds = options.Seconds ? ":" + now.ToString("ss", CultureInfo.CurrentCulture) : "";
            string period = options.Time24Hours ? "" : now.ToString("tt", CultureInfo.CurrentCulture);
            switch (face)
            {
                case ClockFace.Analog:
                    if (analog is not null) analog.Update(now, options.Seconds);
                    break;
                case ClockFace.Stacked:
                    Put(primary, Tracking(hour, options.ClockTracking));
                    Put(secondary, Tracking(minute + seconds, options.ClockTracking));
                    break;
                case ClockFace.Words:
                    int shownHour = options.Time24Hours ? now.Hour : (now.Hour + 11) % 12 + 1;
                    Put(primary, Number(shownHour) + (now.Minute == 0 && !options.Seconds ? " o’clock" : "\n" + Number(now.Minute)) +
                        (options.Seconds ? " · " + Number(now.Second) : ""));
                    break;
                default:
                    Put(primary, Tracking(hour + (face == ClockFace.Minimal ? " " : ":") + minute + seconds, options.ClockTracking));
                    break;
            }
            string date = options.Date ? now.ToString(face == ClockFace.Minimal ? "ddd, MMM d" : "dddd, MMMM d", CultureInfo.CurrentCulture) : "";
            string caption = period.Length > 0 && date.Length > 0 ? period + "  ·  " + date : period + date;
            Put(detail, caption);
            detail.Visibility = caption.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static TextBlock ClockText(double size)
        {
            var text = OneLine("", size, Studio.Paper, true);
            text.TextAlignment = TextAlignment.Center;
            text.TextTrimming = TextTrimming.None;
            text.LineHeight = size * 1.05;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            return text;
        }

        private static string Tracking(string value, int spacing) => spacing <= 0 ? value : string.Join(new string(' ', Math.Clamp(spacing, 1, 6)), value.ToCharArray());

        private static string Number(int value) => value < 20 ? SmallNumbers[value] :
            Tens[value / 10] + (value % 10 == 0 ? "" : "-" + SmallNumbers[value % 10]);
    }

    private sealed class AnalogClock : FrameworkElement
    {
        public Brush Ink { get; set; } = Studio.Paper;
        private DateTime time;
        private bool seconds;

        public void Update(DateTime clock, bool showSeconds)
        {
            bool changed = time.Minute != clock.Minute || showSeconds && time.Second != clock.Second || seconds != showSeconds || time.Hour != clock.Hour;
            time = clock;
            seconds = showSeconds;
            if (changed) InvalidateVisual();
        }

        protected override void OnRender(DrawingContext context)
        {
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 2);
            context.DrawEllipse(null, new Pen(Ink, 1.5), center, radius, radius);
            for (int tick = 0; tick < 12; tick++)
                context.DrawLine(new Pen(Ink, tick % 3 == 0 ? 2 : 1), PointAt(center, tick / 12d, radius * .81), PointAt(center, tick / 12d, radius * .915));
            context.DrawLine(new Pen(Ink, 4), center, PointAt(center, (time.Hour % 12 + time.Minute / 60d) / 12, radius * .56));
            context.DrawLine(new Pen(Ink, 2.5), center, PointAt(center, (time.Minute + time.Second / 60d) / 60, radius * .78));
            if (seconds) context.DrawLine(new Pen(Studio.Mint, 1), center, PointAt(center, time.Second / 60d, radius * .83));
            context.DrawEllipse(Ink, null, center, 3.5, 3.5);
        }

        private static Point PointAt(Point origin, double fraction, double radius)
        {
            double angle = fraction * Math.PI * 2 - Math.PI / 2;
            return new Point(origin.X + radius * Math.Cos(angle), origin.Y + radius * Math.Sin(angle));
        }
    }

    private sealed class LyricsPanel : Grid
    {
        private readonly LyricsStyle style;
        private readonly TextBlock[] rows = [];
        private readonly LyricText[] textRows = [];
        private readonly StackPanel? lines;
        private readonly LyricFlow? flow;
        private readonly ScrollViewer? plainView;
        private readonly TextBlock? plainText;
        private readonly TranslateTransform lift = new();
        private readonly TranslateTransform marqueeOffset = new();
        private LyricsTrack? previousTrack;
        private int previousIndex = int.MinValue;
        private long lineChanged;
        private long lastTick;
        private TimeSpan lastPosition;
        private string previousMessage = "";

        public LyricsPanel(LyricsStyle style, bool useBackdrop = false)
        {
            this.style = style;
            if (style == LyricsStyle.Flow)
            {
                flow = new LyricFlow();
                Children.Add(flow);
                return;
            }
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            lines = panel;
            panel.RenderTransform = lift;
            int count = style switch { LyricsStyle.Karaoke => 3, LyricsStyle.Subtitles => 2, LyricsStyle.Panel => 5, _ => 1 };
            rows = new TextBlock[count];
            textRows = new LyricText[count];
            int center = count == 2 ? 0 : count / 2;
            for (int index = 0; index < count; index++)
            {
                bool current = index == center;
                var line = OneLine("", current ? style == LyricsStyle.Subtitles ? 20 : 23 : 14, current ? Studio.Paper : Faded, current);
                line.TextAlignment = style == LyricsStyle.Panel ? TextAlignment.Left : TextAlignment.Center;
                line.Margin = new Thickness(0, 2, 0, 2);
                panel.Children.Add(line);
                rows[index] = line;
                textRows[index] = new LyricText(line);
            }
            if (style == LyricsStyle.Marquee)
            {
                var belt = new Canvas { ClipToBounds = true, Height = 45, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Remove(rows[0]);
                rows[0].TextWrapping = TextWrapping.NoWrap;
                rows[0].RenderTransform = marqueeOffset;
                belt.Children.Add(rows[0]);
                panel.Children.Add(belt);
            }
            var inset = new Border { Child = panel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8),
                Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(10),
                Background = useBackdrop && (style is LyricsStyle.Subtitles or LyricsStyle.Panel) ? Glass : Brushes.Transparent };
            if (style == LyricsStyle.Panel)
            {
                plainText = Studio.Text("", 14, Studio.Paper);
                plainView = new ScrollViewer { Content = plainText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Visibility = Visibility.Collapsed, Padding = new Thickness(3) };
                var content = new Grid();
                inset.Child = content;
                content.Children.Add(panel);
                content.Children.Add(plainView);
                inset.BorderThickness = new Thickness(useBackdrop ? 1 : 0);
                inset.BorderBrush = Line;
                inset.VerticalAlignment = VerticalAlignment.Stretch;
            }
            Children.Add(inset);
        }

        public void Update(LyricsTrack? track, TimeSpan position, long now, string status, double textSize)
        {
            textSize = Math.Clamp(textSize, 12, 34);
            if (flow is not null)
            {
                flow.Render(track, position, now, status, textSize);
                return;
            }
            int index = track?.IndexAt(position) ?? -1;
            int center = rows.Length == 2 ? 0 : rows.Length / 2;
            double delta = (position - lastPosition).TotalMilliseconds;
            bool seek = delta < -80 || delta > Math.Max(0, now - lastTick) + 300;
            lastPosition = position;
            lastTick = now;
            if (track != previousTrack || index != previousIndex || track is null && status != previousMessage)
            {
                var step = track is { Lines.Count: > 0 } && ReferenceEquals(track, previousTrack) && index == previousIndex + 1 && !seek;
                previousTrack = track;
                previousIndex = index;
                previousMessage = status;
                lineChanged = now;
                AnimateLine(step);
                bool showPlain = track is { Lines.Count: 0 } && track.PlainText.Length > 0;
                if (plainView is not null && plainText is not null)
                {
                    plainView.Visibility = showPlain ? Visibility.Visible : Visibility.Collapsed;
                    if (lines is not null) lines.Visibility = showPlain ? Visibility.Collapsed : Visibility.Visible;
                    SetLine(plainText, showPlain ? track!.PlainText : "");
                }
                if (track is null || track.Lines.Count == 0)
                {
                    foreach (var row in textRows) row.SetPlain("");
                    string[] plainLines = track?.PlainText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                    if (style == LyricsStyle.Marquee && showPlain) textRows[center].SetPlain(string.Join("   ·   ", plainLines));
                    else if (showPlain)
                    {
                        for (int row = center; row < rows.Length && row - center < plainLines.Length; row++)
                            textRows[row].SetPlain(plainLines[row - center]);
                    }
                    else textRows[center].SetPlain(status);
                }
                else
                {
                    for (int row = 0; row < rows.Length; row++)
                    {
                        int line = index - center + row;
                        textRows[row].SetTimed(track, line);
                    }
                }
            }
            var height = Math.Max(12, ActualHeight - 36);
            for (var row = 0; row < textRows.Length; row++)
            {
                bool active = row == center;
                double rowHeight = textRows.Length == 1 ? height : active ? height * .60 : height * .40 / (textRows.Length - 1);
                if (style == LyricsStyle.Marquee)
                {
                    rows[0].FontSize = textSize;
                    double room = Math.Max(0, ActualWidth - 42);
                    double overflow = rows[0].ActualWidth - room;
                    marqueeOffset.X = overflow <= 0 ? -overflow / 2 :
                        -Math.Min(overflow, Math.Max(0, now - lineChanged - 950) * .055);
                }
                else textRows[row].Fit(ActualWidth - 40, rowHeight, active ? textSize : Math.Max(10, textSize * .72));
                textRows[row].HighlightAt(position, currentLine: active);
            }
        }

        private static void SetLine(TextBlock element, string text)
        {
            if (element.Text == text) return;
            element.Text = text;
            element.FlowDirection = LyricFlow.DirectionFor(text);
        }

        private void AnimateLine(bool step)
        {
            if (lines is null) return;
            if (!step)
            {
                lines.BeginAnimation(OpacityProperty, null);
                lift.BeginAnimation(TranslateTransform.YProperty, null);
                return;
            }
            var duration = TimeSpan.FromMilliseconds(180);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            lines.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }

    }

    private static TextBlock OneLine(string text, double size, Brush brush, bool bold = false)
    {
        var result = Studio.Text(text, size, brush, bold);
        result.TextWrapping = TextWrapping.NoWrap;
        result.TextTrimming = TextTrimming.CharacterEllipsis;
        return result;
    }

    private static void Put(TextBlock element, string text)
    {
        if (element.Text != text) element.Text = text;
    }
}
