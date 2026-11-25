using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Sada;

internal sealed class DesktopWidget : Window
{
    readonly App app;
    readonly WidgetKind kind;
    readonly System.Drawing.Rectangle bounds;
    readonly bool fullscreen;
    readonly WidgetView view;
    readonly FullNeonOverlay? fullNeon;
    readonly Border shell;
    readonly System.Windows.Controls.Primitives.Thumb resize;
    Point? dragStart;
    bool dragged;
    readonly DispatcherTimer? neonClick;
    System.Drawing.Rectangle dragBounds;
    string lastPlacement = "";
    bool desktopAttached;

    public DesktopWidget(App app, WidgetKind kind, System.Drawing.Rectangle bounds, bool fullscreen = false)
    {
        this.app = app;
        this.kind = kind;
        this.bounds = bounds;
        this.fullscreen = fullscreen;
        Title = fullscreen ? "Sada · Full screen" : $"Sada · {Dashboard.WidgetName(kind)}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = !fullscreen || app.Scene.FullBackdrop == Backdrop.Transparent;
        Background = AllowsTransparency ? Brushes.Transparent : StudioUi.Night;
        ShowActivated = fullscreen;
        ShowInTaskbar = false;
        Topmost = fullscreen;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = fullscreen || IsWallpaperSize(kind) ? bounds.Width : app.Scene.Widget(kind).Width;
        Height = fullscreen || IsWallpaperSize(kind) ? bounds.Height : app.Scene.Widget(kind).Height;
        view = new WidgetView(app, kind, fullscreen);
        shell = new Border { Child = view };
        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(shell);
        resize = new() { Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE, Template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = ResizeGripVisual() } };
        resize.DragDelta += (_, args) =>
        {
            var options = app.Scene.Widget(kind);
            Height = options.Height = Math.Clamp(Height + args.VerticalChange, 90, 1260);
            Width = options.Width = Math.Clamp(Width + args.HorizontalChange, 140, 2000);
            app.QueueSave();
        };
        grid.Children.Add(resize);
        Content = grid;
        if (fullscreen)
        {
            resize.Visibility = Visibility.Collapsed;
            fullNeon = new FullNeonOverlay(app);
            grid.Children.Add(fullNeon);
            PreviewKeyDown += HandleKeys;
            Loaded += (_, _) => Keyboard.Focus(this);
        }
        else
        {
            if (kind == WidgetKind.Neon)
            {
                neonClick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime) };
                neonClick.Tick += async (_, _) =>
                {
                    neonClick.Stop();
                    if (app.Scene.Neon.ClickToPlay) await app.Media.ToggleAsync();
                };
            }
            grid.PreviewMouseLeftButtonDown += BeginDrag;
            grid.PreviewMouseMove += MoveDrag;
            grid.PreviewMouseLeftButtonUp += EndDrag;
            var menu = new ContextMenu();
            var dashboard = new MenuItem { Header = "Open Studio" };
            dashboard.Click += (_, _) => app.ShowDashboard();
            menu.Items.Add(dashboard);
            var lockItem = new MenuItem { Header = "Lock widget" };
            lockItem.Click += (_, _) => { app.Scene.Widget(kind).Locked = true; app.ApplyChanges(); };
            menu.Items.Add(lockItem);
            ContextMenu = menu;
        }
        DpiChanged += (_, _) =>
        {
            lastPlacement = "";
            Dispatcher.InvokeAsync(() => { if (IsVisible) Apply(); });
        };
        Closed += (_, _) =>
        {
            neonClick?.Stop();
            fullNeon?.Release();
            view.Release();
        };
    }

    static FrameworkElementFactory ResizeGripVisual()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty, "◢");
        text.SetValue(TextBlock.ForegroundProperty, StudioUi.Soft);
        text.SetValue(TextBlock.FontSizeProperty, 13d);
        return text;
    }

    public static bool IsWallpaperSize(WidgetKind kind) => kind is WidgetKind.Wallpaper or WidgetKind.AlbumBackdrop or WidgetKind.Environment or WidgetKind.ScreenGlow
        or WidgetKind.Snow or WidgetKind.CursorTrail or WidgetKind.Span;

    public Placement ScenePlacement => fullscreen ? Placement.OnTop : app.Scene.Widget(kind).Placement;

    public void Apply()
    {
        if (fullscreen)
        {
            DesktopInterop.SetScreenBounds(this, bounds);
            view.Apply();
            fullNeon?.Apply();
            return;
        }
        var options = app.Scene.Widget(kind);
        var fixedSize = IsWallpaperSize(kind);
        var clickThrough = fixedSize || options.Locked && !(kind == WidgetKind.Neon && app.Scene.Neon.ClickToPlay);
        var layout = $"{options.Placement}/{clickThrough}/{options.Left}/{options.Top}/{options.RelativeLeft}/{options.RelativeTop}/{options.Width}/{options.Height}";
        var needsPlacement = layout != lastPlacement;
        var wasAttached = desktopAttached;
        if (needsPlacement || options.Placement == Placement.Desktop && !DesktopInterop.IsDesktopAttached(this))
        {
            desktopAttached = DesktopInterop.SetPlacement(this, options.Placement, clickThrough);
            lastPlacement = layout;
            if (options.Placement == Placement.Desktop && !desktopAttached)
            {
                DesktopInterop.SetPlacement(this, Placement.Floating, clickThrough);
                if (needsPlacement || wasAttached) app.SetStatus("Windows desktop attachment is unavailable. Widget opened in a floating window.");
            }
            if (fixedSize) DesktopInterop.SetScreenBounds(this, bounds);
            else
            {
                var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                var area = new Size(bounds.Width / transform.M11, bounds.Height / transform.M22);
                var origin = new Point(bounds.Left, bounds.Top);
                var layoutBounds = SceneLayout.Resolve(kind, options, area, origin, transform.M11, transform.M22);
                Width = layoutBounds.Width;
                Height = layoutBounds.Height;
                DesktopInterop.SetScreenBounds(this, new System.Drawing.Rectangle(
                    bounds.X + (int)Math.Round(layoutBounds.X * transform.M11),
                    bounds.Y + (int)Math.Round(layoutBounds.Y * transform.M22),
                    (int)Math.Round(layoutBounds.Width * transform.M11),
                    (int)Math.Round(layoutBounds.Height * transform.M22)));
            }
        }
        Opacity = Math.Clamp(options.Opacity, .08, 1);
        var alpha = fixedSize ? (byte)0 : (byte)Math.Clamp(options.Surface * 239, 0, 239);
        shell.Background = new SolidColorBrush(Color.FromArgb(alpha, 10, 21, 40));
        shell.CornerRadius = fixedSize ? new CornerRadius(0) : new CornerRadius(14);
        resize.Visibility = options.Locked || fixedSize ? Visibility.Collapsed : Visibility.Visible;
        view.Apply();
    }

    void BeginDrag(object sender, MouseButtonEventArgs args)
    {
        if (IsWallpaperSize(kind) || IsInteractive(args.OriginalSource as DependencyObject)) return;
        if (app.Scene.Widget(kind).Locked && !(kind == WidgetKind.Neon && app.Scene.Neon.ClickToPlay)) return;
        if (kind == WidgetKind.Neon && args.ClickCount == 2)
        {
            neonClick?.Stop();
            app.ShowNeonSettings();
            args.Handled = true;
            return;
        }
        dragged = false;
        dragStart = new Point(Forms.Cursor.Position.X, Forms.Cursor.Position.Y);
        var corner = PointToScreen(new Point());
        var end = PointToScreen(new Point(ActualWidth, ActualHeight));
        dragBounds = new((int)corner.X, (int)corner.Y, (int)(end.X - corner.X), (int)(end.Y - corner.Y));
        Mouse.Capture((IInputElement)sender);
    }

    static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or Slider or System.Windows.Controls.Primitives.Thumb) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : null;
        }
        return false;
    }

    void MoveDrag(object sender, MouseEventArgs args)
    {
        if (dragStart is not { } start || args.LeftButton != MouseButtonState.Pressed) return;
        var mouse = Forms.Cursor.Position;
        if (kind == WidgetKind.Neon)
        {
            dragged |= Math.Abs(mouse.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(mouse.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
            if (!dragged || app.Scene.Widget(kind).Locked) return;
        }
        var moved = dragBounds with { X = (int)(dragBounds.X + mouse.X - start.X), Y = (int)(dragBounds.Y + mouse.Y - start.Y) };
        DesktopInterop.SetScreenBounds(this, moved);
    }

    void EndDrag(object sender, MouseButtonEventArgs args)
    {
        if (dragStart is null) return;
        dragStart = null;
        ReleaseMouseCapture();
        if (Mouse.Captured == sender) Mouse.Capture(null);
        if (kind == WidgetKind.Neon)
        {
            if (!dragged && app.Scene.Neon.ClickToPlay)
            {
                neonClick?.Stop();
                neonClick?.Start();
            }
            if (!dragged || app.Scene.Widget(kind).Locked) return;
        }
        var position = PointToScreen(new Point());
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var options = app.Scene.Widget(kind);
        SceneLayout.Store(options,
            new Rect((position.X - bounds.X) / transform.M11, (position.Y - bounds.Y) / transform.M22, Width, Height),
            new Size(bounds.Width / transform.M11, bounds.Height / transform.M22));
        Apply();
        app.QueueSave();
    }

    async void HandleKeys(object sender, KeyEventArgs args)
    {
        switch (args.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Space: await app.Media.ToggleAsync(); break;
            case Key.Right: await app.Media.NextAsync(); break;
            case Key.Left: await app.Media.PreviousAsync(); break;
            case Key.V: view.ToggleVisual(); break;
            case Key.C: view.TogglePalette(); break;
            default: return;
        }
        args.Handled = true;
    }

    public void Render(AudioFrame frame)
    {
        if (!IsVisible) return;
        view.Render(frame, kind is WidgetKind.CursorTrail or WidgetKind.Flock or WidgetKind.Fluid ? DesktopInterop.Pointer(this) : new(double.NaN, double.NaN));
        fullNeon?.Render(frame);
    }

    public void RenderLyrics()
    {
        if (IsVisible && (fullscreen || kind == WidgetKind.Lyrics)) view.RenderLyrics();
    }
}
