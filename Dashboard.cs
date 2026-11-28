using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using static Sada.StudioUi;

namespace Sada;

internal sealed class Dashboard : Window
{
    const string DeveloperProfile = "https://github.com/Abood03";

    readonly App app;
    SceneOptions Scene => app.Scene;
    readonly ContentControl canvas = new();
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel nav = new();
    readonly TextBlock status = Text("", 12, Soft);
    readonly TextBlock songTitle = Text("Waiting for playback", 14, Paper, true);
    readonly TextBlock songArtist = Text("Windows media", 12, Soft);
    readonly TextBlock playback = Text("▶", 14, Paper, true);
    readonly Image artwork = new() { Width = 42, Height = 42, Stretch = Stretch.UniformToFill };
    readonly TextBlock count = Text("", 12, Mint, true);
    readonly TextBlock level = Text("", 12, Mint);
    readonly TextBlock signalType = Label("WINDOWS PLAYBACK");
    readonly ProgressBar signal = new() { Minimum = 0, Maximum = 1, Height = 5, Foreground = Mint, Background = Field };
    readonly Button toggle;
    byte[]? coverBytes;
    string page = "Scenes";
    WidgetKind selectedWidget = WidgetKind.Visualizer;
    Visualizer? preview;
    WidgetView? widgetPreview;
    ScenePreview? scenePreview;
    TextBlock? previewLabel;
    TextBlock? lyricsStatus;
    TextBlock? songTimingStatus;
    Button[] songTimingButtons = [];
    long lastDisplay;
    long lastPreviewFrame;

    internal bool WantsSystemReadings =>
        widgetPreview is not null && (selectedWidget is WidgetKind.System or WidgetKind.Disks) ||
        scenePreview is not null && (Scene.Widget(WidgetKind.System).Enabled || Scene.Widget(WidgetKind.Disks).Enabled);

