using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GuideLine = System.Windows.Shapes.Line;
using Forms = System.Windows.Forms;
using static Sada.StudioUi;

namespace Sada;

internal sealed class SceneEditor : Window
{
    sealed record WidgetChoice(WidgetKind Kind, string Title);
    sealed record DisplayChoice(Forms.Screen Screen, string Title);
    sealed record Tile(Grid Surface, Border Backing, WidgetView View, Border Frame, Thumb Resize, Border Tag);
    sealed record Gesture(WidgetKind Kind, Point Pointer, Dictionary<WidgetKind, Rect> Start, Rect Group, bool Resize);

    readonly App app;
    readonly Action<SceneOptions> changed;
    readonly Func<SceneOptions, string?, bool> commit;
    readonly Canvas desktop = new() { IsHitTestVisible = false };
    readonly Canvas widgets = new() { Background = Brushes.Transparent };
    readonly Canvas guides = new() { IsHitTestVisible = false };
    readonly ComboBox displays = new() { Width = 165, DisplayMemberPath = nameof(DisplayChoice.Title) };
    readonly ComboBox presets = new() { Width = 116, ItemsSource = ScenePresets.Names, SelectedIndex = 0 };
    readonly ComboBox choices = new() { DisplayMemberPath = nameof(WidgetChoice.Title) };
    readonly TextBox name = new() { Width = 142, MaxLength = 80 };
    readonly TextBox width = new() { Width = 68, MaxLength = 8 };
    readonly TextBox height = new() { Width = 68, MaxLength = 8 };
    readonly TextBlock title = Text("", 18, Paper, true);
    readonly TextBlock message = Text("", 12, Soft);
    readonly TextBlock opacityLabel = Text("", 12, Soft);
    readonly Slider opacity = new() { Minimum = .08, Maximum = 1, IsMoveToPointEnabled = true, SmallChange = .02 };
    readonly CheckBox enabled = new() { Content = "Show on desktop" };
    readonly CheckBox locked = new() { Content = "Lock outside the editor" };
    readonly CheckBox linked = new() { Content = "Move cover and lyrics together" };
    readonly Dictionary<WidgetKind, Tile> tiles = new();
    readonly Dictionary<WidgetKind, WidgetView> backgrounds = new();
    readonly Dictionary<WidgetKind, Rect> rectangles = new();
    readonly Button resizeButton;
    readonly Border toolbar;
    readonly Border properties;
    SceneOptions draft;
    Forms.Screen screen;
    WidgetKind selected;
    Gesture? gesture;
    bool updating;

    internal SceneEditor(App app, SceneOptions draft, Action<SceneOptions> onDraftChanged,
        Func<SceneOptions, string?, bool> commit, string? preset = null)
    {
        this.app = app;
        this.draft = draft;
        this.commit = commit;
        changed = onDraftChanged;
        screen = CurrentDisplay();
        if (preset is not null)
        {
            this.draft = ScenePresets.Apply(preset, draft);
            changed(this.draft);
        }

        Title = "Sada · Desktop editor";
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = true;
        Topmost = true;
        Width = screen.WorkingArea.Width;
        Height = screen.WorkingArea.Height;
        Left = screen.WorkingArea.Left;
        Top = screen.WorkingArea.Top;
        UseLayoutRounding = true;

        AutomationProperties.SetName(displays, "Desktop display");
        AutomationProperties.SetName(presets, "Scene preset");
        AutomationProperties.SetName(name, "Name for saved scene");
        AutomationProperties.SetName(choices, "Select desktop widget");
        AutomationProperties.SetName(opacity, "Widget opacity");
        AutomationProperties.SetName(width, "Widget width");
        AutomationProperties.SetName(height, "Widget height");

        toolbar = BuildToolbar();
        resizeButton = Button("Apply size", ApplySize);
        properties = BuildInspector();
        var surface = new Grid { Background = Brush("#01000000"), ClipToBounds = true };
        surface.Children.Add(desktop);
        surface.Children.Add(widgets);
        surface.Children.Add(guides);
        surface.Children.Add(toolbar);
        surface.Children.Add(properties);
        Content = surface;

        displays.SelectionChanged += (_, _) => ChooseDisplay();
        choices.SelectionChanged += (_, _) =>
        {
            if (!updating && choices.SelectedItem is WidgetChoice choice) Select(choice.Kind);
        };
        enabled.Checked += (_, _) => SetEnabled(true);
        enabled.Unchecked += (_, _) => SetEnabled(false);
        locked.Checked += (_, _) => SetLocked(true);
        locked.Unchecked += (_, _) => SetLocked(false);
        linked.Checked += (_, _) => SetLinked(true);
        linked.Unchecked += (_, _) => SetLinked(false);
        opacity.ValueChanged += (_, _) => SetOpacity();
        toolbar.SizeChanged += (_, _) => PositionInspector();
        surface.SizeChanged += (_, _) => { PositionInspector(); ArrangeWidgets(); };
        Loaded += (_, _) =>
        {
            SyncDisplayPicker();
            UpdateScreenBounds();
            SelectInitialWidget();
            BuildPreviews();
            Keyboard.Focus(displays);
        };
        Closed += (_, _) => ReleasePreviews();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Save(false);
            }
            else if (e.Key == Key.Enter && (width.IsKeyboardFocused || height.IsKeyboardFocused))
            {
                e.Handled = true;
                ApplySize();
            }
        };
    }

    Border BuildToolbar()
    {
        var controls = Wrap(
            Labeled("Display", displays),
            Labeled("Preset", presets),
            Button("Preview preset", PreviewPreset),
            Button("Save", () => Save(false), true),
            Labeled("Scene name", name),
            Button("Save as", () => Save(true)),
            Button("Cancel", Close));
        var header = Stack(title, controls, message);
        title.Margin = new Thickness(0, 0, 0, 7);
        message.Margin = new Thickness(0, 1, 0, 0);
        var card = Card(header, Night, 13);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Margin = new Thickness(12);
        return card;
    }

    Border BuildInspector()
    {
        choices.ItemsSource = Enum.GetValues<WidgetKind>().Select(kind => new WidgetChoice(kind, Dashboard.WidgetName(kind))).ToArray();
        var numbers = new StackPanel { Orientation = Orientation.Horizontal };
        numbers.Children.Add(Labeled("Width", width));
        numbers.Children.Add(Labeled("Height", height));
        var notes = Text("Drag to place. Pull the corner to resize. Hold Alt to bypass alignment.", 12, Soft);
        notes.Margin = new Thickness(0, 9, 0, 0);
        foreach (var check in new[] { enabled, locked, linked }) check.Margin = new Thickness(0, 5, 0, 4);
        resizeButton.Margin = new Thickness(0, 0, 0, 9);
        opacity.Margin = new Thickness(0, 1, 0, 8);
        var inspector = Stack(Text("Widget", 15, Paper, true), choices, enabled, numbers, resizeButton, opacityLabel, opacity, locked, linked, notes);
        choices.Margin = new Thickness(0, 7, 0, 2);
        var card = Card(new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Night, 14);
        card.Width = 242;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.VerticalAlignment = VerticalAlignment.Top;
        return card;
    }

    static StackPanel Labeled(string label, FrameworkElement control)
    {
        var panel = Stack(Text(label, 11, Soft), control);
        panel.Margin = new Thickness(0, 0, 9, 8);
        control.Margin = new Thickness(0, 3, 0, 0);
        return panel;
    }

    void PositionInspector()
    {
        var area = Area;
        double toolbarWidth = Math.Min(area.Width, toolbar.ActualWidth);
        double toolbarHeight = toolbar.ActualHeight;
        bool hasSelection = rectangles.TryGetValue(selected, out var active);
        bool placeToolbarBelow = hasSelection && active.Top < toolbarHeight + 24 &&
            active.Right > (area.Width - toolbarWidth) / 2 && active.Left < (area.Width + toolbarWidth) / 2;
        toolbar.VerticalAlignment = placeToolbarBelow ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        double inspectorTop = placeToolbarBelow ? 12 : toolbarHeight + 24;
        properties.MaxHeight = Math.Max(80, area.Height - toolbarHeight - 36);
        bool placeInspectorRight = hasSelection && active.Left < properties.Width + 24 &&
            active.Bottom > inspectorTop && active.Top < inspectorTop + Math.Min(properties.ActualHeight, properties.MaxHeight);
        properties.HorizontalAlignment = placeInspectorRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        properties.Margin = new Thickness(12, inspectorTop, 12, 12);
    }

    Forms.Screen CurrentDisplay() =>
        Forms.Screen.AllScreens.FirstOrDefault(candidate => candidate.DeviceName == draft.MonitorName) ??
        Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    void SyncDisplayPicker()
    {
        var previous = updating;
        updating = true;
        var displayOptions = Forms.Screen.AllScreens
            .Select((display, index) => new DisplayChoice(display, $"Display {index + 1}{(display.Primary ? " · Primary" : "")}"))
            .ToArray();
        displays.ItemsSource = displayOptions;
        displays.SelectedItem = displayOptions.FirstOrDefault(choice => choice.Screen.DeviceName == screen.DeviceName);
        updating = previous;
    }

    void UpdateScreenBounds()
    {
        DesktopInterop.SetScreenBounds(this, screen.WorkingArea);
        Dispatcher.BeginInvoke(new Action(ArrangeWidgets));
    }

    void ChooseDisplay()
    {
        if (updating || displays.SelectedItem is not DisplayChoice choice || choice.Screen.DeviceName == screen.DeviceName) return;
        NormalizeLayout();
        screen = choice.Screen;
        draft.MonitorName = screen.DeviceName;
        changed(draft);
        UpdateScreenBounds();
    }

    internal void DisplaysChanged()
    {
        screen = CurrentDisplay();
        SyncDisplayPicker();
        if (IsLoaded) UpdateScreenBounds();
    }

    void SelectInitialWidget()
    {
        selected = draft.Widgets.FirstOrDefault(item => item.Value.Enabled && !DesktopWidget.IsWallpaperSize(item.Key)).Key;
        Select(selected);
    }

    void Select(WidgetKind kind)
    {
        selected = kind;
        UpdateInspector();
        foreach (var (item, tile) in tiles)
        {
            bool current = item == selected;
            tile.Frame.BorderBrush = current ? Mint : Line;
            tile.Tag.Background = current ? Mint : Night;
            ((TextBlock)tile.Tag.Child).Foreground = current ? Ink : Paper;
            tile.Resize.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            System.Windows.Controls.Panel.SetZIndex(tile.Surface, current ? 1 : 0);
        }
        PositionInspector();
    }

    void UpdateInspector()
    {
        var options = draft.Widget(selected);
        updating = true;
        title.Text = draft.Name;
        choices.SelectedItem = choices.Items.OfType<WidgetChoice>().First(choice => choice.Kind == selected);
        enabled.IsChecked = options.Enabled;
        locked.IsChecked = options.Locked;
        var fixedSize = DesktopWidget.IsWallpaperSize(selected);
        locked.IsEnabled = !fixedSize;
        width.IsEnabled = height.IsEnabled = resizeButton.IsEnabled = !fixedSize && options.Enabled;
        opacity.IsEnabled = options.Enabled;
        linked.IsChecked = draft.LinkCoverAndLyrics;
        opacity.Value = Math.Clamp(options.Opacity, opacity.Minimum, opacity.Maximum);
        opacityLabel.Text = $"Opacity · {opacity.Value:P0}";
        UpdateSizeFields(options);
        updating = false;
    }

    void UpdateSizeFields(WidgetOptions options)
    {
        bool found = rectangles.TryGetValue(selected, out var bounds);
        width.Text = (found ? bounds.Width : options.Width).ToString("0", CultureInfo.CurrentCulture);
        height.Text = (found ? bounds.Height : options.Height).ToString("0", CultureInfo.CurrentCulture);
    }

    void PreviewPreset()
    {
        if (presets.SelectedItem is not string preset) return;
        draft = ScenePresets.Apply(preset, draft, Area);
        changed(draft);
        SelectInitialWidget();
        BuildPreviews();
        message.Text = $"{draft.Name} is being previewed.";
    }

    void SetEnabled(bool value)
    {
        if (updating) return;
        draft.Widget(selected).Enabled = value;
        changed(draft);
        BuildPreviews();
    }

    void SetLocked(bool value)
    {
        if (updating) return;
        draft.Widget(selected).Locked = value;
        changed(draft);
    }

    void SetLinked(bool value)
    {
        if (updating) return;
        draft.LinkCoverAndLyrics = value;
        changed(draft);
    }

    void SetOpacity()
    {
        if (updating) return;
        var options = draft.Widget(selected);
        options.Opacity = opacity.Value;
        opacityLabel.Text = $"Opacity · {opacity.Value:P0}";
        if (tiles.TryGetValue(selected, out var tile)) tile.Backing.Opacity = opacity.Value;
        if (backgrounds.TryGetValue(selected, out var view)) view.Opacity = opacity.Value;
        changed(draft);
    }

    Size Area => new(Math.Max(0, widgets.ActualWidth), Math.Max(0, widgets.ActualHeight));

    void ReleasePreviews()
    {
        foreach (var tile in tiles.Values) tile.View.Release();
        foreach (var view in backgrounds.Values) view.Release();
        tiles.Clear();
        backgrounds.Clear();
        rectangles.Clear();
        widgets.Children.Clear();
        desktop.Children.Clear();
    }

    void BuildPreviews()
    {
        ReleasePreviews();
        foreach (var (kind, options) in draft.Widgets.Where(item => item.Value.Enabled).OrderBy(item => Layer(item.Key)))
        {
            var view = new WidgetView(app, kind, false) { IsHitTestVisible = false, Opacity = Math.Clamp(options.Opacity, .08, 1) };
            if (DesktopWidget.IsWallpaperSize(kind))
            {
                backgrounds.Add(kind, view);
                desktop.Children.Add(view);
            }
            else
            {
                var tile = MakeTile(kind, view, options);
                tiles.Add(kind, tile);
                widgets.Children.Add(tile.Surface);
            }
        }
        ArrangeWidgets();
        Select(selected);
    }

    static int Layer(WidgetKind kind) => kind switch
    {
        WidgetKind.Wallpaper or WidgetKind.AlbumBackdrop => 0,
        WidgetKind.Environment => 1,
        _ => 2
    };

    Tile MakeTile(WidgetKind kind, WidgetView view, WidgetOptions options)
    {
        var surface = new Grid { ClipToBounds = true };
        var backing = new Border
        {
            Child = view,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(options.Surface * 239, 0, 239), 10, 21, 40)),
            CornerRadius = new CornerRadius(14),
            Opacity = view.Opacity
        };
        view.Opacity = 1;
        var frame = new Border { BorderThickness = new Thickness(1), BorderBrush = Line, CornerRadius = new CornerRadius(10), IsHitTestVisible = false };
        var move = MakeThumb(Cursors.SizeAll, Brushes.Transparent);
        var resize = MakeThumb(Cursors.SizeNWSE, Mint);
        resize.HorizontalAlignment = HorizontalAlignment.Right;
        resize.VerticalAlignment = VerticalAlignment.Bottom;
        resize.Width = resize.Height = 12;
        resize.Margin = new Thickness(0, 0, 5, 5);
        var tag = new Border
        {
            Child = Text(Dashboard.WidgetName(kind), 11, Paper, true),
            Background = Night,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        surface.Children.Add(backing);
        surface.Children.Add(frame);
        surface.Children.Add(tag);
        surface.Children.Add(move);
        surface.Children.Add(resize);
        AutomationProperties.SetName(move, $"Move {Dashboard.WidgetName(kind)}");
        AutomationProperties.SetName(resize, $"Resize {Dashboard.WidgetName(kind)}");
        BindGesture(move, kind, false);
        BindGesture(resize, kind, true);
        return new Tile(surface, backing, view, frame, resize, tag);
    }

    static Thumb MakeThumb(Cursor cursor, Brush fill)
    {
        var shape = new FrameworkElementFactory(typeof(Border));
        shape.SetValue(Border.BackgroundProperty, fill);
        shape.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        return new Thumb
        {
            Cursor = cursor,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = shape }
        };
    }

    void ArrangeWidgets()
    {
        var area = Area;
        if (area.Width < 1 || area.Height < 1) return;
        foreach (var view in backgrounds.Values)
        {
            view.Width = area.Width;
            view.Height = area.Height;
        }
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var origin = new Point(screen.WorkingArea.Left, screen.WorkingArea.Top);
        foreach (var kind in tiles.Keys)
            Place(kind, SceneLayout.Resolve(kind, draft.Widget(kind), area, origin, scale.M11, scale.M22));
        if (!updating) UpdateSizeFields(draft.Widget(selected));
        PositionInspector();
    }

    void Place(WidgetKind kind, Rect bounds)
    {
        var surface = tiles[kind].Surface;
        surface.Width = bounds.Width;
        surface.Height = bounds.Height;
        Canvas.SetLeft(surface, bounds.Left);
        Canvas.SetTop(surface, bounds.Top);
        rectangles[kind] = bounds;
    }

    void BindGesture(Thumb thumb, WidgetKind kind, bool resize)
    {
        thumb.DragStarted += (_, _) =>
        {
            if (!rectangles.TryGetValue(kind, out var initial)) return;
            Select(kind);
            var items = new Dictionary<WidgetKind, Rect> { [kind] = initial };
            var group = initial;
            if (!resize && draft.LinkCoverAndLyrics && kind is WidgetKind.Visualizer or WidgetKind.Lyrics)
            {
                var partner = kind == WidgetKind.Visualizer ? WidgetKind.Lyrics : WidgetKind.Visualizer;
                if (rectangles.TryGetValue(partner, out var paired))
                {
                    items.Add(partner, paired);
                    group.Union(paired);
                }
            }
            gesture = new Gesture(kind, Mouse.GetPosition(widgets), items, group, resize);
        };
        thumb.DragDelta += (_, _) => Drag();
        thumb.DragCompleted += (_, args) => EndGesture(args.Canceled);
    }

    void Drag()
    {
        if (gesture is not { } active) return;
        var delta = Mouse.GetPosition(widgets) - active.Pointer;
        var neighbors = rectangles.Where(item => !active.Start.ContainsKey(item.Key)).Select(item => item.Value);
        var area = Area;
        bool snap = (Keyboard.Modifiers & ModifierKeys.Alt) == 0;
        if (active.Resize)
        {
            var resized = SceneLayout.Resize(active.Group, delta, area, neighbors, snap);
            Place(active.Kind, resized.Bounds);
            ShowGuides(resized.X, resized.Y, area);
        }
        else
        {
            var wanted = active.Group;
            wanted.Offset(delta);
            var moved = SceneLayout.Move(wanted, area, neighbors, snap);
            var shift = moved.Bounds.Location - active.Group.Location;
            foreach (var (kind, start) in active.Start)
            {
                var movedItem = start;
                movedItem.Offset(shift);
                Place(kind, movedItem);
            }
            ShowGuides(moved.X, moved.Y, area);
        }
        UpdateSizeFields(draft.Widget(selected));
    }

    void EndGesture(bool cancelled)
    {
        guides.Children.Clear();
        if (gesture is not { } active) return;
        gesture = null;
        foreach (var (kind, start) in active.Start)
        {
            if (cancelled) Place(kind, start);
            else if (rectangles.TryGetValue(kind, out var bounds)) SceneLayout.Store(draft.Widget(kind), bounds, Area);
        }
        if (!cancelled) changed(draft);
        UpdateSizeFields(draft.Widget(selected));
        PositionInspector();
    }

    void ShowGuides(double? x, double? y, Size area)
    {
        guides.Children.Clear();
        if (x is { } vertical) guides.Children.Add(Guide(vertical, 0, vertical, area.Height));
        if (y is { } horizontal) guides.Children.Add(Guide(0, horizontal, area.Width, horizontal));
    }

    static GuideLine Guide(double x1, double y1, double x2, double y2) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = Mint,
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 5, 3 },
        Opacity = .8
    };

    void ApplySize()
    {
        if (!rectangles.TryGetValue(selected, out var bounds)) return;
        if (!double.TryParse(width.Text, out double w) || !double.TryParse(height.Text, out double h) ||
            !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
        {
            message.Text = "Enter a positive width and height.";
            return;
        }
        var resized = SceneLayout.ResizeTo(bounds, new Size(w, h), Area);
        var options = draft.Widget(selected);
        SceneLayout.Store(options, resized, Area);
        Place(selected, resized);
        changed(draft);
        UpdateSizeFields(options);
        PositionInspector();
        message.Text = "";
    }

    void NormalizeLayout()
    {
        var area = Area;
        if (area.Width < 1 || area.Height < 1) return;
        foreach (var (kind, bounds) in rectangles)
            SceneLayout.Store(draft.Widget(kind), bounds, area);
    }

    void Save(bool asProfile)
    {
        var label = asProfile ? name.Text.Trim() : null;
        if (asProfile && string.IsNullOrWhiteSpace(label))
        {
            message.Text = "Enter a name for the scene.";
            name.Focus();
            return;
        }
        NormalizeLayout();
        if (commit(draft, label)) Close();
        else message.Text = "Sada could not save this scene. Your draft is still open.";
    }

    internal void Render(AudioFrame frame)
    {
        foreach (var (kind, tile) in tiles)
            tile.View.Render(frame, kind is WidgetKind.Flock or WidgetKind.Fluid ? Mouse.GetPosition(tile.View) : NoPointer);
        foreach (var (kind, view) in backgrounds)
            view.Render(frame, kind == WidgetKind.CursorTrail ? Mouse.GetPosition(view) : NoPointer);
    }

    internal void RenderLyrics()
    {
        if (tiles.TryGetValue(WidgetKind.Lyrics, out var tile)) tile.View.RenderLyrics();
    }

    static readonly Point NoPointer = new(double.NaN, double.NaN);
}