    internal Dashboard(App app)
    {
        this.app = app;
        Title = "Sada — Desktop audio studio";
        Width = 1190;
        Height = 805;
        MinWidth = 790;
        MinHeight = 590;
        Background = Night;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        if (Environment.ProcessPath is { } exe)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null) Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
            }
            catch (IOException) { }
        }
        var root = new Grid();
        root.ColumnDefinitions.Add(new() { Width = new GridLength(218) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var side = new DockPanel { LastChildFill = true };
        var monogram = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(11)
        };
        monogram.Background = Mint;
        monogram.Child = new Logo();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 38) };
        var name = Stack(Text("Sada", 27, Paper, true), Text("DESKTOP AUDIO", 10, Mint, true));
        name.Margin = new Thickness(12, -2, 0, 0);
        brand.Children.Add(monogram);
        brand.Children.Add(name);
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        var sidebarFooter = Stack(signalType, signal, level);
        signal.Margin = new Thickness(0, 9, 0, 6);
        sidebarFooter.Margin = new Thickness(0, 16, 0, 5);
        DockPanel.SetDock(sidebarFooter, Dock.Bottom);
        side.Children.Add(sidebarFooter);
        side.Children.Add(nav);
        var sideFrame = new Border { Background = Side, Child = side, Padding = new Thickness(19, 20, 17, 16), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 1, 0) };
        root.Children.Add(sideFrame);

        var workspace = new DockPanel { LastChildFill = true };
        toggle = Button("", app.ToggleWidgets);
        var full = Button("Full screen  ↗", app.ToggleFull, true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(toggle);
        actions.Children.Add(full);
        var upper = new DockPanel { Margin = new Thickness(25, 17, 20, 5), LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Right);
        upper.Children.Add(actions);
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(0, 0, 8, 6);
        upper.Children.Add(count);
        DockPanel.SetDock(upper, Dock.Top);
        workspace.Children.Add(upper);
        scroll.Content = canvas;
        workspace.Children.Add(scroll);
        Grid.SetColumn(workspace, 1);
        root.Children.Add(workspace);
        var footer = BuildFooter();
        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 2);
        root.Children.Add(footer);
        Content = root;
        ReloadPage();
    }

    UIElement BuildFooter()
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var track = new DockPanel { LastChildFill = true };
        var cover = new Border { Background = Field, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 12, 0), Child = artwork, ClipToBounds = true };
        DockPanel.SetDock(cover, Dock.Left);
        track.Children.Add(cover);
        var labels = Stack(songTitle, songArtist);
        labels.VerticalAlignment = VerticalAlignment.Center;
        foreach (var item in new[] { songTitle, songArtist })
        {
            item.TextWrapping = TextWrapping.NoWrap;
            item.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        track.Children.Add(labels);
        content.Children.Add(track);
        var previous = AsyncButton("|◀", async () => await app.Media.PreviousAsync());
        var play = AsyncButton(playback, async () => await app.Media.ToggleAsync());
        var next = AsyncButton("▶|", async () => await app.Media.NextAsync());
        var controls = Wrap(previous, play, next);
        foreach (var (button, label) in new[] { (previous, "Previous track"), (play, "Play or pause"), (next, "Next track") })
        {
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.ToolTip = label;
        }
        foreach (Button control in controls.Children) control.Margin = new Thickness(3, 0, 3, 0);
        Grid.SetColumn(controls, 1);
        content.Children.Add(controls);
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(status, 2);
        content.Children.Add(status);
        return new Border { Child = content, Background = Side, BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(19, 10, 20, 10) };
    }

    internal void UpdateStatus(string value) { status.Text = value; status.ToolTip = value; }
    internal void UpdateLyricsStatus(string value) { if (lyricsStatus is not null) lyricsStatus.Text = value; }
    internal void UpdateScene()
    {
        var enabled = Scene.Widgets.Count(x => x.Value.Enabled);
        count.Text = app.WidgetsVisible ? $"{enabled} widget{(enabled == 1 ? "" : "s")} active" : "Widgets hidden";
        toggle.Content = app.WidgetsVisible ? "Hide widgets" : "Show widgets";
        if (preview != null) SyncAlbumPreview();
        if (previewLabel != null) previewLabel.Text = Friendly(Scene.Visual.Mode.ToString());
        signalType.Text = app.Prefs.Microphone ? "MICROPHONE" : "WINDOWS PLAYBACK";
        widgetPreview?.Apply();
        SizeGlitchPreview();
        scenePreview?.Apply();
    }

    internal void ReloadPage()
    {
        preview = null;
        widgetPreview?.Release();
        widgetPreview = null;
        scenePreview?.Release();
        scenePreview = null;
        previewLabel = null;
        lyricsStatus = null;
        songTimingStatus = null;
        songTimingButtons = [];
        nav.Children.Clear();
        foreach (var item in new[] { ("Scenes", "◈"), ("Visualizer", "∿"), ("Desktop widgets", "⊞"), ("Media & lyrics", "♫"), ("Profiles", "▤"), ("Audio & system", "◌"), ("About", "ⓘ") })
        {
            var tab = Button($"{item.Item2}   {item.Item1}", () => Navigate(item.Item1), page == item.Item1);
            tab.HorizontalContentAlignment = HorizontalAlignment.Left;
            tab.Margin = new Thickness(0, 0, 0, 9);
            nav.Children.Add(tab);
        }
        var sheet = page switch
        {
            "Desktop widgets" => BuildWidgets(),
            "Media & lyrics" => BuildMedia(),
            "Profiles" => BuildProfiles(),
            "Audio & system" => BuildSystem(),
            "About" => BuildAbout(),
            "Visualizer" => BuildStudio(),
            _ => BuildScenes()
        };
        sheet.Margin = new Thickness(25, 5, 25, 20);
        canvas.Content = sheet;
        scroll.ScrollToTop();
        UpdateScene();
    }

    void Navigate(string destination)
    {
        if (page == destination) return;
        page = destination;
        ReloadPage();
        AnimatePage();
    }

    void AnimatePage()
    {
        if (!SystemParameters.ClientAreaAnimation || canvas.Content is not StackPanel sheet) return;
        var offset = new TranslateTransform();
        sheet.RenderTransform = offset;
        var duration = TimeSpan.FromMilliseconds(165);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        sheet.BeginAnimation(OpacityProperty, new DoubleAnimation(.48, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }

    internal void ShowNeonSettings()
    {
        selectedWidget = WidgetKind.Neon;
        page = "Desktop widgets";
        ReloadPage();
        AnimatePage();
    }

    StackPanel BuildScenes()
    {
        var heading = Heading(Scene.Name, "Arrange what appears on your Windows desktop.");
        var page = Stack(heading);
        var toolbar = Wrap(Button("Edit on desktop  ↗", () => app.OpenSceneEditor(), true),
            Button("Widget settings", () => Navigate("Desktop widgets")));
        toolbar.Margin = new Thickness(0, 0, 0, 11);
        page.Children.Add(toolbar);

        var stage = new Grid { Height = 325, ClipToBounds = true };
        scenePreview = new ScenePreview(app) { Margin = new Thickness(18) };
        stage.Children.Add(scenePreview);
        if (!Scene.Widgets.Values.Any(item => item.Enabled))
        {
            var empty = Text("Add a widget in the desktop editor", 14, Soft);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            stage.Children.Add(empty);
        }
        var backdrop = new LinearGradientBrush(Color.FromRgb(16, 27, 51), Color.FromRgb(21, 11, 37), 28);
        page.Children.Add(Card(stage, backdrop, 0));
        var templates = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var name in ScenePresets.Names)
        {
            var description = name switch
            {
                "Album" => "Cover-driven color and lyrics",
                "Karaoke" => "More room for each line",
                _ => "Clock and a quiet spectrum"
            };
            var choice = Button(Stack(Text(name, 16, Paper, true), Text(description, 11, Soft)), () => app.OpenSceneEditor(name));
            choice.HorizontalContentAlignment = HorizontalAlignment.Left;
            templates.Children.Add(choice);
        }
        page.Children.Add(Section("Scene templates", templates));
        if (app.Prefs.Profiles.Count > 0)
        {
            var profiles = Wrap();
            foreach (var profile in app.Prefs.Profiles)
                profiles.Children.Add(Button(profile.Name, () => app.ApplyProfile(profile)));
            page.Children.Add(Section("Saved scenes", profiles));
        }
        return page;
    }

    StackPanel BuildStudio()
    {
        var page = Stack(Heading("Live studio", "Shape the animation from the sound playing on your PC."));
        var stage = new Grid { Height = 284, ClipToBounds = true };
        preview = new Visualizer { Settings = Scene.Visual, Margin = new Thickness(58, 28, 58, 22) };
        stage.Children.Add(preview);
        var tag = new Border { Child = Label("●  LIVE SIGNAL"), Background = Side, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(15) };
        stage.Children.Add(tag);
        previewLabel = Text("", 12, Soft, true);
        previewLabel.HorizontalAlignment = HorizontalAlignment.Right;
        previewLabel.VerticalAlignment = VerticalAlignment.Bottom;
        previewLabel.Margin = new Thickness(12);
        stage.Children.Add(previewLabel);
        var stageFill = new RadialGradientBrush(Color.FromRgb(33, 57, 85), Color.FromRgb(10, 20, 39))
            { Center = new Point(.51, .57), GradientOrigin = new Point(.49, .56), RadiusX = .68, RadiusY = .85 };
        page.Children.Add(Card(stage, stageFill, 0));
        var options = new UniformGridProxy();
        var visual = Scene.Visual;
        options.Add(Choice("Pattern", Enum.GetValues<VisualMode>(), visual.Mode, value => { visual.Mode = value; app.ApplyChanges(); }));
        options.Add(Choice("Palette", Enum.GetValues<VisualPalette>(), visual.Palette, value => { visual.Palette = value; app.ApplyChanges(); }));
        options.Add(Choice("Direction", Enum.GetValues<VisualDirection>(), visual.Direction, value => { visual.Direction = value; app.ApplyChanges(); }));
        options.Add(Choice("Atmosphere", Enum.GetValues<VisualAtmosphere>(), visual.Atmosphere, value => { visual.Atmosphere = value; app.ApplyChanges(); }));
        options.Add(Range("Density", 12, 128, visual.Count, v => { visual.Count = (int)v; app.QueueSave(); }, 1));
        options.Add(Range("Line weight", .05, 1, visual.Thickness, v => { visual.Thickness = v; app.QueueSave(); }));
        options.Add(Range("Glow", 0, 1, visual.Glow, v => { visual.Glow = v; app.QueueSave(); }));
        options.Add(Range("Motion speed", .25, 3, visual.Speed, v => { visual.Speed = v; app.QueueSave(); }));
        options.Add(Range("Atmosphere strength", 0, 1, visual.AtmosphereIntensity, v => { visual.AtmosphereIntensity = v; app.QueueSave(); }));
        options.Add(Range("Visual opacity", .08, 1, visual.Opacity, v => { visual.Opacity = v; app.QueueSave(); }));
        options.Add(Entry("Custom starting color", visual.CustomColor, value => { visual.CustomColor = value; app.QueueSave(); }, "Apply"));
        options.Add(Entry("Custom ending color", visual.CustomAccent, value => { visual.CustomAccent = value; app.QueueSave(); }, "Apply"));
        options.Add(Check("Reflect pattern", visual.Mirror, value => { visual.Mirror = value; app.QueueSave(); }));
        options.Add(Check("Use colors from album art", Scene.MoodColor, value => { Scene.MoodColor = value; app.ApplyChanges(); }));
        page.Children.Add(Section("Appearance", options));
        return page;
    }

    StackPanel BuildWidgets()
    {
        var page = Stack(Heading("Desktop widgets", "Set up each widget and choose where it appears."));
        var selector = Choice("Choose a widget", Enum.GetValues<WidgetKind>(), selectedWidget, value =>
        {
            selectedWidget = value;
            Dispatcher.BeginInvoke(() => { ReloadPage(); AnimatePage(); });
        }, WidgetName);
        selector.Margin = new Thickness(0, 0, 0, 12);
        page.Children.Add(selector);
        var current = Scene.Widget(selectedWidget);
        var label = selectedWidget == WidgetKind.Neon ? "Show neon on the desktop" : "Show " + WidgetName(selectedWidget);
        var detail = Stack(Check(label, current.Enabled, value => { current.Enabled = value; app.ApplyChanges(); }));
        if (selectedWidget == WidgetKind.Neon)
            detail.Children.Add(Check("Show neon in full screen", Scene.FullNeon, value => { Scene.FullNeon = value; app.ApplyChanges(); }));
        if (selectedWidget == WidgetKind.GlitchBox)
            detail.Children.Add(Check("Show Glitch Box in full screen", Scene.FullGlitchBox, value => { Scene.FullGlitchBox = value; app.ApplyChanges(); }));
        detail.Children.Add(Choice("Placement", Enum.GetValues<Placement>(), current.Placement, value => { current.Placement = value; app.ApplyChanges(); },
            x => x switch { Placement.Desktop => "On the desktop", Placement.Floating => "Normal window", _ => "Always on top" }));
        detail.Children.Add(Range("Widget opacity", .08, 1, current.Opacity, value => { current.Opacity = value; app.ApplyChanges(); }));
        if (!DesktopWidget.IsWallpaperSize(selectedWidget))
        {
            detail.Children.Add(Range("Backdrop", 0, 1, current.Surface, value => { current.Surface = value; app.ApplyChanges(); }));
            detail.Children.Add(Check("Lock position and ignore clicks", current.Locked, value => { current.Locked = value; app.ApplyChanges(); }));
            string[] sizes = selectedWidget == WidgetKind.GlitchBox
                ? ["Custom", "Small", "Medium", "Large", "Square", "Wide", "Banner"]
                : ["Custom", "Small", "Medium", "Large", "Wide", "Banner"];
            detail.Children.Add(Choice("Size preset", sizes, "Custom", value =>
            {
                (current.Width, current.Height) = value switch
                {
                    "Small" => (240, 150), "Medium" => (340, 260), "Large" => (490, 390),
                    "Square" => (360, 360), "Wide" => (720, 250), "Banner" => (960, 170), _ => (current.Width, current.Height)
                };
                app.ApplyChanges();
            }));
            detail.Children.Add(SizeEditor(current));
        }
        switch (selectedWidget)
        {
            case WidgetKind.Clock:
                detail.Children.Add(ClockOptions());
                break;
            case WidgetKind.Lyrics:
                detail.Children.Add(Choice("Lyrics style", Enum.GetValues<LyricsStyle>(), Scene.LyricsStyle, value => { Scene.LyricsStyle = value; app.ApplyChanges(); }));
                detail.Children.Add(Range("Maximum text size", 12, 34, Scene.LyricsTextSize, value => { Scene.LyricsTextSize = value; app.QueueSave(); }, 1));
                detail.Children.Add(LyricsActions());
                break;
            case WidgetKind.Visualizer:
                detail.Children.Add(Check("Track card on the visualizer", Scene.TrackOnVisualizer, v => { Scene.TrackOnVisualizer = v; app.ApplyChanges(); }));
                detail.Children.Add(Check("Album cover inside Ring and Halo", Scene.RingCover, v => { Scene.RingCover = v; app.ApplyChanges(); }));
                detail.Children.Add(Check("Match colors to the cover", Scene.MoodColor, v => { Scene.MoodColor = v; app.ApplyChanges(); }));
                detail.Children.Add(Check("Ambient album art", Scene.AlbumGlow, v => { Scene.AlbumGlow = v; app.ApplyChanges(); }));
                break;
            case WidgetKind.Media:
                detail.Children.Add(Choice("Card layout", Enum.GetValues<CardLayout>(), Scene.MediaLayout, v => { Scene.MediaLayout = v; app.ApplyChanges(); }));
                break;
            case WidgetKind.Neon:
                detail.Children.Add(NeonControls());
                break;
            case WidgetKind.GlitchBox:
                detail.Children.Add(GlitchControls());
                break;
            case WidgetKind.Environment:
                detail.Children.Add(Choice("Environment", new[] { "Dusk", "Tides", "Skyline" }, new[] { "Dusk", "Tides", "Skyline" }[Math.Clamp(Scene.EnvironmentStyle, 0, 2)], value =>
                {
                    Scene.EnvironmentStyle = Array.IndexOf(new[] { "Dusk", "Tides", "Skyline" }, value);
                    app.ApplyChanges();
                }));
                break;
            case WidgetKind.Wallpaper:
                detail.Children.Add(Button("Choose wallpaper file…", PickWallpaper));
                detail.Children.Add(Text(string.IsNullOrEmpty(Scene.ImagePath) ? "Supports images and local videos." : Path.GetFileName(Scene.ImagePath), 12, Soft));
                break;
            case WidgetKind.ScreenGlow:
                var styles = new[] { "Lower edge", "Upper edge", "Side edges", "Outline", "Taskbar spectrum", "Corner glow", "Horizon", "Pulse", "Moving lights", "Surround spectrum" };
                detail.Children.Add(Choice("Glow style", styles, styles[Math.Clamp(Scene.GlowStyle, 0, 9)], value => { Scene.GlowStyle = Array.IndexOf(styles, value); app.ApplyChanges(); }));
                detail.Children.Add(Range("Glow strength", .05, 1, Scene.GlowStrength, value => { Scene.GlowStrength = value; app.ApplyChanges(); }));
                break;
            case WidgetKind.Weather:
                detail.Children.Add(Entry("City", Scene.WeatherCity, value => _ = app.WeatherAsync(value), "Search"));
                detail.Children.Add(Text("Weather by Open-Meteo", 11, Soft));
                break;
        }
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var controls = Card(detail);
        controls.Margin = new Thickness(0, 0, 11, 0);
        pair.Children.Add(controls);
        widgetPreview = new WidgetView(app, selectedWidget, false) { Height = 248, ClipToBounds = true, PreviewMediaWidget = true };
        FrameworkElement previewSurface = widgetPreview;
        if (selectedWidget == WidgetKind.GlitchBox)
        {
            SizeGlitchPreview();
            previewSurface = new Viewbox { Height = 248, Stretch = Stretch.Uniform, Child = widgetPreview };
        }
        var render = Card(Stack(Label("WIDGET PREVIEW"), previewSurface), Night, 11);
        render.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(render, 1);
        pair.Children.Add(render);
        page.Children.Add(pair);
        var grid = new UniformGridProxy();
        foreach (var kind in Enum.GetValues<WidgetKind>())
        {
            var options = Scene.Widget(kind);
            grid.Add(Check(WidgetName(kind), options.Enabled, value =>
            {
                options.Enabled = value;
                app.ApplyChanges();
                if (selectedWidget == kind) ReloadPage();
            }));
        }
        page.Children.Add(Section("All widgets", grid));
        return page;
    }

    void SizeGlitchPreview()
    {
        if (selectedWidget != WidgetKind.GlitchBox || widgetPreview is null) return;
        var size = Scene.Widget(WidgetKind.GlitchBox);
        widgetPreview.Width = Math.Clamp(size.Width, 140, 2000);
        widgetPreview.Height = Math.Clamp(size.Height, 90, 1260);
    }

    UIElement NeonControls()
    {
        var options = Scene.Neon;
        var inks = Stack(ColorInput("Title color", options.TitleColor, value => options.TitleColor = value),
            ColorInput("Artist color", options.ArtistColor, value => options.ArtistColor = value));
        inks.Visibility = options.Colors == NeonColors.Custom ? Visibility.Visible : Visibility.Collapsed;
        return Stack(
            Choice("Lettering", Enum.GetValues<NeonLettering>(), options.Lettering, value => { options.Lettering = value; app.ApplyChanges(); }),
            Choice("Colors", Enum.GetValues<NeonColors>(), options.Colors, value =>
            {
                options.Colors = value;
                inks.Visibility = value == NeonColors.Custom ? Visibility.Visible : Visibility.Collapsed;
                app.ApplyChanges();
            }, value => value switch { NeonColors.Album => "Album cover", NeonColors.RoseIce => "Rose and ice", _ => Friendly(value.ToString()) }),
            inks,
            Range("Glow intensity", 0, 2, options.GlowIntensity, value => { options.GlowIntensity = value; app.QueueSave(); }),
            Check("Show glass trim", options.GlassTrim, value => { options.GlassTrim = value; app.QueueSave(); }),
            Choice("Music response", Enum.GetValues<NeonReaction>(), options.Reaction, value => { options.Reaction = value; app.ApplyChanges(); },
                value => value == NeonReaction.Surge ? "Power surge" : Friendly(value.ToString())),
            Range("Response strength", 0, 2, options.ReactionStrength, value => { options.ReactionStrength = value; app.QueueSave(); }),
            Choice("Tube flicker", Enum.GetValues<NeonFlicker>(), options.Flicker, value => { options.Flicker = value; app.QueueSave(); }),
            Choice("After playback stops", Enum.GetValues<NeonIdle>(), options.Idle, value => { options.Idle = value; app.ApplyChanges(); },
                value => value switch { NeonIdle.ShowTube => "Show unlit tubes", NeonIdle.Fade => "Fade unlit tubes", _ => "Hide sign" }),
            Check("Acrylic backing", options.Acrylic, value => { options.Acrylic = value; app.ApplyChanges(); }),
            Check("Show artist", options.ShowArtist, value => { options.ShowArtist = value; app.ApplyChanges(); }),
            Check("Show music note", options.ShowNote, value => { options.ShowNote = value; app.ApplyChanges(); }),
            Check("Click desktop sign to play or pause", options.ClickToPlay, value => { options.ClickToPlay = value; app.ApplyChanges(); }),
            Button("Reset neon look", () => ResetWidgetLook(() => Scene.Neon = new NeonOptions())));
    }

    UIElement GlitchControls()
    {
        var options = Scene.Glitch;
        const double subtle = .28, medium = .65, heavy = 1;
        var accent = ColorInput("Accent color", options.Accent, value => options.Accent = value);
        accent.Visibility = options.Colors == GlitchColors.Custom ? Visibility.Visible : Visibility.Collapsed;
        return Stack(
            Choice("Shows", Enum.GetValues<GlitchContent>(), options.Content, value => { options.Content = value; app.QueueSave(); },
                value => value == GlitchContent.Artwork ? "Album cover" : "Song title"),
            Choice("Distortion", new[] { subtle, medium, heavy }, options.Intensity < .46 ? subtle : options.Intensity < .83 ? medium : heavy,
                value => { options.Intensity = value; app.QueueSave(); }, value => value == subtle ? "Subtle" : value == heavy ? "Heavy" : "Medium"),
            Choice("Accent colors", Enum.GetValues<GlitchColors>(), options.Colors, value =>
            {
                options.Colors = value;
                accent.Visibility = value == GlitchColors.Custom ? Visibility.Visible : Visibility.Collapsed;
                app.ApplyChanges();
            }, value => value switch { GlitchColors.Album => "Album cover", GlitchColors.Visualizer => "Match the visualizer", _ => "Custom" }),
            accent,
            Range("Music response", 0, 3, options.MusicResponse, value => { options.MusicResponse = value; app.QueueSave(); }),
            Check("Scanlines", options.Scanlines, value => { options.Scanlines = value; app.QueueSave(); }),
            Choice("After playback stops", Enum.GetValues<GlitchIdle>(), options.Idle, value => { options.Idle = value; app.QueueSave(); },
                value => value switch { GlitchIdle.Keep => "Keep visible", GlitchIdle.Dim => "Dim", _ => "Hide" }),
            Button("Reset Glitch Box look", () => ResetWidgetLook(() => Scene.Glitch = new GlitchOptions())));
    }

    void ResetWidgetLook(Action resetOptions)
    {
        resetOptions();
        app.ApplyChanges();
        ReloadPage();
    }

    UIElement ColorInput(string label, string current, Action<string> save) =>
        Entry(label, current, value =>
        {
            var color = value.Trim();
            if (color.Length != 7 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
            { app.SetStatus("Enter a color in #RRGGBB format."); return; }
            save(color);
            app.ApplyChanges();
        }, "Apply");

    UIElement SizeEditor(WidgetOptions options)
    {
        var sizes = Wrap();
        var width = Input(options.Width.ToString("0", CultureInfo.InvariantCulture), 64);
        var height = Input(options.Height.ToString("0", CultureInfo.InvariantCulture), 64);
        var left = Input(options.Left?.ToString("0", CultureInfo.InvariantCulture) ?? "", 64);
        var top = Input(options.Top?.ToString("0", CultureInfo.InvariantCulture) ?? "", 64);
        sizes.Children.Add(Stack(Text("Width", 11, Soft), width));
        sizes.Children.Add(Stack(Text("Height", 11, Soft), height));
        sizes.Children.Add(Stack(Text("X", 11, Soft), left));
        sizes.Children.Add(Stack(Text("Y", 11, Soft), top));
        var buttons = Wrap(Button("Apply size & position", () =>
        {
            if (ParseNumber(width.Text) is { } w) options.Width = Math.Clamp(w, 140, 2000);
            if (ParseNumber(height.Text) is { } h) options.Height = Math.Clamp(h, 90, 1260);
            if (ParseNumber(left.Text) is { } x) { options.Left = x; options.RelativeLeft = null; }
            if (ParseNumber(top.Text) is { } y) { options.Top = y; options.RelativeTop = null; }
            app.ApplyChanges();
        }), Button("Reset position", () =>
        {
            options.Left = options.Top = options.RelativeLeft = options.RelativeTop = null;
            left.Clear();
            top.Clear();
            app.ApplyChanges();
        }));
        return Stack(sizes, buttons);
    }

    static double? ParseNumber(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    UIElement ClockOptions()
    {
        return Stack(
            Choice("Clock face", Enum.GetValues<ClockFace>(), Scene.Clock, value => { Scene.Clock = value; app.ApplyChanges(); }),
            Choice("Typography", new[] { "Segoe UI Variable Display", "Bahnschrift", "Georgia", "Cascadia Mono", "Trebuchet MS" }, Scene.ClockFont, value => { Scene.ClockFont = value; app.ApplyChanges(); }),
            Entry("Clock color", Scene.ClockColor, value => { Scene.ClockColor = value; app.ApplyChanges(); }, "Apply"),
            Range("Character spacing", 0, 8, Scene.ClockTracking, value => { Scene.ClockTracking = (int)value; app.ApplyChanges(); }, 1),
            Check("Gradient color", Scene.ClockGradient, value => { Scene.ClockGradient = value; app.ApplyChanges(); }),
            Check("Bold type", Scene.BoldClock, value => { Scene.BoldClock = value; app.ApplyChanges(); }),
            Check("Clock glow", Scene.ClockGlow, value => { Scene.ClockGlow = value; app.ApplyChanges(); }),
            Check("24-hour time", Scene.Time24Hours, value => { Scene.Time24Hours = value; app.ApplyChanges(); }),
            Check("Show seconds", Scene.Seconds, value => { Scene.Seconds = value; app.ApplyChanges(); }),
            Check("Show date", Scene.Date, value => { Scene.Date = value; app.ApplyChanges(); }));
    }

    StackPanel BuildMedia()
    {
        var page = Stack(Heading("Media & lyrics", "Follow the Windows media session on the desktop or in full screen."));
        page.Children.Add(Section("Playback source", Stack(
            Choice("Follow", Enum.GetValues<MediaSourcePreference>(), app.Prefs.PlaybackSource, app.ChangePlaybackSource, source => source.ToString()),
            Wrap(Button("Match YouTube song…", app.OpenYouTubeMatch), Button("Register browser bridge", app.RegisterYouTubeBridge)),
            Text("Automatic prefers Spotify. Choose YouTube to try your browser’s Windows media session or use the optional Chrome/Edge add-on.", 12, Soft))));
#if SADA_LYRICS_STUDIO
        page.Children.Add(Section("Lyrics Studio", Stack(Button("Edit this recording  ↗", app.OpenLyricsStudio, true),
            Text("Set line and word timing against the song as it plays.", 12, Soft))));
#endif
        var options = new UniformGridProxy();
        options.Add(Choice("Now playing layout", Enum.GetValues<CardLayout>(), Scene.MediaLayout, value => { Scene.MediaLayout = value; app.ApplyChanges(); }));
        options.Add(Choice("Lyrics layout", Enum.GetValues<LyricsStyle>(), Scene.LyricsStyle, value => { Scene.LyricsStyle = value; app.ApplyChanges(); }));
        options.Add(Check("Show media widget", Scene.Widget(WidgetKind.Media).Enabled, value => { Scene.Widget(WidgetKind.Media).Enabled = value; app.ApplyChanges(); }));
        options.Add(Check("Show lyrics widget", Scene.Widget(WidgetKind.Lyrics).Enabled, value => { Scene.Widget(WidgetKind.Lyrics).Enabled = value; app.ApplyChanges(); }));
        options.Add(Range("Maximum text size", 12, 34, Scene.LyricsTextSize, value => { Scene.LyricsTextSize = value; app.QueueSave(); }, 1));
        page.Children.Add(Section("Desktop", options));
        page.Children.Add(Section("Lyrics source", LyricsActions()));
        page.Children.Add(Section("Timing for this song", BuildSongTiming()));
        var full = new UniformGridProxy();
        full.Add(Choice("Backdrop", Enum.GetValues<Backdrop>(), Scene.FullBackdrop, value => { Scene.FullBackdrop = value; app.ApplyChanges(); }));
        full.Add(Button("Choose image or video…", PickWallpaper));
        full.Add(Check("Show playback controls", Scene.FullMedia, value => { Scene.FullMedia = value; app.ApplyChanges(); }));
        full.Add(Check("Show clock", Scene.FullClock, value => { Scene.FullClock = value; app.ApplyChanges(); }));
        full.Add(Check("Show lyrics", Scene.FullLyrics, value => { Scene.FullLyrics = value; app.ApplyChanges(); }));
        full.Add(Button("Enter full screen", app.ToggleFull, true));
        page.Children.Add(Section("Full screen", Stack(full, Text("Space plays or pauses. Left and right change tracks. V toggles the visualizer, C cycles colors and Esc closes full screen.", 12, Soft))));
        return page;
    }

    UIElement BuildSongTiming()
    {
        songTimingStatus = Text("", 13, Mint, true);
        songTimingStatus.Margin = new Thickness(0, 2, 0, 8);
        songTimingButtons =
        [
            Button("Later by 0.10 s", () => { app.AdjustCurrentLyricTiming(-.10); UpdateSongTiming(); }),
            Button("Earlier by 0.10 s", () => { app.AdjustCurrentLyricTiming(.10); UpdateSongTiming(); }),
            Button("Clear saved timing", () => { app.ClearCurrentLyricTiming(); UpdateSongTiming(); })
        ];
        UpdateSongTiming();
        return Stack(songTimingStatus, Wrap(songTimingButtons),
            Check("Adjust automatically when the audio matches", app.Prefs.AdaptiveLyricsTiming, enabled =>
            {
                app.Prefs.AdaptiveLyricsTiming = enabled;
                app.QueueSave();
                UpdateSongTiming();
            }));
    }

    void UpdateSongTiming()
    {
        if (songTimingStatus is null) return;
        var timing = app.CurrentLyricTiming();
        if (songTimingStatus.Text != timing) songTimingStatus.Text = timing;
        bool available = app.HasSongTimingTarget;
        foreach (var button in songTimingButtons) button.IsEnabled = available;
    }

    void PickWallpaper()
    {
        var dialog = new OpenFileDialog { Filter = "Supported files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.wmv;*.avi;*.mov|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        Scene.ImagePath = dialog.FileName;
        app.ApplyChanges(true);
        ReloadPage();
    }

    UIElement LyricsActions()
    {
        lyricsStatus = Text(app.LyricsMessage, 13, Mint, true);
        lyricsStatus.Margin = new Thickness(0, 2, 0, 8);
        return Stack(lyricsStatus, Check("Find lyrics automatically", Scene.AutoLyrics, app.SetAutomaticLyrics),
            Wrap(Button("Open lyrics file…", () =>
            {
                var dialog = new OpenFileDialog { Filter = "Lyrics files|*.lrc;*.txt;*.lyricsfile;*.yaml;*.yml|All files|*.*" };
                if (dialog.ShowDialog(this) == true) _ = app.LoadLyricsAsync(dialog.FileName);
            }),
            AsyncButton("Search again", app.FetchLyricsAsync),
            Button("Clear lyrics", app.ClearLyrics)),
            Text("Online lookup uses the current track’s title and artist on LRCLIB.", 12, Soft));
    }

    StackPanel BuildProfiles()
    {
        var page = Stack(Heading("Profiles", "Save a desktop arrangement. Apply it from here or the system tray."));
        var name = Input("", 185);
        var process = Input("", 140);
        var start = Input("", 90);
        var fields = Wrap(Stack(Text("Profile name", 12, Soft), name), Stack(Text("Foreground app (optional)", 12, Soft), process), Stack(Text("Daily time (HH:mm)", 12, Soft), start));
        var form = Stack(fields, Button("Save current scene", () =>
        {
            var label = name.Text.Trim();
            if (label.Length == 0) { app.SetStatus("Enter a profile name."); return; }
            var time = start.Text.Trim();
            if (time.Length > 0 && !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            { app.SetStatus("Use a 24-hour time such as 19:30."); return; }
            var profile = new SceneProfile(label, Preferences.Clone(Scene), process.Text.Trim(), time);
            var existing = app.Prefs.Profiles.FindIndex(p => string.Equals(p.Name, label, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) app.Prefs.Profiles.Add(profile);
            else app.Prefs.Profiles[existing] = profile;
            app.QueueSave();
            app.SetStatus("Profile saved · " + label);
            ReloadPage();
        }, true));
        page.Children.Add(Section("Save a profile", form));
        var saved = Stack();
        foreach (var profile in app.Prefs.Profiles)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 6), LastChildFill = true };
            var actions = Wrap(Button("Apply", () => app.ApplyProfile(profile)), Button("Remove", () =>
            {
                if (MessageBox.Show(this, $"Remove the saved profile “{profile.Name}”?", "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                app.Prefs.Profiles.Remove(profile);
                app.QueueSave();
                ReloadPage();
            }));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            row.Children.Add(Stack(Text(profile.Name, 16, Paper, true),
                Text(string.Join("   ·   ", new[] { profile.Process, profile.StartTime }.Where(s => !string.IsNullOrWhiteSpace(s))), 12, Soft)));
            saved.Children.Add(row);
        }
        if (app.Prefs.Profiles.Count == 0) saved.Children.Add(Text("No saved profiles", 13, Soft));
        page.Children.Add(Section("Saved scenes", saved));
        return page;
    }

    StackPanel BuildSystem()
    {
        var page = Stack(Heading("Audio & system", "Choose what Sada listens to and how it runs on Windows."));
        var devices = app.Audio.GetDevices();
        var device = devices.FirstOrDefault(d => (d.Id ?? "") == app.Prefs.AudioId && d.IsMicrophone == app.Prefs.Microphone);
        var audio = new UniformGridProxy();
        audio.Add(Choice("Capture device", devices, device, value =>
        {
            if (value is null) return;
            app.Prefs.AudioId = value.Id ?? "";
            app.Prefs.Microphone = value.IsMicrophone;
            app.RestartAudio();
            UpdateScene();
            app.QueueSave();
        }, x => x?.Name ?? "Windows default playback"));
        audio.Add(Button("Refresh devices", () => { app.RestartAudio(); ReloadPage(); }));
        audio.Add(Range("Sensitivity", .25, 3, Scene.Visual.Sensitivity, value => { Scene.Visual.Sensitivity = value; app.QueueSave(); }));
        audio.Add(Range("Smoothing", 0, 1, Scene.Visual.Smoothing, value => { Scene.Visual.Smoothing = value; app.QueueSave(); }));
        page.Children.Add(Section("Audio capture", audio));
        var system = new UniformGridProxy();
        system.Add(Choice("Frame cap", new[] {24, 30, 60, 120}, app.Prefs.Fps == 0 ? 120 : app.Prefs.Fps, value => { app.Prefs.Fps = value; app.QueueSave(); }, value => $"{value} fps"));
        system.Add(Check("Reduce refresh while silent", app.Prefs.ReduceOnSilence, value => { app.Prefs.ReduceOnSilence = value; app.QueueSave(); }));
        system.Add(Check("Pause covered widgets", app.Prefs.PauseWhenCovered, value => { app.Prefs.PauseWhenCovered = value; app.QueueSave(); }));
        system.Add(Check("Launch at Windows login", app.Prefs.StartAtLogin, value => { if (!app.SetStartup(value)) ReloadPage(); }));
        system.Add(Check("Global shortcuts (Ctrl+Alt+Shift+M / Ctrl+Alt+Shift+F)", app.Prefs.Hotkeys, value => { app.Prefs.Hotkeys = value; app.ConfigureHotkeys(); app.QueueSave(); }));
        system.Add(Choice("Widget monitor", new[] { "" }.Concat(Forms.Screen.AllScreens.Select(x => x.DeviceName)), Scene.MonitorName, value =>
        { Scene.MonitorName = value; app.ApplyChanges(true); }, x => x.Length == 0 ? "Primary monitor" : x.Replace(@"\\.\DISPLAY", "Display ")));
        system.Add(Check("Repeat full-size effects on every monitor", Scene.AllMonitors, value => { Scene.AllMonitors = value; app.ApplyChanges(true); }));
        system.Add(Button("Exit Sada", app.ExitApp));
        page.Children.Add(Section("Windows & performance", system));
        return page;
    }

    StackPanel BuildAbout()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3);
        var page = Stack(Heading("About Sada", $"Desktop audio studio · Version {version}"));
        var credit = Text("Built by Abood", 22, Paper, true);
        credit.Margin = new Thickness(0, 0, 0, 12);
        var github = Button("GitHub · @Abood03  ↗", () =>
        {
            try { Process.Start(new ProcessStartInfo(DeveloperProfile) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                app.SetStatus("Could not open the GitHub profile in the default browser.");
            }
        }, true);
        github.HorizontalAlignment = HorizontalAlignment.Left;
        github.ToolTip = DeveloperProfile;
        page.Children.Add(Card(Stack(credit, github)));
        return page;
    }

    internal void Render(AudioFrame frame)
    {
        var now = Environment.TickCount64;
        if (now - lastPreviewFrame >= 32)
        {
            lastPreviewFrame = now;
            if (preview != null) { preview.Frame = frame; preview.InvalidateVisual(); }
            scenePreview?.Render(frame);
            widgetPreview?.Render(frame, new Point(double.NaN, double.NaN));
        }
        if (now - lastDisplay < 110) return;
        lastDisplay = now;
        UpdateSongTiming();
        signal.Value = frame.Level;
        level.Text = $"{frame.Level * 100:0}% signal";
        level.ToolTip = app.Audio.Error ?? app.Status;
        var song = app.Media.Current;
        songTitle.Text = song.HasTrack ? song.Title : "Waiting for playback";
        songArtist.Text = song.HasTrack ? song.Artist : "Windows media";
        playback.Text = song.IsPlaying ? "Ⅱ" : "▶";
        if (!ReferenceEquals(song.Artwork, coverBytes))
        {
            coverBytes = song.Artwork;
            artwork.Source = ReadImage(coverBytes);
        }
        if (preview != null) SyncAlbumPreview();
    }

    void SyncAlbumPreview()
    {
        if (preview is null) return;
        preview.Settings = Scene.Visual;
        preview.RingArtwork = Scene.RingCover ? artwork.Source : null;
        preview.ArtworkColors = Scene.MoodColor ? AlbumPalette.From(artwork.Source as BitmapSource) : null;
    }

    internal void RenderLyrics()
    {
        widgetPreview?.RenderLyrics();
        scenePreview?.RenderLyrics();
    }

    internal static ImageSource? ReadImage(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = 720;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or ArgumentException) { return null; }
    }

    static StackPanel Heading(string title, string description)
    {
        var lead = Text(title, 37, Paper, true);
        lead.FontFamily = new FontFamily("Bahnschrift SemiLight, Segoe UI Variable Display");
        var box = Stack(lead, Text(description, 14, Soft));
        box.Margin = new Thickness(0, 0, 0, 15);
        return box;
    }

    static Border Section(string name, UIElement content)
    {
        var heading = Text(name, 19, Paper, true);
        heading.Margin = new Thickness(0, 0, 0, 11);
        var section = Card(Stack(heading, content));
        section.Margin = new Thickness(0, 13, 0, 0);
        return section;
    }

    static StackPanel Choice<T>(string label, IEnumerable<T> items, T selected, Action<T> changed, Func<T, string>? display = null)
    {
        var box = new ComboBox { MinWidth = 104, Background = Field, Foreground = Paper, Padding = new Thickness(7, 4, 7, 4), BorderBrush = Line, Margin = new Thickness(0, 5, 0, 4) };
        foreach (var item in items)
        {
            var option = new ComboBoxItem { Content = display?.Invoke(item) ?? Friendly(item?.ToString() ?? ""), Tag = item };
            box.Items.Add(option);
            if (EqualityComparer<T>.Default.Equals(item, selected)) box.SelectedItem = option;
        }
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: T value }) changed(value); };
        return Stack(Text(label, 12, Soft, true), box);
    }

    static TextBox Input(string text, double width = 145) => new() { Text = text, Width = width, Margin = new Thickness(0, 4, 7, 7) };

    static UIElement Entry(string label, string start, Action<string> commit, string verb)
    {
        var field = Input(start);
        field.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) commit(field.Text.Trim()); };
        return Stack(Text(label, 12, Soft), Wrap(field, Button(verb, () => commit(field.Text.Trim()))));
    }

    static UIElement Range(string label, double min, double max, double initial, Action<double> change, double step = .01)
    {
        var value = Text(initial.ToString("0.##"), 12, Mint);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(initial, min, max),
            IsSnapToTickEnabled = true, TickFrequency = step, SmallChange = step, LargeChange = step * 10, Margin = new Thickness(0, 7, 0, 6) };
        slider.ValueChanged += (_, e) => { value.Text = e.NewValue.ToString("0.##"); change(e.NewValue); };
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(value, Dock.Right);
        header.Children.Add(value);
        header.Children.Add(Text(label, 12, Soft, true));
        return Stack(header, slider);
    }

    static CheckBox Check(string label, bool start, Action<bool> commit)
    {
        var check = new CheckBox { Content = Text(label, 13), IsChecked = start, Margin = new Thickness(0, 3, 0, 9) };
        check.Checked += (_, _) => commit(true);
        check.Unchecked += (_, _) => commit(false);
        return check;
    }

    internal static string WidgetName(WidgetKind kind) => kind switch
    {
        WidgetKind.Visualizer => "Visualizer",
        WidgetKind.Media => "Now playing",
        WidgetKind.AlbumBackdrop => "Album backdrop",
        WidgetKind.ScreenGlow => "Screen glow",
        WidgetKind.Span => "Spanning spectrum",
        WidgetKind.Fluid => "Fluid motion",
        WidgetKind.Listening => "Listening stats",
        WidgetKind.System => "System monitor",
        WidgetKind.Disks => "Disk space",
        WidgetKind.Mood => "Album colors",
        WidgetKind.Flock => "Murmuration",
        WidgetKind.Disc => "Disc player",
        WidgetKind.Neon => "Neon sign",
        WidgetKind.GlitchBox => "Glitch Box",
        _ => Friendly(kind.ToString())
    };

    static string Friendly(string input) => System.Text.RegularExpressions.Regex.Replace(input, "(?<=[a-z])(?=[A-Z])", " ");

    sealed class UniformGridProxy : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGridProxy() { Columns = 2; }
        public void Add(UIElement child)
        {
            if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 13, 8);
            Children.Add(child);
        }
    }

    sealed class Logo : FrameworkElement
    {
        protected override void OnRender(DrawingContext ctx)
        {
            base.OnRender(ctx);
            var stroke = new Pen(Ink, 2.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var points = new[] { new Point(7, 26), new Point(14, 26), new Point(20, 13), new Point(26, 33), new Point(31, 20), new Point(39, 20) };
            for (var i = 0; i < points.Length - 1; i++) ctx.DrawLine(stroke, points[i], points[i + 1]);
        }
    }
}
